using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Perisc.Safety;

// 释放契约与在途循环的有界等待（SafeGuardClient 的 partial；D-06）
// 拆分依据：《工程改进方案》§4.8 / CRS 9.6.4 D-06；拆分**只做位置迁移**，未改行为。
public sealed partial class SafeGuardClient : IDisposable
{
    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                return false;
            }
            read += n;
        }
        return true;
    }

    /// <summary>
    /// 同步等待异步实现的通用写法（4.10 第 9 条允许同步完成）。
    /// <para>
    /// <paramref name="work"/> 恒在**专用线程**上执行：调用方可能是 UI 线程，若直接在
    /// 调用线程上取得 Task 再等结果，就会与消息泵互锁——这正是本方法存在的理由。
    /// </para>
    /// <para>
    /// 这里不用 <c>Task.Run</c>：RunSync 的调用点多处在「线程池任务里再同步等一个任务」
    /// 的嵌套位置（心跳循环、收尾等待都走这条路），线程池一旦饥饿，**等待本身**也会被
    /// 排进线程池而一起饿死。专用线程不受线程池调度影响，等待必然在预期时间内结束。
    /// </para>
    /// </summary>
    private static T RunSync<T>(Func<Task<T>> work)
    {
        var result = default(T)!;
        ExceptionDispatchInfo? failure = null;
        using var done = new ManualResetEventSlim(initialState: false);

        var worker = new Thread(() =>
        {
            try
            {
                result = work().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // 捕获而非直接抛：此刻还在子线程的栈上，抛出会变成未处理异常把进程带崩。
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                done.Set();
            }
        })
        {
            IsBackground = true,
            Name = "safeguard-runsync",
        };

        worker.Start();
        done.Wait();
        worker.Join();

        failure?.Throw();
        return result;
    }

    private async Task TeardownAsync()
    {
        IsConnected = false;
        _handshakeCompleted = false;
        ProtocolVersion = null;
        var pipe = _pipe;
        _pipe = null;
        pipe?.Dispose();
        // _writeLock 不在此释放，也不在 Dispose 里释放：SemaphoreSlim 是纯托管对象
        // （未访问 AvailableWaitHandle 时不申请非托管句柄），释放它没有任何收益，
        // 却会给"已通过闸门、正要 Release()"的在途 SendAsync 制造一个新的 ObjectDisposedException。
        // 这条规则全仓一致（见《设计取舍》：纯托管闸门不释放）；真正需要释放的是管道与取消令牌。
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// 释放：**先把在途工作停干净，再放资源**（9.6.4 / D-11a、D-12）。
    /// <para>
    /// 顺序是本方法唯一重要的事，改造前的写法（取消 → 立刻 Dispose 管道与令牌）有三个问题：
    /// </para>
    /// <list type="number">
    /// <item>接收循环与心跳循环仍在跑，却已经踩在已释放的管道和已 Dispose 的
    /// <see cref="CancellationTokenSource"/> 上——前者靠 catch 吞掉，后者会在
    /// <c>Task.Delay(ms, token)</c> 注册回调时抛 <c>ObjectDisposedException</c>（真正的竞态缺陷）；</item>
    /// <item>两个循环从来没人等过，所以"Dispose 返回后本对象彻底静止"这句话根本不成立；</item>
    /// <item>终止通知的等待方（<see cref="_pending"/>）只被塞了失败结果，但等待方可能正被
    /// 接收循环完成——两边都在写同一个 <c>TaskCompletionSource</c>，顺序不确定。</item>
    /// </list>
    /// <para>
    /// 现在：置释放标记 → 取消令牌（同时让重连循环的 5 秒等待立即返回）→
    /// 等在途循环退出（有上限，正常情况下立即返回）→ 处置管道 → 处置令牌。
    /// </para>
    /// <para>
    /// 上限是必要的：本方法可能在 UI 线程的退出流程里被调用，磁盘/管道卡住时不能把界面一起拖住。
    /// 超过上限即放弃等待——此时残留的只是一个会在下一次读/写时因令牌取消而退出的循环，
    /// 它不会再触碰已释放的管道（管道在等待之后才处置）。
    /// </para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _cts.Cancel();
        _heartbeatCts?.Cancel();

        WaitLoopsBounded(ShutdownWaitMs);

        _heartbeatCts?.Dispose();
        _heartbeatCts = null;

        foreach (var tcs in _pending.Values)
        {
            tcs.TrySetResult(RpcReply.Failed("客户端已释放"));
        }

        _pipe?.Dispose();
        _pipe = null;
        _cts.Dispose();

        // 最后归还运行时持有（D-12）：此时管道与两个循环都已处置完毕，
        // 不会再有代码去碰 _runtime，归还后它是否被拆除由 SafetyEnvironment 按引用计数决定。
        SafetyEnvironment.Release(_runtime);
    }

    /// <summary>等待接发与心跳两个循环退出，总等待不超过 <paramref name="timeoutMs"/>。</summary>
    private void WaitLoopsBounded(int timeoutMs)
    {
        var loops = new List<Task>(2);
        if (_receiveLoop is not null)
        {
            loops.Add(_receiveLoop);
        }
        if (_heartbeatLoop is not null)
        {
            loops.Add(_heartbeatLoop);
        }
        if (loops.Count == 0)
        {
            return;
        }
        try
        {
            // 不用 Task.WaitAll(..., timeout) 的返回值作判定：这里等不到也不改变后续处置，
            // 能等到就等，等不到就走——所有后续动作（处置管道 / 令牌）都已由取消令牌保底。
            _ = Task.WaitAll(loops.ToArray(), timeoutMs);
        }
        catch (AggregateException)
        {
            // 吞掉的是"循环退出时抛出的异常"（管道被关闭、帧损坏等）。
            // 循环内部已经各自把异常折算成"按失联处理"并写进审计，这里再抛一次只是重复扰动调用方
            // （Dispose 的调用方是退出流程，抛异常会中断收尾）。
            // 何时应传播：不需要——释放是收尾动作，它的失败不应阻止收尾继续。
        }
    }
}
