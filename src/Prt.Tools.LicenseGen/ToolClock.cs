using System;

namespace Prt.Tools.LicenseGen;

/// <summary>
/// 签发工具的取时点（本进程唯一）。
/// <para>
/// 为什么不共用 <c>Perisc.Safety.ISafetyClock</c>：本工程是**签发端**独立工具，不引用模块库
/// （也不应引用——签发端与运行期安全模块没有任何调用关系）。为一个接口建立工程引用会引入
/// 无谓的耦合与分发体积。
/// </para>
/// <para>
/// 可注入：签发时间参与激活码载荷与到期计算，测试需要能固定它。
/// </para>
/// </summary>
internal static class ToolClock
{
    /// <summary>取时函数；缺省读系统时间。</summary>
    internal static Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.Now;
}
