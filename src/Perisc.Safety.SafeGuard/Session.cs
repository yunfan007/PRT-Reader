using System.IO.Pipes;
using System.Text.Json;

namespace Perisc.Safety.SafeGuard;

/// <summary>
/// 一个已连接程序（守护对象）的会话：线协议分发（7.2 方法表）。
/// <para>
/// 心跳判定与终止处置<b>不在这里</b>：它们属于宿主启动器侧的监督职责，集中在
/// <see cref="GuardSupervisor"/>（3.4 的处置时序、6.2.2 的宽限重探都要求跨会话的一致判定，
/// 分散到每个会话会出现"同一程序两条连接给出两种结论"）。
/// 本类只负责：解析帧 → 分发 → 把心跳与版本哈希转交监督者 → 发送 terminate 通知。
/// </para>
/// 版本协商（7.2 第 ⑧ 条）：主版本不一致 → 回 <c>ApiMismatch</c> 并关闭连接。
/// </summary>
internal sealed class Session
{
    private readonly NamedPipeServerStream _pipe;
    private readonly GuardStore _store;
    private readonly GuardSupervisor? _supervisor;
    private readonly Action<Session>? _onEnded;
    private readonly Action? _onProgramConnected;
    private readonly CancellationToken _cancellationToken;

    private int _heartbeatIntervalMs = 5000;
    private bool _closeAfterReply;
    private string _programId = "unknown-program";
    private string _versionHash = string.Empty;
    private string _mode = "native";
    private int _pid;

    /// <param name="onProgramConnected">
    /// 接入成功（本会话完成 guard.connect）后的通知挂钩，宿主用它收掉启动界面。
    /// <b>只做界面收尾</b>：它不参与任何裁决，改写不了应答，也决定不了是否放行。
    /// 回调**不得抛异常**——接入应答不能被界面拖累（见 <see cref="GuardSplash"/> 的"公开入口不抛异常"）。
    /// </param>
    public Session(NamedPipeServerStream pipe, GuardStore store, Action<Session>? onEnded,
                   CancellationToken cancellationToken, GuardSupervisor? supervisor = null,
                   Action? onProgramConnected = null)
    {
        _pipe = pipe;
        _store = store;
        _supervisor = supervisor;
        _onEnded = onEnded;
        _onProgramConnected = onProgramConnected;
        _cancellationToken = cancellationToken;
    }

    public string ProgramId => _programId;
    public int Pid => _pid;

