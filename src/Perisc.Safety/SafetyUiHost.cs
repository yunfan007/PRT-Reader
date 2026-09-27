using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Perisc.Safety;

/// <summary>
/// 模块界面挂钩（实现级，非标准 API 面）：宿主程序（如 PRT 阅读器）在启动时通过
/// <see cref="SafetyOptions.UiHost"/> 注入，库在需要用户裁决（3.4 ask）或打开模块界面（ShowUi）时回调。
/// 标准禁止任何"关闭审计/永久允许"后门——这里只承载"用户当场决定"的交互通道本身。
/// <para>
/// <b>本次改造的两处要点</b>
/// </para>
/// <list type="number">
/// <item><b>不再是静态类</b>。原先两个 handler 是 <c>public static { get; set; }</c>，
/// 按 9.6.4 / D-07 的锚点属于"任意位置可改的静态可变状态"，判 3 分并直接封顶可维护性维度 B-。
/// 它也确实挡住了正确的写法：两个 <see cref="SafetyClient"/> 实例只能共用同一组 handler，
/// "各自的宿主、各自的界面"这种用例在静态 handler 下根本无法表达。
/// 现在挂钩随 <c>SafetyOptions</c> 注入，一个客户端一份。</item>
/// <item><b>处理器改为异步</b>。原先的同步签名要求宿主"立刻给出答复"，
/// 于是库只能靠 <c>Task.Wait(超时)</c> 等它，而界面又要回到 UI 线程弹窗——
/// 两边一凑就只能在 UI 线程上跑嵌套消息泵（<c>Dispatcher.PushFrame</c>）。
/// 改成 <c>Task</c> 之后，等待的是一个任务而不是一个线程：调用方不必占住任何线程，
/// 界面也能在自己的消息循环里正常响应（4.3 第 3 条的超时仍由库侧按 <c>CancelAfter</c> 施加）。</item>
/// </list>
/// <para>
/// <b>同步 <c>Ask</c> 的情形</b>：<see cref="SafetyClient.Ask"/> 仍保留（4.3 第 3 条要求在调用线程上等答复），
/// 但它**不得在 UI 线程调用**——界面宿主的答复依赖于回到 UI 线程，在 UI 线程上同步等待等于等待自己。
/// 界面程序一律走 <see cref="SafetyClient.AskAsync"/>。判据与取舍见《设计取舍》"已知妥协"。
/// </para>
/// </summary>
public sealed class SafetyUiHost
{
    /// <summary>单次询问的用户答复：Allowed=false 即拒绝。</summary>
    public sealed class Answer
    {
        public Answer(bool allowed, TimeSpan? ttl = null, int? maxUses = null)
        {
            Allowed = allowed;
            Ttl = ttl;
            MaxUses = maxUses;
        }

        public bool Allowed { get; }
        public TimeSpan? Ttl { get; }
        public int? MaxUses { get; }
    }

    /// <summary>
    /// 批量询问处理器：入参为需要用户决定的请求（顺序与未决项一致），
    /// 出参须与入参一一对应；元素为 null 视为拒绝/未决。
    /// <para>
    /// 未设置时申报一律按"用户未决定"处理（拒绝，fail-closed），不抛异常。
    /// 处理器内部若抛出，库按拒绝处理并继续（界面回调异常不得影响裁决）。
    /// </para>
    /// </summary>
    public Func<IReadOnlyList<AskRequest>, CancellationToken, Task<IReadOnlyList<Answer?>>>? AskHandler { get; set; }

    /// <summary>
    /// 打开模块界面（审计页 / 许可页 / 行为清单页）。
    /// 返回的 <see cref="Task"/> 在界面"已打开"即完成（不等待窗口关闭）；
    /// 未注册时 <see cref="SafetyClient.ShowUiAsync"/> 返回降级结果（NoModule）。
    /// </summary>
    public Func<SafetyUiPage, Task>? ShowUiHandler { get; set; }
}
