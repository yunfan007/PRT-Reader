using System;
using Perisc.Safety;

namespace Prt.App.Services;

/// <summary>
/// 宿主程序的取时点（本进程唯一）。
/// <para>
/// 缺省用 <see cref="MonotonicClock"/>，因而**天然带 6.1.3 的回拨保护**——
/// 这正是激活到期判定原先缺失的东西：改造前用 <c>DateTime.Today</c>，
/// 把系统时间往回拨即可让已过期的授权"复活"，属可复现的安全缺陷，而不是可测性问题。
/// </para>
/// <para>
/// 自检可替换为 <see cref="ManualClock"/> 以固定时刻、验证到期与回拨行为。
/// </para>
/// </summary>
internal static class AppClock
{
    /// <summary>取时源；仅供宿主与自检替换。</summary>
    internal static ISafetyClock Source { get; set; } = new MonotonicClock();

    /// <summary>当前时刻。</summary>
    internal static DateTimeOffset Now() => Source.Now();

    /// <summary>本地日的日期部分（保持既有 <c>DateTime.Today</c> 的语义）。</summary>
    internal static DateTime Today => Now().LocalDateTime.Date;
}
