using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Perisc.Safety.SafeGuard;

/// <summary>
/// 双击启动时的「安全系统启动中」图形界面：自建一条 STA 线程承载窗口与消息循环。
/// <para>
/// <b>为什么单独起线程</b>：WPF 窗口只能在 STA 线程上创建，而主线程要接着做运行体哈希校验、
/// 启动作业对象与子进程、守在主程序的退出上，不能被一个消息循环占住。
/// 这与 WPF 自带 <c>SplashScreen</c> 的内部做法一致（它也另起线程泵消息）。
/// </para>
/// <para>
/// <b>界面不参与任何裁决</b>：本类只被 <c>Program</c> 用来显示阶段文字与收尾，
/// 它的<b>所有公开入口都不抛异常</b>——调用方在安全流程上（拒绝启动、终止进程树都由那条
/// 流程决定），界面不得成为它的失败源。窗口在或不在，3.4 / 6.2 的行为完全一样。
/// </para>
/// <para>
/// <b>线程模型</b>：<c>_dispatcher / _stage / _completed / _disposed</c> 由 <c>_gate</c> 保护，
/// 可被任意线程读写；<c>_window / _watchdog / _shownAtTicks / _closing</c> 只属于那条
/// 启动界面线程（即窗口自己的调度线程），别的线程一律经调度器入队去碰它们；
/// <c>_shownSignal</c> 是跨线程的"已显示"信号（置位在启动界面线程，等待在主线程）。
/// </para>
/// </summary>
internal sealed class GuardSplash : IDisposable
{
    /// <summary>
    /// 最短显示时长：主程序接入很快时，界面一闪而过反而像出了故障。
    /// <para>
    /// <b>这 800 ms 是"确实看得见"的时长，不是"窗口还活着"的时长</b>——调用方（<c>Program</c>）
    /// 在<b>启动主程序之前</b>等足它（见 <see cref="EnsureMinimumVisibleAsync"/>）。
    /// 主程序一刻没起来，屏幕上就没有第二个窗口，本界面因此不会被任何东西盖住；
    /// 反过来如果先把主程序起起来再等，它的窗口（其启动画面是置顶的）会立刻把本界面压到后面，
    /// 那时"最短显示时长"就只剩账面意义了。
    /// </para>
    /// <para>
    /// 也正因为如此，本界面<b>不需要置顶</b>：置顶会踩到另一头——压住主程序自己的启动画面，
    /// 并压住紧跟其后的**需要用户操作的**「安全模块 · 请求授权」弹窗（与
    /// 「界面不得妨碍裁决交互」冲突，见《设计取舍》第 11 条）。
    /// </para>
    /// <para>取值与主程序启动画面的最短显示时长一致（<c>SplashWindow.MinimumVisibleMs</c>），两段观感同拍。</para>
    /// </summary>
    private const int MinimumVisibleMs = 800;

    /// <summary>淡出时长。短一点，让主程序的启动画面尽快接上。</summary>
    private const int FadeOutMs = 200;

    /// <summary>
    /// 最长显示时长（兜底）。主程序始终不接入（例如它自己卡住）时，
    /// 启动界面也必须自行退场：长期挂在用户可见的位置上就是妨碍，
    /// 而不是"启动界面"。这也是它与拦截层的区别——拦截层从不自行消失。
    /// </summary>
    private const int MaximumVisibleMs = 20000;

    /// <summary>等待启动界面线程收尾的上限。</summary>
    private const int ThreadJoinTimeoutMs = 700;

    /// <summary>窗口还没建起来之前先显示的阶段文字。</summary>
    private const string InitialStage = "正在启动宿主启动器…";

    private readonly object _gate = new();
    private readonly Thread _thread;

    private string _stage = InitialStage;
    private bool _completed;
    private bool _disposed;
    private Dispatcher? _dispatcher;

    private GuardSplashWindow? _window;
    private DispatcherTimer? _watchdog;
    private long _shownAtTicks;
    private bool _closing;

