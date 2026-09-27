using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shell;
using Perisc.Safety;
using Prt.App.Models;
using Prt.App.Rendering;
using Prt.App.Views;
using Prt.Core;
using Prt.Core.Diagnostics;
using Prt.Core.Export;
using Prt.Core.Rendering;
using Prt.Core.Syntax;

namespace Prt.App.Services;

/// <summary>自检用例组：申报异步 / 超时 / 挂钩隔离 / 释放契约（<see cref="SelfTest"/> 的 partial）。</summary>
internal static partial class SelfTest
{
    /// <summary>自检用例组：申报异步 / 超时 / 挂钩隔离 / 释放契约。</summary>
    private static async Task RunSafetyCasesAsync(CaseRunner runner)
    {
        // 40. 申报异步化：调用线程只为"任务"等待，不为"线程"等待（D-13 / D-14）。
        //     回归点——改造前 AskAsync 用 Task.Run 把同步内核塞进线程池，界面侧还要在 UI 线程上
        //     跑嵌套消息泵（Dispatcher.PushFrame）才能等它返回；界面在等待期间被占住。
        //
        //     本用例起一个隔离环境并把审计落点指到临时目录：自检反复建/拆审计存储，
        //     落点必须避开用户真实数据（与 AuditStore 自检构造的同一个理由）。
        await runner.CheckAsync("申报异步：调用线程不为界面挂钩让出等待（D-13）", () => SafetyCase40());

        // 41. 申报超时：挂钩一直不答复时按 Deny + Timeout 返回（4.3 第 3 条）。
        //     异步化最容易丢掉的就是这条语义——把超时从 Task.Wait 改成 CancelAfter 之后必须重新证明它成立。
        await runner.CheckAsync("申报超时：挂钩不答复按 Deny + Timeout（4.3 第 3 条）", () => SafetyCase41());

        // 42. 界面挂钩按客户端隔离（D-07 的用例）。
        //     回归点——挂钩原先是 public static 属性，两个 SafetyClient 只能共用一组 handler；
        //     这一条用例在那种设计下**根本写不出来**，本身就是问题的证明。
        await runner.CheckAsync("界面挂钩按客户端隔离（D-07）", () => SafetyCase42());

        // 43. 运行时引用计数：任一份持有归还不得关掉共享审计，最后一份归还必须关掉（2.3 / D-12）。
        //     回归点——改造前运行时是永不拆除的静态单例，"提前拆"与"永不拆"这两个方向都无从验证；
        //     引用计数把两者都变成可断言的，也把"审计补写定时器靠进程退出兜底"这件事收进可验证范围。
        runner.Check("运行时释放契约：多持有者不提前拆除、最后一份归还即拆（2.3 / D-12）", () => SafetyCase43());

        // 44. 守护客户端释放契约（D-11b / D-11d）：释放是收尾动作，必须幂等、必须不抛。
        //     回归点——改造前 Dispose 是"取消令牌 → 立刻 Dispose 管道与令牌"，
        //     接收循环与心跳循环从来没人等过，且可能在 Task.Delay(ms, token) 注册回调时
        //     撞上已 Dispose 的令牌（ObjectDisposedException）。现在顺序是
        //     "置标记 → 取消 → 等在途循环退出（有上限）→ 处置管道 → 处置令牌"。
        await runner.CheckAsync("守护客户端释放：幂等、未连接时不抛（D-11b / D-11d）", () => SafetyCase44());
    }

    // ───────────────────────────── 本组用例的主体 ─────────────────────────────
    /// <summary>用例 40 的主体：申报异步化后，调用线程只为"任务"等待（D-13）。</summary>
    private static async Task<string> CaseD13Async()
    {
        var host = new SafetyUiHost
        {
            // 挂钩刻意"过一会儿才答复"：同步等待的实现会在这里把调用线程占住 150 ms。
            AskHandler = async (requests, _) =>
            {
                // 刻意不传播令牌：这 150 ms 正是本用例要制造的"答复迟到"场景，
                // 跟随取消令牌会让用例在取消时提前结束，就测不到待测行为。
                await Task.Delay(150, CancellationToken.None).ConfigureAwait(false);
                return requests.Select(_ => new SafetyUiHost.Answer(allowed: true)).ToList();
            },
        };

        using var client = SafetyClient.Create(new SafetyOptions(
            autoRecordDecisions: false,
            programId: "Prt.App.selftest.async",
            uiHost: host));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var task = client.AskAsync(new AskRequest("PRIV-01", @"C:\selftest\async.prt", "验证异步申报"));
        var returnMs = watch.Elapsed.TotalMilliseconds;

        if (returnMs > 60)
        {
            throw new InvalidOperationException(
                $"AskAsync 在调用线程上等了 {returnMs:0} ms——同步等待没有清除（D-13）");
        }

        var decision = await task;
        if (!decision.IsAllowed)
        {
            throw new InvalidOperationException($"挂钩已允许，裁决却为 {decision.Status}：{decision.Detail}");
        }

        return $"调用线程立即返回（{returnMs:0.0} ms），未等待挂钩的 150 ms";
    }

