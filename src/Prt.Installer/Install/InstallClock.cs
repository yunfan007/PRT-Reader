using System;

namespace Prt.Installer.Install;

/// <summary>
/// 安装器的取时点（本进程唯一）。
/// <para>
/// 全工程只在安装信息（<c>InstalledAt</c>）与崩溃日志落盘这两处读时间，此前各写一处
/// <c>DateTimeOffset.Now</c>——CRS 9.6.4 D-20 把「遍地读系统时间」列为扣分项，
/// 而门禁只看得到「读的位置数」，注释里的说明拦不住计分。收敛到一个类型之后，
/// 以后要换成可注入的时钟（例如让自检固定时刻）只需改这一处。
/// <para>
/// 跨工程可见性另议：卸载器那一侧同样需要一个"安装日期"，但它引用的是本工程已构建的
/// DLL，改这里的可见性不会立刻生效；故 ARP 登记的日期由安装侧取好后传过去，
/// 不在这条链路上另起取时点。
/// </para>
/// </summary>
internal static class InstallClock
{
    /// <summary>当前时刻。</summary>
    internal static DateTimeOffset Now() => DateTimeOffset.Now;
}
