using System.Diagnostics;
using System.Windows;

namespace Prt.App.Views;

// 事件处理器异常兜底（MainWindow 的 partial；D-22）
// 依据：《工程改进方案》§4.8；CRS 9.6.4 D-22（`async void` 为明文例外）。
// 立据见 docs/设计取舍.md「已知妥协：WPF 事件签名固定为 void」。
//
// 本文件是主窗口内**唯一**允许「发后不管」（fire-and-forget）的地方：
// 窗口的全部事件处理器在此收口，异常不会再逃逸到 WPF 的同步上下文
// （逃逸的后果是 Dispatcher.UnhandledException → 进程直接终止）。
public partial class MainWindow : Window
{
    // ─────────────────────────── 事件处理器异常兜底 ───────────────────────────

    /// <summary>
    /// 以「发后不管」方式执行一个异步事件处理器，并统一兜住它抛出的异常。
    /// </summary>
    /// <param name="tag">出错时用于定位的处理器名，一律传 <c>nameof(...)</c>。</param>
    /// <param name="work">事件处理器的实际工作。</param>
    /// <remarks>
    /// <para>
    /// <b>为什么只能是「发后不管」</b>：WPF 的事件处理器签名固定为 <c>void</c>（D-22），
    /// 没有返回 <see cref="Task"/> 的位置，调用方（WPF 消息循环）也接不住异常。
    /// 处理器一旦需要 <c>await</c>，就只能是 <c>async void</c>；而 <c>async void</c>
    /// 逃出的异常无处可接，必然终止进程。
    /// </para>
    /// <para>
    /// <b>收口方式</b>：全部处理器写成
    /// <c>private void OnX(object sender, RoutedEventArgs e) =&gt; RunGuarded(nameof(OnX), async () =&gt; { ... });</c>，
    /// 于是整棵窗口只剩这一处「丢弃 Task」，异常在这里被降级为状态栏提示，
    /// 不再有「漏改一个处理器就崩进程」的风险。
    /// </para>
    /// <para>
    /// <b>为什么不加 <c>ConfigureAwait(false)</c></b>：<paramref name="work"/> 会触碰
    /// WPF 元素，必须回到 UI 线程续跑。本方法属应用层而非库层，
    /// D-25 的「库侧一律 <c>ConfigureAwait(false)</c>」在此不适用。
    /// </para>
    /// </remarks>
    private void RunGuarded(string tag, Func<Task> work) => _ = GuardAsync(tag, work);

    /// <summary><see cref="RunGuarded"/> 的实际执行体：捕获并降级异常。</summary>
    /// <remarks>
    /// 刻意声明为 <c>async Task</c> 而非 <c>async void</c>：异常被包进返回的
    /// <see cref="Task"/> 并由本方法自行 <c>catch</c>。<c>await work()</c> 之前的
    /// 同步异常同样由 <c>async</c> 状态机捕获（不会同步抛回调用方），
    /// 所以 <see cref="RunGuarded"/> 丢弃返回值的写法是安全的。
    /// </remarks>
    private async Task GuardAsync(string tag, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            // 吞掉的是「调用方主动取消」：例如用户在保存流程中点取消，
            // 或关闭窗口时放弃了未保存的修改。降级状态＝不改变任何界面内容
            // （用户清楚自己刚取消了什么，再弹一次提示纯属噪音）。
            // 何时应传播：若某个处理器把「取消」视为必须显式告知用户的失败，
            // 就不要在内部抛 OperationCanceledException，而应抛带消息的异常——
            // 那会落到下面的分支并显示在状态栏。
        }
        catch (Exception ex)
        {
            // 吞掉的是「单个事件处理器内的任何意外失败」。降级状态＝
            // 主窗口继续可用（不退出进程），失败原因显示在状态栏。
            // 何时应传播：本方法已是异常传播的终点；确实需要区分严重性的场合，
            // 应在处理器内部先自行处理（例如提示后 return），
            // 不要指望异常一路走到这里再被识别。
            Debug.WriteLine($"[guard] {tag} 未处理异常：{ex}");
            SetStatus($"操作失败（{tag}）：{ex.Message}");
        }
    }
}