    /// <summary>
    /// 会话主循环：读帧 → 分发；退出时收尾。
    /// <para>这里<b>没有</b>周期性的心跳检查定时器：心跳是否丢失由监督者按单调计时判定，
    /// 会话只在上报时把时刻转过去（3.4 三：由子进程主动上报，父进程不轮询）。</para>
    /// </summary>
    public async Task RunAsync()
    {
        try
        {
            while (!_cancellationToken.IsCancellationRequested && _pipe.IsConnected && !_closeAfterReply)
            {
                var payload = await Wire.ReadFrameAsync(_pipe, _cancellationToken).ConfigureAwait(false);
                if (payload is null)
                {
                    break;
                }
                await DispatchAsync(payload).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 吞掉的是"服务被要求停机时读取被取消"这一**预期信号**；降级到"由 finally 的 End() 收尾"。
            // 何时应传播：不需要——停机是控制流而不是故障（下面那个 catch 才是真正的异常断开）。
        }
        catch
        {
            // 客户端异常断开：按 DISCONNECTED 收尾。
        }
        finally
        {
            End();
        }
    }

    private async Task DispatchAsync(byte[] payload)
    {
        if (!Wire.TryParseRequest(payload, out var id, out var method, out var parameters, out var protocolVersion))
        {
            // 帧长度非法 / JSON 无法解析 → 关闭连接（5.2 第 ② 条）。
            _closeAfterReply = true;
            return;
        }
        if (!Wire.ProtocolMajorMatches(protocolVersion))
        {
            // 主版本不一致 → ApiMismatch 并关闭连接（5.2 第 ⑧ 条）。
            _closeAfterReply = true;
            await ReplyAsync(Wire.Err(id, GuardCode.ApiMismatch,
                $"线协议主版本不一致：服务端 {Wire.ServerProtocolVersion}，客户端 {protocolVersion}")).ConfigureAwait(false);
            return;
        }

        object reply;
        switch (method)
        {
            case "guard.connect":
                reply = HandleConnect(id, parameters);
                break;
            case "guard.heartbeat":
                reply = HandleHeartbeat(id, parameters);
                break;
            case "guard.declareWhitelist":
                reply = HandleDeclareWhitelist(id, parameters);
                break;
            case "guard.explain":
                reply = HandleExplain(id, parameters);
                break;
            default:
                // 方法未知 → Internal（5.2 第 ⑨ 条；不得自造码）。
                reply = Wire.Err(id, GuardCode.Internal, $"未知方法：{method}");
                break;
        }
        await ReplyAsync(reply).ConfigureAwait(false);
    }

    private async Task ReplyAsync(object reply)
    {
        try
        {
            await Wire.WriteFrameAsync(_pipe, Wire.Serialize(reply), _cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _closeAfterReply = true;
            throw new IOException("应答写出失败");
        }
    }

    // ───────────────────────────── 方法实现 ─────────────────────────────

    /// <summary>guard.connect：参数 programId / versionHash / mode（5.2 第 ⑤ 条）。</summary>
    private object HandleConnect(string id, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            return Wire.Err(id, GuardCode.BadArg, "参数缺失或类型不符：params 必须为对象");
        }

        var programId = Wire.Str(parameters, "programId");
        if (string.IsNullOrWhiteSpace(programId))
        {
            return Wire.Err(id, GuardCode.BadArg, "参数缺失：programId");
        }

        // versionHash 是 HASH-MISMATCH 判定与自更新白名单基准的唯一数据源（2.4），缺失即拒绝接入。
        var versionHash = Wire.Str(parameters, "versionHash");
        if (string.IsNullOrWhiteSpace(versionHash))
        {
            return Wire.Err(id, GuardCode.BadArg, "参数缺失：versionHash（2.4：哈希与白名单判定所需）");
        }

        var mode = Wire.Str(parameters, "mode");
        if (mode is not ("native" or "bridged"))
        {
            // 3.7：接入模式只有 A（native）与 B（bridged）两种，"只接一部分层"不是合法取值。
            return Wire.Err(id, GuardCode.BadArg, "参数取值非法：mode 必须是 native / bridged（3.7、7.2 第 ⑥ 条）");
        }

        _programId = programId;
        _versionHash = versionHash;
        _mode = mode;
        _pid = Wire.Int(parameters, "pid") ?? 0;
        var interval = Wire.Int(parameters, "heartbeatIntervalMs");
        if (interval is not null)
        {
            _heartbeatIntervalMs = Math.Clamp(interval.Value, 500, 30000); // 上限 30 s（2.1）
        }

        // 登记到监督者：此后心跳与终止由它统一判定（3.4、6.2.2）。
        // 通知窗口取程序声明值，实际生效值由监督者按 min(声明值, 宿主策略值, 2000) 收紧。
        _supervisor?.Register(_programId, _pid,
            TimeSpan.FromMilliseconds(_heartbeatIntervalMs),
            Wire.Int(parameters, "noticeWindowMs") ?? Wire.NoticeWindowMs,
            SendTerminateAsync);

        var (eventId, _) = _store.AppendEvent(GuardRules.EventConnected, _programId, _pid,
            $"守护对象接入（pid={_pid}，模式 {_mode}，心跳间隔 {_heartbeatIntervalMs} ms，versionHash {ShortHash(_versionHash)}）");

        // 审计先行，再通知界面：记录比界面重要（3.4 五的同一取舍）。
        _onProgramConnected?.Invoke();

        return Wire.Ack(id, new Dictionary<string, object>
        {
            ["connected"] = true,
            ["protocolVersion"] = Wire.ServerProtocolVersion,
            ["noticeWindowMs"] = Wire.NoticeWindowMs,
            ["eventId"] = eventId,
        });
    }

    /// <summary>guard.heartbeat：参数 programId / versionHash（5.2 第 ⑤ 条）。</summary>
    private object HandleHeartbeat(string id, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            return Wire.Err(id, GuardCode.BadArg, "参数缺失或类型不符：params 必须为对象");
        }
        var versionHash = Wire.Str(parameters, "versionHash");
        if (string.IsNullOrWhiteSpace(versionHash))
        {
            return Wire.Err(id, GuardCode.BadArg, "参数缺失：versionHash（2.4：自更新后以本值为新哈希基准）");
        }
        if (_versionHash.Length > 0 && !string.Equals(_versionHash, versionHash, StringComparison.Ordinal))
        {
            VerifyHashChange(versionHash);
        }

        // 心跳时刻与版本哈希一并转交监督者：它按单调计时判定是否构成 HEARTBEAT-LOST。
        _supervisor?.OnHeartbeat(_programId, versionHash);

        // 5.2 第 ⑤ 条：guard.heartbeat 的 result 字段为 code、detail。
        return Wire.Ack(id, new Dictionary<string, object>
        {
            ["code"] = GuardCode.Ok,
            ["detail"] = "ok",
            ["time"] = GuardClock.Now().ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
        });
    }

