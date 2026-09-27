using System;

namespace Perisc.Safety.SafeGuard;

/// <summary>
/// 守护服务的取时点（本进程唯一）。
/// <para>
/// 为什么不共用 <c>Perisc.Safety.ISafetyClock</c>：本工程与 <c>Perisc.Safety</c> 之间**没有工程引用**，
/// 为一个接口去引用整个模块库不划算；且守护服务是独立部署的服务端进程（5.1），
/// 让它反向依赖客户端库会模糊两者的部署边界。
/// </para>
/// <para>
/// 因此这里是第二个（也是允许存在的第二个）取时点。它是**可注入**的——
/// 服务端自检可以替换成手动时钟来验证心跳窗口与终止期限，这正是 D-20 要求的形态。
/// </para>
/// </summary>
internal static class GuardClock
{
    /// <summary>取时函数；缺省读系统时间。自检可替换为手动实现。</summary>
    internal static Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.Now;
}
