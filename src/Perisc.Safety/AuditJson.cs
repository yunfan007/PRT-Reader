using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Perisc.Safety;

/// <summary>
/// 审计记录的 7.4 外部格式：一行一条 JSON（JSON Lines）。
/// <para>
/// 为什么独立成类：这是审计**唯一**对外承诺的数据格式——写入端与读取端、审计存储与自检探测、
/// 导出与补写全都依赖它。放在 <c>AuditStore</c> 里当私有成员时，"格式"与"失败策略"混在同一个
/// 千行类型里，任何一处改动都要先在脑子里把它们分开（9.6.4 / D-06）。
/// </para>
/// <para>
/// 读入的一侧尤其要当外部输入对待：这些行会落在磁盘上、可被篡改，
/// 因此 <see cref="TryDeserialize"/> 对时刻与时长一律走 <c>TryParse</c>，绝不用会抛异常的 <c>Parse</c>（D-18）。
/// </para>
/// </summary>
internal static class AuditJson
{
    internal static string Serialize(AuditRecord r)
    {
        var sb = new StringBuilder(256);
        sb.Append('{');
        void Str(string name, string value)
        {
            sb.Append('"').Append(name).Append("\":").Append(JsonSerializer.Serialize(value)).Append(',');
        }

        sb.Append("\"time\":\"").Append(r.Time.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)).Append("\",");
        Str("program", r.Program);
        Str("action", r.Action);
        Str("target", r.Target);
        sb.Append("\"decision\":\"").Append(DecisionText(r.Decision)).Append("\",");
        sb.Append("\"code\":\"").Append(r.Code).Append("\",");
        sb.Append("\"layer\":\"").Append(LayerText(r.Layer)).Append("\",");
        Str("detail", r.Detail);
        if (r.RequestId is not null) Str("requestId", r.RequestId);
        sb.Append("\"cached\":").Append(r.Cached ? "true" : "false").Append(',');
        if (r.Duration is not null)
        {
            sb.Append("\"duration\":\"").Append(r.Duration.Value.ToString("c", CultureInfo.InvariantCulture)).Append("\",");
        }
        if (r.Evidence is not null) Str("evidence", r.Evidence);
        sb.Append("\"auditDegraded\":").Append(r.AuditDegraded ? "true" : "false");
        if (r.BackfilledAt is not null)
        {
            sb.Append(",\"backfilledAt\":\"").Append(r.BackfilledAt.Value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)).Append('"');
        }
        sb.Append('}');
        return sb.ToString();
    }

    internal static string DecisionText(AuditDecision d) => d switch
    {
        AuditDecision.Allow => "allow",
        AuditDecision.Deny => "deny",
        AuditDecision.Ask => "ask",
        AuditDecision.Undeclared => "undeclared",
        _ => "escalation",
    };

    internal static string LayerText(AuditLayer l) => l switch
    {
        AuditLayer.Arm => "arm",
        AuditLayer.Intercept => "intercept",
        _ => "guard",
    };

    /// <summary>
    /// 反序列化一行（7.4）。
    /// <para>
    /// 入参来自**可被篡改的审计文件**，因此时刻与时长一律走 <c>TryParse</c>（D-18）：
    /// 改造前用 <c>DateTimeOffset.Parse</c>，异常被外层吞掉，结果是"这一行不见了"却没有任何痕迹。
    /// 现在失败路径显式：整行判为不可解析，由调用方计入 <see cref="AuditStore.DamagedLines"/> 并上报。
    /// </para>
    /// </summary>
    internal static bool TryDeserialize(string line, out AuditRecord record)
    {
        record = null!;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            string GetStr(string name, string fallback = "")
                => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;

            // time 缺失或不可解析 → 整行不可用（不得回落到"当前时刻"，那会伪造出虚假的审计时间）。
            if (!DateTimeOffset.TryParse(
                    GetStr("time"),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var time))
            {
                return false;
            }

            var decision = GetStr("decision") switch
            {
                "allow" => AuditDecision.Allow,
                "deny" => AuditDecision.Deny,
                "ask" => AuditDecision.Ask,
                "undeclared" => AuditDecision.Undeclared,
                _ => AuditDecision.Escalation,
            };
            var code = Enum.TryParse<PssCode>(GetStr("code", "Ok"), out var c) ? c : PssCode.Ok;
            var layer = GetStr("layer") switch
            {
                "arm" => AuditLayer.Arm,
                "intercept" => AuditLayer.Intercept,
                _ => AuditLayer.Guard,
            };

            DateTimeOffset? backfilledAt = null;
            if (root.TryGetProperty("backfilledAt", out var ba) && ba.ValueKind == JsonValueKind.String)
            {
                if (!DateTimeOffset.TryParse(ba.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedBackfill))
                {
                    return false; // 字段存在却不可解析：视为整行损坏，避免静默丢弃该字段
                }
                backfilledAt = parsedBackfill;
            }

            record = new AuditRecord(
                GetStr("program"), GetStr("action"), GetStr("target"),
                decision, code, layer, GetStr("detail"),
                requestId: root.TryGetProperty("requestId", out var rid) && rid.ValueKind == JsonValueKind.String ? rid.GetString() : null,
                cached: root.TryGetProperty("cached", out var cd) && cd.ValueKind == JsonValueKind.True,
                // 7.4：字段名与 AuditRecord 属性一一对应，duration 不得在往返中丢失
                // （否则 Query 取回后 Purge 会把 null 写回，造成持久损坏）。
                duration: root.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.String &&
                          TimeSpan.TryParse(du.GetString(), CultureInfo.InvariantCulture, out var parsedDuration)
                    ? parsedDuration
                    : null,
                evidence: root.TryGetProperty("evidence", out var ev) && ev.ValueKind == JsonValueKind.String ? ev.GetString() : null,
                time: time,
                auditDegraded: root.TryGetProperty("auditDegraded", out var ad) && ad.ValueKind == JsonValueKind.True,
                backfilledAt: backfilledAt);
            return true;
        }
        catch
        {
            // 吞掉的是"这一行根本不是合法 JSON"；降级到"跳过该行 + 计入损坏计数"（调用方负责）。
            // 何时应传播：不需要——损坏行的存在是本方法要报告的事实，不是要中断查询的错误。
            return false;
        }
    }
}