    /// <summary>guard.declareWhitelist：entries[]（rule/reason/evidence，5.2 第 ⑤ 条）。</summary>
    private object HandleDeclareWhitelist(string id, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("entries", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
        {
            return Wire.Err(id, GuardCode.BadArg, "参数缺失或类型不符：entries 必须为数组");
        }

        var list = new List<(string Rule, string Reason, string Evidence)>();
        foreach (var entry in entries.EnumerateArray())
        {
            var rule = Wire.Str(entry, "rule");
            var reason = Wire.Str(entry, "reason");
            var evidence = Wire.Str(entry, "evidence");
            if (rule.Length == 0 || reason.Length == 0 || evidence.Length == 0)
            {
                return Wire.Err(id, GuardCode.BadArg, "白名单条目的 rule / reason / evidence 均不得为空白（6.1.1）");
            }
            if (!IsValidRule(rule))
            {
                return Wire.Err(id, GuardCode.BadArg,
                    "规则语法非法：应为「<匹配模式> <对象文本>」，匹配模式取 exact / pathPrefix / hostSuffix / hostPortRange（6.1.5）");
            }
            list.Add((rule, reason, evidence));
        }

        if (list.Count > 64)
        {
            return Wire.Err(id, GuardCode.Limit, $"白名单有效条目上限 64（实际 {list.Count} 条），整批拒绝（12.2）");
        }

        var (accepted, total) = _store.MergeWhitelist(_programId, list);
        _store.AppendEvent(GuardRules.EventWhitelistDeclared, _programId, _pid,
            $"白名单声明：接受 {accepted} 条（共存 {total} 条）");
        return Wire.Ack(id, new Dictionary<string, object>
        {
            ["code"] = GuardCode.Ok,
            ["detail"] = $"已接受 {accepted} 条（共存 {total} 条）",
        });
    }

    /// <summary>guard.explain：eventId（result 字段 explanation：对象或 null，5.2 第 ⑤ 条）。</summary>
    private object HandleExplain(string id, JsonElement parameters)
    {
        var eventId = Wire.Str(parameters, "eventId");
        var found = _store.FindEvent(eventId);
        if (found is null)
        {
            return Wire.Ack(id, new Dictionary<string, object?> { ["explanation"] = null });
        }
        var (type, programId, time, detail, evidence) = found.Value;
        return Wire.Ack(id, new Dictionary<string, object>
        {
            ["explanation"] = new Dictionary<string, object>
            {
                ["eventId"] = eventId,
                ["rule"] = type,
                ["target"] = string.Empty,
                ["programId"] = programId,
                ["time"] = time.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
                ["detail"] = detail,
                ["evidence"] = evidence,
            },
        });
    }

    /// <summary>规则语法（6.1.5）：<c>&lt;匹配模式&gt; &lt;对象文本&gt;</c>，匹配模式拼写同 5.2 第 ⑥ 条。</summary>
    private static bool IsValidRule(string rule)
    {
        var space = rule.IndexOf(' ');
        if (space <= 0)
        {
            return false;
        }
        if (!Wire.IsMatchMode(rule[..space]))
        {
            return false;
        }
        var target = rule[(space + 1)..].Trim();
        return target.Length is > 0 and <= 1024;
    }

    private static string ShortHash(string hash) =>
        hash.Length <= 20 ? hash : hash[..20] + "…";

    // ───────────────────────────── 运行体哈希核对 ─────────────────────────────

    /// <summary>
    /// 心跳里的 versionHash 变化时核对基准（3.4 二、3.4 六）。
    /// <para>
    /// 处置分三档：① 新哈希命中已声明白名单的凭据（自更新：更新后的运行体哈希）→ 记为新基准；
    /// ② 该规则与对象已有用户确认的误报豁免 → 记为新基准；
    /// ③ 都不符 → 按 <c>HASH-MISMATCH</c> 交监督者处置。<b>不因存在白名单而整体豁免</b>——
    /// 3.4 六要求凭据与新基准都不符的哈希不一致仍按越权处置。
    /// </para>
    /// </summary>
    private void VerifyHashChange(string versionHash)
    {
        var previous = _versionHash;
        if (_store.WhitelistHasEvidence(_programId, versionHash) ||
            _store.HasExemption(_programId, GuardRules.HashMismatch, "versionHash"))
        {
            _store.SaveHashBaseline(_programId, versionHash);
            _versionHash = versionHash;
            _store.AppendEvent(GuardRules.EventHashBaseline, _programId, _pid,
                $"versionHash 变更并已被凭据覆盖：{ShortHash(previous)} → {ShortHash(versionHash)}");
            return;
        }

        _versionHash = versionHash;
        _store.AppendEvent(GuardRules.HashMismatch, _programId, _pid,
            $"运行体哈希与已声明基准不一致：{ShortHash(previous)} → {ShortHash(versionHash)}");
        _supervisor?.Escalate(_programId, GuardRules.HashMismatch, "versionHash:" + ShortHash(versionHash));
    }

    // ───────────────────────────── 服务端通知 ─────────────────────────────

    /// <summary>
    /// 发送 <c>terminate</c> 通知（7.2 第 ⑦ 条：唯一的非请求-响应消息）。
    /// <para>
    /// 它是<b>通知而不是协商</b>：协议不提供「取消终止」的响应语义，
    /// 因此本方法不等待、也不解析任何回复——发出即返回，窗口届满由监督者负责终止。
    /// </para>
    /// </summary>
    private async Task SendTerminateAsync(TerminationNotice notice)
    {
        var frame = new Dictionary<string, object>
        {
            ["protocolVersion"] = Wire.ServerProtocolVersion,
            ["method"] = "terminate",
            ["params"] = new Dictionary<string, object>
            {
                ["eventId"] = notice.EventId,
                ["rule"] = notice.Rule,
                ["target"] = notice.Target,
                ["deadline"] = notice.Deadline.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
            },
        };
        try
        {
            await Wire.WriteFrameAsync(_pipe, Wire.Serialize(frame), _cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 吞掉的是"通知发不出去（管道已断）"；降级到"照常在窗口届满后终止"——
            // 通知失败不构成暂缓终止的理由（3.4 第 3 步：不得等待程序确认）。
        }
    }

    private void End()
    {
        try
        {
            if (_pipe.IsConnected)
            {
                _pipe.Disconnect();
            }
        }
        catch
        {
            // 吞掉的是"收尾时断管道失败"；降级到"管道对象尽快交给 GC 回收"，
            // 不影响下面的会话结束留痕与资源清理。何时应传播：不需要——
            // End() 在 finally 里调用，抛出会把正常收尾变成异常路径。
        }
        if (_pid != 0)
        {
            _store.AppendEvent(GuardRules.EventDisconnected, _programId, _pid, "守护对象会话结束");
            _supervisor?.Unregister(_programId);
        }
        _onEnded?.Invoke(this);
    }
}
