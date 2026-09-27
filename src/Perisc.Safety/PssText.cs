using System;

namespace Perisc.Safety;

/// <summary>
/// 线上拼写转换（PSS 标准 5.2 第 ⑥ 条，规范性）：枚举值在线上**大小写敏感**，
/// 必须使用 <c>exact / pathPrefix / hostSuffix / hostPortRange</c>、
/// <c>native / bridged / guardOnly</c>、<c>allow / deny / ask</c>、
/// <c>Ok / NoModule / …</c> 这些写法。C# 枚举名（PascalCase）**不得**直接上线。
/// </summary>
internal static class PssText
{
    /// <summary>MatchMode → 线上拼写（5.2 第 ⑥ 条）。</summary>
    public static string FromMatchMode(MatchMode mode) => mode switch
    {
        MatchMode.Exact => "exact",
        MatchMode.PathPrefix => "pathPrefix",
        MatchMode.HostSuffix => "hostSuffix",
        _ => "hostPortRange",
    };

    /// <summary>线上拼写 → MatchMode；拼写不符标准即解析失败（不得宽松匹配）。</summary>
    public static bool TryParseMatchMode(string? text, out MatchMode mode)
    {
        switch ((text ?? string.Empty).Trim())
        {
            case "exact": mode = MatchMode.Exact; return true;
            case "pathPrefix": mode = MatchMode.PathPrefix; return true;
            case "hostSuffix": mode = MatchMode.HostSuffix; return true;
            case "hostPortRange": mode = MatchMode.HostPortRange; return true;
            default: mode = MatchMode.Exact; return false;
        }
    }

    /// <summary>IntegrationMode → 线上拼写（5.2 第 ⑥ 条）。</summary>
    public static string FromIntegrationMode(IntegrationMode mode) => mode switch
    {
        IntegrationMode.Bridged => "bridged",
        _ => "native",
    };

    /// <summary>DecisionStatus → 线上拼写（5.2 第 ⑥ 条）。</summary>
    public static string FromDecisionStatus(DecisionStatus status) => status switch
    {
        DecisionStatus.Allow => "allow",
        DecisionStatus.Deny => "deny",
        _ => "ask",
    };

    /// <summary>线上错误码 → PssCode；不认识或非全集取值时按 Internal 处理（不得自造码）。</summary>
    public static PssCode ParseCode(string? text)
        => Enum.TryParse<PssCode>(text, ignoreCase: false, out var code) && Enum.IsDefined(typeof(PssCode), code)
            ? code
            : PssCode.Internal;
}
