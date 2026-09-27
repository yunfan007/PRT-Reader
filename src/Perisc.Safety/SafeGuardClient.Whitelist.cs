using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Tasks;

namespace Perisc.Safety;

// 白名单申报与终止解释（SafeGuardClient 的 partial；D-06）
// 拆分依据：《工程改进方案》§4.8 / CRS 9.6.4 D-06；拆分**只做位置迁移**，未改行为。
public sealed partial class SafeGuardClient : IDisposable
{
    // ───────────────────────────── 白名单 / 解释 ─────────────────────────────

    /// <summary>向守护声明误报白名单（4.6 / 6.1.5）。条目为空时表示清除本程序先前申报的全部条目。</summary>
    public PssResult DeclareWhitelist(IEnumerable<WhitelistEntry> entries)
        => RunSync(() => DeclareWhitelistAsync(entries));

    /// <summary>声明误报白名单（异步形式；与 <see cref="DeclareWhitelist"/> 同语义）。</summary>
    public async Task<PssResult> DeclareWhitelistAsync(IEnumerable<WhitelistEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var list = new List<object>();
        var count = 0;
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                return new PssResult(PssCode.BadArg, "白名单条目不得为 null");
            }
            if (string.IsNullOrWhiteSpace(entry.Rule) || string.IsNullOrWhiteSpace(entry.Reason) ||
                string.IsNullOrWhiteSpace(entry.Evidence))
            {
                return new PssResult(PssCode.BadArg, "白名单条目的 Rule / Reason / Evidence 均不得为空白");
            }
            if (!IsValidRule(entry.Rule))
            {
                return new PssResult(PssCode.BadArg,
                    "规则必须为「<匹配模式> <对象文本>」，匹配模式为 exact / pathPrefix / hostSuffix / hostPortRange（6.1.5）");
            }
            count++;
            list.Add(new Dictionary<string, object>
            {
                ["rule"] = entry.Rule,
                ["reason"] = entry.Reason,
                ["evidence"] = entry.Evidence,
            });
        }

        if (count > 64)
        {
            return new PssResult(PssCode.Limit, $"白名单有效条目上限为 64（实际 {count} 条），整批拒绝");
        }
        if (!IsConnected || _disposed)
        {
            return new PssResult(PssCode.GuardUnavailable, "守护服务未连接", degraded: true);
        }

        var reply = await CallAsync("guard.declareWhitelist",
            new Dictionary<string, object> { ["entries"] = list }, DefaultHeartbeatMs, _cts.Token).ConfigureAwait(false);
        return ToPssResult(reply, $"已声明 {count} 条");
    }

    /// <summary>事后解释某次终止（4.6）；无该事件或守护不可用时返回 null。</summary>
    public TerminationExplanation? Explain(string terminationEventId) => RunSync(() => ExplainAsync(terminationEventId));

    /// <summary>事后解释某次终止（异步形式）。</summary>
    public async Task<TerminationExplanation?> ExplainAsync(string terminationEventId)
    {
        if (string.IsNullOrWhiteSpace(terminationEventId))
        {
            return null;
        }
        if (!IsConnected || _disposed)
        {
            return null;
        }
        var reply = await CallAsync("guard.explain",
            new Dictionary<string, object> { ["eventId"] = terminationEventId }, DefaultHeartbeatMs, _cts.Token)
            .ConfigureAwait(false);
        if (!reply.Ok)
        {
            return null;
        }

        // 5.2 第 ⑤ 条：guard.explain 的 result 为 {"explanation": 对象或 null}。
        var payload = reply.Result;
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("explanation", out var explanation))
        {
            payload = explanation;
        }
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        try
        {
            return new TerminationExplanation(
                GetString(payload, "eventId"),
                GetString(payload, "rule"),
                GetString(payload, "target"),
                GetString(payload, "programId"),
                payload.TryGetProperty("time", out var t) && DateTimeOffset.TryParse(t.GetString(), out var time) ? time : _runtime.Clock.Now(),
                GetString(payload, "evidence"));
        }
        catch
        {
            return null;
        }
    }

    private static bool IsValidRule(string rule)
    {
        var space = rule.IndexOf(' ', StringComparison.Ordinal);
        if (space <= 0)
        {
            return false;
        }
        if (!PssText.TryParseMatchMode(rule[..space], out _))
        {
            return false;
        }
        var target = rule[(space + 1)..].Trim();
        return target.Length is > 0 and <= 1024;
    }
}