    /// <summary>
    /// 窗口"已显示"信号：值为窗口显示那一刻的 <c>TickCount64</c>，界面没起来时为 0。
    /// <para>
    /// 跨线程使用：置位在启动界面线程（窗口 <c>Show()</c> 之后，或建窗失败时），
    /// 等待在主线程（<see cref="EnsureMinimumVisibleAsync"/> 的 <c>await</c>）。
    /// 界面起不来的情形<b>也必须置位</b>，否则等待方会一直悬着，把启动流程拖死。
    /// </para>
    /// </summary>
    private readonly TaskCompletionSource<long> _shownSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private GuardSplash()
    {
        _thread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "perisc-guard-splash",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>
    /// 显示启动界面。失败一律返回 null（调用方据此走"没有启动界面"的分支），不抛异常。
    /// </summary>
    public static GuardSplash? TryStart()
    {
        try
        {
            return new GuardSplash();
        }
        catch
        {
            // 吞掉的是"启动界面线程起不来"（进程资源耗尽等）；降级到"没有启动界面"。
            // 何时应传播：不需要——观感增强失败在任何情况下都不该阻断启动流程。
            return null;
        }
    }

    /// <summary>推进阶段文字。文字必须是**真实阶段**，不用于伪造进度。</summary>
    public void Stage(string stage)
    {
        Dispatcher? dispatcher;
        lock (_gate)
        {
            _stage = stage;
            dispatcher = ReadyDispatcher();
        }
        Notify(dispatcher, () => _window?.SetStage(stage));
    }

    /// <summary>
    /// 安全系统就绪（主程序已接入，5.2 的 guard.connect 成功）：让启动界面淡出收工。
    /// 重复调用无副作用（重连时会再次走到这里）。
    /// </summary>
    public void Complete()
    {
        Dispatcher? dispatcher;
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }
            _completed = true;
            dispatcher = ReadyDispatcher();
        }
        Notify(dispatcher, FadeOut);
    }

    /// <summary>
    /// 等本界面**真正显示满** <see cref="MinimumVisibleMs"/> 毫秒（从窗口 <c>Show()</c> 那一刻起算）。
    /// <para>
    /// 调用点在 <c>Program</c> <b>启动主程序之前</b>：子进程一刻没创建，屏幕上就没有第二个窗口，
    /// 本界面因此不会被任何东西盖住——"至少显示 800 ms"是用户确实看得见的时长。
    /// 先起主程序、再靠置顶去保时长是另一条路，那条路会压住主程序的启动画面与授权弹窗，不走。
    /// </para>
    /// <para>
    /// <b>不抛异常</b>（与其他公开入口一致）：界面没起来时立即返回，不为此拖慢启动；
    /// 等待用 <c>Task.Delay</c>，不占线程、也不阻塞启动界面线程泵消息。
    /// </para>
    /// </summary>
    public async Task EnsureMinimumVisibleAsync()
    {
        try
        {
            var shownAt = await _shownSignal.Task.ConfigureAwait(false);
            if (shownAt <= 0)
            {
                // 界面没建起来（已降级为"没有启动界面"）：不必为它白等。
                return;
            }

            var remainingMs = MinimumVisibleMs - (int)(Environment.TickCount64 - shownAt);
            if (remainingMs > 0)
            {
                await Task.Delay(remainingMs).ConfigureAwait(false);
            }
        }
        catch
        {
            // 吞掉的是"等最短显示时长这一步出了岔子"（调度器/计时异常）；降级到"不等了，直接往下走"。
            // 何时应传播：不需要——观感节拍出问题不得阻断启动流程，这与 TryStart/Stage 的处置一致。
        }
    }

    /// <summary>
    /// 立即撤掉启动界面（失败出口用：弹错误提示之前必须先撤，否则用户第一眼看到的是那句
    /// "安全系统启动中"，而不是失败原因），并等待启动界面线程收尾（有界等待）。可重复调用。
    /// </summary>
    public void Dispose()
    {
        Dispatcher? dispatcher;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            dispatcher = ReadyDispatcher();
        }
        Notify(dispatcher, CloseAndShutdown);
        _ = _thread.Join(ThreadJoinTimeoutMs);
    }

    /// <summary>启动界面线程的主体：建窗口、显示、跑消息循环。</summary>
    private void RunMessageLoop()
    {
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            lock (_gate)
            {
                _dispatcher = dispatcher;
            }

            // 建一个 Application 对象再建窗口：XAML 编译产物（BAML）由 Application.LoadComponent
            // 加载，而 WPF 的 pack URI 解析依赖 Application 已存在。启动器虽是 WPF 程序，
            // 却没有 Application 对象（它不跑 Application.Run，消息循环归本条线程自己管）——
            // 不补这一步，窗口的 XAML 会静默加载失败（被下方 catch 吞掉，
            // 表现为"双击后没有启动界面"这种最难查的症状）。
            // ShutdownMode 取 OnExplicitShutdown：消息循环与停机一律由 CloseAndShutdown 掌握，
            // 不让"最后一个窗口关闭"另起一条收尾路径（两条路径迟早会分叉）。
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            // 窗口的属性一律在它自己的构造体里设定，这里只负责建、显示与收尾（见 GuardSplashWindow）。
            var window = new GuardSplashWindow(CurrentStage());
            _window = window;
            ArmWatchdog();
            window.Show();
            _shownAtTicks = Environment.TickCount64;
            _ = _shownSignal.TrySetResult(_shownAtTicks);

            // 主程序可能已经接入（接入快于界面创建）：那时补一次收尾，不把界面留在屏幕上。
            if (IsCompleted())
            {
                FadeOut();
            }

            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            // 吞掉的是"启动界面没建起来"（无桌面会话、WPF 初始化失败、XAML 解析失败）；
            // 降级到"没有启动界面"——主流程对这个状态已有预期（TryStart 返回 null 是同一分支）。
            // 何时应传播：不需要——界面失败不得升级成启动失败。
            // 但**不能连原因也一起吞掉**：双击时这一行也无处可去（图形路径下没有控制台），
            // 可经探针重定向、或从命令行经启动器运行时，它就是现场唯一的线索。
            Console.Error.WriteLine("[启动界面] 未能显示，已降级为无启动界面：" + ex);
            // 置位等待信号：等待最短显示时长的调用方据此立即放行，不为一个不存在的界面白等 800 ms。
            _ = _shownSignal.TrySetResult(0);
        }
    }

    /// <summary>装上兜底计时（见 <see cref="MaximumVisibleMs"/>）。只在启动界面线程上调用。</summary>
    private void ArmWatchdog()
    {
        _watchdog = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(MaximumVisibleMs),
        };
        _watchdog.Tick += (_, _) => FadeOut();
        _watchdog.Start();
    }

    /// <summary>开始淡出并关闭。只在启动界面线程上调用。</summary>
    private void FadeOut()
    {
        if (_closing)
        {
            return;
        }
        _closing = true;
        ReleaseWatchdog();

        var window = _window;
        if (window is null)
        {
            Dispatcher.CurrentDispatcher.InvokeShutdown();
            return;
        }

        // 不足最短显示时长的部分用 BeginTime 补齐（不用 Sleep/Timer 占住线程）。
        var remainingMs = MinimumVisibleMs - (int)(Environment.TickCount64 - _shownAtTicks);
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(FadeOutMs));
        if (remainingMs > 0)
        {
            fade.BeginTime = TimeSpan.FromMilliseconds(remainingMs);
        }
        fade.Completed += (_, _) => CloseAndShutdown();
        window.BeginAnimation(Window.OpacityProperty, fade);
    }

    /// <summary>关窗并结束消息循环。只在启动界面线程上调用。</summary>
    private void CloseAndShutdown()
    {
        ReleaseWatchdog();
        var window = _window;
        if (window is not null && window.IsVisible)
        {
            window.BeginAnimation(Window.OpacityProperty, null);
            window.Close();
        }
        Dispatcher.CurrentDispatcher.InvokeShutdown();
    }

    private void ReleaseWatchdog()
    {
        // DispatcherTimer 不是 IDisposable（与 System.Threading.Timer 不同），停掉即可。
        _watchdog?.Stop();
        _watchdog = null;
    }

    /// <summary>取出可用的调度器；已停机或尚未创建时返回 null（调用方据此直接跳过这次界面更新）。</summary>
    /// <remarks>只在持有 <c>_gate</c> 时调用。</remarks>
    private Dispatcher? ReadyDispatcher()
        => _dispatcher is { HasShutdownStarted: false } dispatcher ? dispatcher : null;

    private string CurrentStage()
    {
        lock (_gate)
        {
            return _stage;
        }
    }

    private bool IsCompleted()
    {
        lock (_gate)
        {
            return _completed;
        }
    }

    /// <summary>把一次界面更新投递到启动界面线程；投不进去就丢掉（界面只是观感）。</summary>
    private static void Notify(Dispatcher? dispatcher, Action action)
    {
        if (dispatcher is null)
        {
            return;
        }
        try
        {
            _ = dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
        }
        catch
        {
            // 吞掉的是"入队那一刻调度器刚好停了"（窗口已关闭）；降级到"丢掉这次界面更新"。
            // 何时应传播：不需要——调用方在安全流程上，一次界面更新失败不得影响它。
        }
    }
}
