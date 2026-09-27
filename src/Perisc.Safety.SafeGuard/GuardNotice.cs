using System;

namespace Perisc.Safety.SafeGuard;

/// <summary>
/// 宿主启动器侧的一次终止通知（PSS 标准 3.4 五、7.2 ⑦）。
/// <para>
/// 本工程与 <c>Perisc.Safety</c> 之间没有工程引用（见 <see cref="GuardClock"/> 的说明），
/// 因此这里是服务端自己的一份同名结构，字段与线协议 <c>terminate</c> 通知一一对应：
/// <c>eventId</c> / <c>rule</c> / <c>target</c> / <c>deadline</c>。
/// </para>
/// </summary>
internal sealed class TerminationNotice
{
    public TerminationNotice(string eventId, string rule, string target, DateTimeOffset deadline)
    {
        EventId = eventId;
        Rule = rule;
        Target = target;
        Deadline = deadline;
    }

    /// <summary>事件标识：与审计记录、事件文件三处使用同一个 EventId（6.2.4 二）。</summary>
    public string EventId { get; }

    /// <summary>触发的规则编号，取自 <see cref="GuardRules"/> 的全集。</summary>
    public string Rule { get; }

    /// <summary>终止对象，如 <c>pid:4821</c>。</summary>
    public string Target { get; }

    /// <summary>绝对截止时刻（本机时钟）；程序不得以「还没收尾完」为由要求延长。</summary>
    public DateTimeOffset Deadline { get; }
}