    /// <summary>用例 42 的主体：界面挂钩按客户端实例隔离（D-07）。</summary>
    private static async Task<string> CaseD07Async()
    {
        var hostAllow = new SafetyUiHost
        {
            AskHandler = (requests, _) => Task.FromResult<IReadOnlyList<SafetyUiHost.Answer?>>(
                requests.Select(_ => new SafetyUiHost.Answer(allowed: true)).ToList()),
        };
        var hostDeny = new SafetyUiHost
        {
            AskHandler = (requests, _) => Task.FromResult<IReadOnlyList<SafetyUiHost.Answer?>>(
                requests.Select(_ => new SafetyUiHost.Answer(allowed: false)).ToList()),
        };

        using var allowClient = SafetyClient.Create(new SafetyOptions(
            autoRecordDecisions: false, programId: "Prt.App.selftest.async", uiHost: hostAllow));
        using var denyClient = SafetyClient.Create(new SafetyOptions(
            autoRecordDecisions: false, programId: "Prt.App.selftest.async", uiHost: hostDeny));

        // 两个客户端共享同一份许可存储（2.3），因此必须用不同的对象文本，
        // 否则后一个会命中前一个已经授予的许可缓存，测不出挂钩差异。
        var allowed = await allowClient.AskAsync(new AskRequest("PRIV-01", @"C:\selftest\isolate-a.prt", "验证挂钩隔离"));
        var denied = await denyClient.AskAsync(new AskRequest("PRIV-01", @"C:\selftest\isolate-b.prt", "验证挂钩隔离"));

        if (!allowed.IsAllowed)
        {
            throw new InvalidOperationException($"允许型挂钩的客户端却得到 {allowed.Status}：{allowed.Detail}");
        }
        if (denied.IsAllowed)
        {
            throw new InvalidOperationException("拒绝型挂钩的客户端却得到 Allow——两个客户端共用了同一组挂钩");
        }

        return "同一进程内两个客户端各自命中自己的挂钩（允许 / 拒绝），互不串扰";
    }

    /// <summary>用例 40：申报异步：调用线程不为界面挂钩让出等待（D-13）</summary>
    private static async Task<string> SafetyCase40()
    {
            var auditDir = Path.Combine(Path.GetTempPath(), "prt-selftest-d13-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (SafetyEnvironment.UseIsolated(new MonotonicClock(), auditDirectory: auditDir))
                {
                    return await CaseD13Async();
                }
            }
            finally
            {
                DeleteTreeQuietly(auditDir);
            }
    }

    /// <summary>用例 41：申报超时：挂钩不答复按 Deny + Timeout（4.3 第 3 条）</summary>
    private static async Task<string> SafetyCase41()
    {
            var auditDir = Path.Combine(Path.GetTempPath(), "prt-selftest-43-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (SafetyEnvironment.UseIsolated(new MonotonicClock(), auditDirectory: auditDir))
                {
                    var host = new SafetyUiHost
                    {
                        // 一直不答复，直到库侧取消令牌触发。
                        AskHandler = async (requests, token) =>
                        {
                            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                            return new SafetyUiHost.Answer?[requests.Count];
                        },
                    };

                    using var client = SafetyClient.Create(new SafetyOptions(
                        askTimeout: TimeSpan.FromMilliseconds(200),
                        autoRecordDecisions: false,
                        programId: "Prt.App.selftest.async",
                        uiHost: host));

                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var decision = await client.AskAsync(new AskRequest("PRIV-01", @"C:\selftest\timeout.prt", "验证申报超时"));
                    watch.Stop();

                    if (decision.Status != DecisionStatus.Deny || decision.Code != PssCode.Timeout)
                    {
                        throw new InvalidOperationException(
                            $"挂钩不答复时应为 Deny + Timeout，实际 {decision.Status} + {decision.Code}");
                    }
                    if (watch.Elapsed.TotalMilliseconds > 3000)
                    {
                        throw new InvalidOperationException($"超时未在限时内生效（耗时 {watch.Elapsed.TotalMilliseconds:0} ms）");
                    }

                    return $"挂钩不答复 → {decision.Code}（耗时 {watch.Elapsed.TotalMilliseconds:0} ms，限时 200 ms）";
                }
            }
            finally
            {
                DeleteTreeQuietly(auditDir);
            }
    }

