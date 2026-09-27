using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Perisc.Safety;

// 心跳：周期上报、连接丢失判定与事件（SafeGuardClient 的 partial；D-06）
// 拆分依据：《工程改进方案》§4.8 / CRS 9.6.4 D-06；拆分**只做位置迁移**，未改行为。
public sealed partial class SafeGuardClient : IDisposable
{
    // ───────────────────────────── 心跳 ─────────────────────────────

    /// <summary>单次心跳（4.6）。建议用 <see cref="StartHeartbeatAsync"/> 自动维持。</summary>
    public PssResult Heartbeat(string programId, string versionHash)
    {
        if (string.IsNullOrWhiteSpace(programId))
        {
            return new PssResult(PssCode.BadArg, "programId 不能为空白");
        }
        if (string.IsNullOrWhiteSpace(versionHash))
        {
            return new PssResult(PssCode.BadArg, "versionHash 不能为空白（2.4：哈希判定的唯一数据源）");
        }
        if (!IsConnected || _disposed)
        {
            return new PssResult(PssCode.GuardUnavailable, "守护服务未连接", degraded: true);
        }
        var reply = RunSync(() => HeartbeatAsync(programId.Trim(), versionHash.Trim(), _heartbeatIntervalMs, _cts.Token));
        return ToPssResult(reply, "心跳已送达");
    }

    /// <summary>启动（或重设）周期心跳（4.6）：默认 5 秒，上限 30 秒（12.2）。</summary>
    public Task StartHeartbeatAsync(TimeSpan? interval = null, CancellationToken cancellationToken = default)
    {
        var requested = interval ?? _options.HeartbeatInterval ?? TimeSpan.FromMilliseconds(DefaultHeartbeatMs);
        _heartbeatIntervalMs = Math.Clamp((int)requested.TotalMilliseconds, MinHeartbeatMs, MaxHeartbeatMs);
        _heartbeatRequested = true;
        if (IsConnected && !_disposed)
        {
            StartHeartbeatLoopLocked();
        }
        return Task.CompletedTask;
    }

    private void StartHeartbeatLoopLocked()
    {
        _heartbeatCts?.Cancel();
        _heartbeatCts?.Dispose();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _heartbeatCts = linked;
        _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(linked.Token));
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMilliseconds(_heartbeatIntervalMs);
        while (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                var seq = Interlocked.Increment(ref _seq);
                var reply = await HeartbeatAsync(_programId, VersionHash, _heartbeatIntervalMs, cancellationToken, seq)
                    .ConfigureAwait(false);
                if (reply.TransportFailed)
                {
                    await OnConnectionLostAsync().ConfigureAwait(false);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                await OnConnectionLostAsync().ConfigureAwait(false);
                return;
            }
        }
    }

    private async Task<RpcReply> HeartbeatAsync(string programId, string versionHash, int timeoutMs,
                                                CancellationToken cancellationToken, int? seq = null)
    {
        // 5.2 第 ⑤ 条：guard.heartbeat 的参数为 programId、versionHash。
        var parameters = new Dictionary<string, object>
        {
            ["programId"] = programId,
            ["versionHash"] = versionHash,
        };
        if (seq is not null)
        {
            parameters["seq"] = seq.Value; // 实现级附加字段（服务端可选读，非标准字段）
        }
        return await CallAsync("guard.heartbeat", parameters, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    private async Task OnConnectionLostAsync()
    {
        if (IsConnected)
        {
            IsConnected = false;
        }
        ProtocolVersion = null;
        _runtime.SetGuardUnavailable(true);
        try
        {
            _runtime.Audit?.TryAppend(new AuditRecord(
                _runtime.ProgramId, action: string.Empty, target: "guard-channel",
                AuditDecision.Escalation, PssCode.GuardUnavailable, AuditLayer.Arm,
                detail: "guard-unavailable：与守护服务的连接中断，模块状态降级"));
        }
        catch
        {
            // 吞掉的是"连接中断的留痕写不进"；降级到"内存状态已置 GuardUnavailable"（上一行已完成），
            // 模块状态与事件回调照常，只少了这一条审计记录。
            // 何时应传播：不需要——通道健康度由 2.6.1 的失败策略独立跟踪，
            // 在此抛出会把一次"降级"升级成回调链断裂。
        }

        var handler = ConnectionLost;
        handler?.Invoke(this, EventArgs.Empty);

        // 重连循环：每 5 秒尝试一次，直到释放（2.6：可继续运行，但不得静默假装仍有守护）。
        while (!_disposed && !_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (await TryConnectAsync(_cts.Token).ConfigureAwait(false))
            {
                return;
            }
        }
    }
}