    /// <summary>用例 42：界面挂钩按客户端隔离（D-07）</summary>
    private static async Task<string> SafetyCase42()
    {
            var auditDir = Path.Combine(Path.GetTempPath(), "prt-selftest-d07-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (SafetyEnvironment.UseIsolated(new MonotonicClock(), auditDirectory: auditDir))
                {
                    return await CaseD07Async();
                }
            }
            finally
            {
                DeleteTreeQuietly(auditDir);
            }
    }

    /// <summary>用例 43：运行时释放契约：多持有者不提前拆除、最后一份归还即拆（2.3 / D-12）</summary>
    private static string SafetyCase43()
    {
            var auditDir = Path.Combine(Path.GetTempPath(), "prt-selftest-ref-" + Guid.NewGuid().ToString("N"));
            Fixture(SafeRuntime.File.CreateDirectory(auditDir, "创建自检隔离审计目录"));
            try
            {
                // pinned: false —— 允许运行时按引用计数自动拆除，这正是本用例要验的语义。
                using (SafetyEnvironment.UseIsolated(
                           new ManualClock(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.FromHours(8))),
                           pinned: false,
                           auditDirectory: auditDir))
                {
                    var first = SafetyEnvironment.Acquire();
                    first.EnsureAudit(AuditFailurePolicy.Buffer, auditDir);
                    var second = SafetyEnvironment.Acquire();
                    if (!ReferenceEquals(first, second))
                    {
                        throw new InvalidOperationException("同一进程内两次 Acquire 拿到了不同实例——违反 2.3");
                    }
                    if (first.Audit is null)
                    {
                        throw new InvalidOperationException("Acquire 之后审计存储未建立");
                    }

                    // 归还一份：仍有持有者在，运行时与审计都必须还在。
                    SafetyEnvironment.Release(first);
                    var third = SafetyEnvironment.Acquire();
                    if (!ReferenceEquals(third, first))
                    {
                        throw new InvalidOperationException(
                            "仍有持有者时提前拆除了运行时——一个客户端注销把另一个客户端的审计一并关掉了（2.3）");
                    }
                    if (first.Audit is null)
                    {
                        throw new InvalidOperationException("归还一份后审计存储被放下（应等最后一个持有者）");
                    }

                    // 全部归还：最后一份离开即拆除，审计存储被放下（补写定时器随之停止）。
                    SafetyEnvironment.Release(second);
                    SafetyEnvironment.Release(third);
                    if (first.Audit is not null)
                    {
                        throw new InvalidOperationException(
                            "最后一份持有归还后审计存储未被放下——补写定时器会一直挂着（D-12）");
                    }

                    // 拆除后再取：必须是一个**新**实例，而不是已拆除的那个。
                    var rebuilt = SafetyEnvironment.Acquire();
                    if (ReferenceEquals(rebuilt, first))
                    {
                        throw new InvalidOperationException("拆除后 Acquire 返回了已拆除的实例");
                    }
                    SafetyEnvironment.Release(rebuilt);

                    return "两份持有互不干扰；最后一份归还后审计存储被放下，再次 Acquire 得到新实例";
                }
            }
            finally
            {
                DeleteTreeQuietly(auditDir);
            }
    }

    /// <summary>用例 44：守护客户端释放：幂等、未连接时不抛（D-11b / D-11d）</summary>
    private static async Task<string> SafetyCase44()
    {
            var auditDir = Path.Combine(Path.GetTempPath(), "prt-selftest-guard-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (SafetyEnvironment.UseIsolated(new MonotonicClock(), auditDirectory: auditDir))
                {
                    // 指向一个不存在的管道端点：guard 为可选增强，连不上必须如实返回未连接实例。
                    var options = new SafeGuardOptions(pipeName: @"\\.\pipe\Perisc.Safety.Selftest.NoSuchPipe.v1");
                    using var guard = await SafeGuardClient.ConnectAsync("Prt.App.selftest.guard", options);

                    if (guard.IsConnected)
                    {
                        throw new InvalidOperationException("端点不存在，连接状态却为已连接");
                    }
                    if (guard.ProtocolVersion is not null)
                    {
                        throw new InvalidOperationException("未连接的客户端不应有协商版本（4.6）");
                    }

                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    guard.Dispose();
                    guard.Dispose(); // 幂等：第二次不得抛
                    watch.Stop();

                    if (watch.Elapsed.TotalMilliseconds > 1500)
                    {
                        throw new InvalidOperationException(
                            $"释放耗时 {watch.Elapsed.TotalMilliseconds:0} ms——在途循环没有被及时取消");
                    }

                    return $"未连接实例释放两次均无异常（耗时 {watch.Elapsed.TotalMilliseconds:0} ms）";
                }
            }
            finally
            {
                DeleteTreeQuietly(auditDir);
            }
    }
}
