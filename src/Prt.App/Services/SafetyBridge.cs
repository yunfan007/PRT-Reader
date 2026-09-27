using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Perisc.Safety;
using Prt.App.Views;

namespace Prt.App.Services;

/// <summary>
/// PSS 接入桥（接入模式 A / Native，2.7）：把 Perisc.Safety 模块库接到阅读器的界面与业务流上。
/// <para>
/// - 启动：创建模块一（SafetyClient）→ 注册模块二（InterceptClient）→ 注册界面挂钩
///   （授权弹窗 + 安全中心）→ 把同一份客户端交给模块四（<see cref="SafeRuntime.SetClient"/>）→
///   后台向守护服务（模块三）连接并周期心跳（连接失败按 3.6 的短期断开降级，不改变功能）；
/// - 业务侧入口：<see cref="EnsureAllowedAsync"/>（**仅**用于 SRT 尚未覆盖的收口点，如打印）、
///   <see cref="OpenCenter"/>（安全中心）。
/// </para>
/// <para>
/// <b>为什么文件/剪贴板等不再有入口</b>：那些行为已全部收口到模块四（<c>SafeRuntime</c> 类型族），
/// 调用 SRT 的函数即完成「申报 → 裁决 → 执行 → 审计」四步（3.8）。
/// 再在这里放一个"申报 + 裸调 BCL"的手写入口，等于给绕开 SRT 留了一条正门。
/// </para>
/// </summary>
internal static class SafetyBridge
{
    /// <summary>
    /// 本程序标识（安全中心与审计记录中的 program 字段）。
    /// <para>
    /// 权威来源是<b>宿主启动器</b>（模块三，唯一启动入口）随环境注入的值：双击启动时命令行
    /// 没有 <c>--program-id</c> 可传，若主程序继续自报一个写死的名字，同一次启动就会在启动器侧
    /// 与程序侧留下两种标识，审计与白名单分组随之分叉——启动权在启动器，标识权也一并归它。
    /// </para>
    /// <para>
    /// 未经启动器直接运行（开发期的 <c>dotnet exec</c>）拿不到注入值，回退到程序集名；
    /// 该路径按部署文档的声明不得用于评估或认证。
    /// </para>
    /// </summary>
    public static string ProgramId => s_programId.Value;

    /// <summary>
    /// 启动器注入程序标识所用的变量名。
    /// <para>
    /// 这个字面量必须与启动器侧的 <c>Perisc.Safety.SafeGuard.GuardLauncher.ProgramIdVariable</c>
    /// 一致；两处各写一份是因为启动器刻意只依赖 BCL，不引用被它监护的程序集。
    /// </para>
    /// </summary>
    private const string ProgramIdVariable = "PERISC_SAFEGUARD_PROGRAM_ID";

    private static readonly Lazy<string> s_programId = new(ResolveProgramId);

    private static string ResolveProgramId()
    {
        var injected = Environment.GetEnvironmentVariable(ProgramIdVariable);
        return string.IsNullOrWhiteSpace(injected)
            ? Assembly.GetEntryAssembly()?.GetName().Name ?? "Prt.App"
            : injected.Trim();
    }

    /// <summary>
    /// 申报超时（PSS 4.3 第 3 条：默认 5 秒）：本程序走的是**用户交互型**申报
    /// （授权弹窗，用户需要阅读行为、对象与理由），故显式声明 2 分钟为答复窗口；
    /// 超时一律按 <c>Deny + Timeout</c> 处理（fail-closed），并在行为清单中列明本值。
    /// </summary>
    private static readonly TimeSpan AskTimeout = TimeSpan.FromMinutes(2);

    private static readonly object Gate = new();
    private static SafetyClient? _client;
    private static SafeGuardClient? _guard;

    /// <summary>
    /// 本程序的界面挂钩实例：随 <see cref="SafetyOptions.UiHost"/> 注入模块一。
    /// <para>
    /// 做成实例而不是往静态属性上挂（原先的 <c>SafetyUiHost.AskHandler = ...</c>）：
    /// 按 9.6.4 / D-07，"任意位置可改的 public static 属性"判 3 分并直接封顶可维护性维度 B-。
    /// 本进程只有一个宿主实例，所以这里持有一份即可——但"只有一份"是**本进程的约定**，
    /// 不再由库的静态字段替我们保证。
    /// </para>
    /// </summary>
    private static readonly SafetyUiHost UiHost = new();

    /// <summary>无头（自检）模式：裁决照常，但拒绝时不弹对话框。</summary>
    public static bool Quiet { get; private set; }

    /// <summary>模块一客户端；未初始化（自检模式）为 null。</summary>
    public static SafetyClient? Client => _client;

    /// <summary>初始化：只在界面启动路径调用（--selftest 不接入，保持自检封闭）。</summary>
    public static void Initialize()
    {
        lock (Gate)
        {
            if (_client is not null)
            {
                return;
            }
            _client = SafetyClient.Create(new SafetyOptions(
                askTimeout: AskTimeout,
                auditFailurePolicy: AuditFailurePolicy.Buffer,   // 2.6.1 默认策略，显式声明
                mode: IntegrationMode.Native,
                // 必须在这里给出标识：紧接着的 InterceptClient.Register 会立刻用
                // runtime.ProgramId 拼出白名单文件名（whitelist-<标识>.json）。
                // 若此处不注册，那一瞬间它还是 "unknown-program"，白名单就落到一个
                // 谁也不会去读的文件上——默认拒绝照旧生效，但用户答应的放行条目会全部失效。
                programId: ProgramId,
                uiHost: UiHost));
            InterceptClient.Register(new InterceptOptions());
            UiHost.AskHandler = AskViaDialogAsync;
            UiHost.ShowUiHandler = ShowCenterAsync;

            // 3.9 ②：把本进程唯一的模块一客户端交给 SRT，使两者共用同一许可池——
            // 否则 SRT 自起一份没有界面宿主的客户端，一律 fail-closed，程序连配置都读不出来。
            SafeRuntime.SetClient(_client);
        }

        // 守护服务（模块三）在本程序里**不是可选项**：接入模式 A 要求它是唯一启动入口，
        // 而"由它启动"这件事已经由 PSS 7.1 的启动顺序保证（运行 Perisc.Safety.SafeGuard.exe
        // 而不是主程序）。这里做的只是**子进程侧主动上报**——连接、心跳与拦截事件都由本侧发起（3.4 三）。
        // 连不上属 3.6 的「宿主管道短期断开」，按降级处理（依实际生效层数计分）并在行为清单中标注。
        _ = Task.Run(async () =>
        {
            // 4.6：连接失败不抛异常，返回的实例 IsConnected 为 false。
            var guard = await SafeGuardClient.ConnectAsync(ProgramId).ConfigureAwait(false);
            if (!guard.IsConnected)
            {
                guard.Dispose();
                return;
            }
            _guard = guard;
            guard.Terminating += (_, notice) => ShowTerminationNotice(notice);

            // 4.9 第 ① 步：连接后显式启动周期心跳（默认 5 秒，12.2 上限 30 秒）。
            await guard.StartHeartbeatAsync().ConfigureAwait(false);

            var intercept = InterceptClient.Current;
            if (intercept is not null && intercept.Whitelist.Snapshot().Count > 0)
            {
                var entries = intercept.Whitelist.Snapshot()
                    .Select(e => new WhitelistEntry(e.Rule, e.Reason, e.Evidence))
                    .ToList();
                await guard.DeclareWhitelistAsync(entries).ConfigureAwait(false);
            }
        });
    }

    /// <summary>
    /// 自检无头初始化：自检过程没有用户界面，由自检框架扮演用户角色自动允许申报；
    /// 不写业务审计、不连守护。默认拒绝的完整链路只在真实运行路径生效。
    /// </summary>
    public static void InitializeHeadless()
    {
        lock (Gate)
        {
            if (_client is not null)
            {
                return;
            }
            _client = SafetyClient.Create(new SafetyOptions(
                autoRecordDecisions: false,
                mode: IntegrationMode.Native,
                programId: ProgramId + ".selftest",
                uiHost: UiHost,
                // 2.1：默认 10 次/秒、硬上限 100 次/秒。自检框架以**机器速度**扮演用户
                // （一次答复就是一次方法返回，不是一次点击），几十条用例的夹具在毫秒内连发申报，
                // 撞上限流是必然的。所以这里显式声明到上限——而不是让 SRT 去做"睡一秒再报"
                // 那种退让（那会把同步 API 变成阻塞调用线程的 API，详见 SafeExecutor 的类注释）。
                // 真实运行路径（Initialize）保持默认 10 次/秒：那里的节拍由用户逐条答复决定。
                askRateLimitPerSecond: SafetyOptions.MaxAskRateLimitPerSecond));
            InterceptClient.Register(new InterceptOptions());
            // 自检没有用户，由自检框架扮演用户角色：一律允许。
            UiHost.AskHandler = AutoAllowHandler;
            UiHost.ShowUiHandler = _ => Task.CompletedTask;
            Quiet = true;

            // 同 3.9 ②：自检路径下 SRT 也要共用这一份「自检框架扮演用户」的客户端，
            // 否则 SRT 侧拿不到答复，文件读写一律 Denied，自检第一步就走不下去。
            SafeRuntime.SetClient(_client);
        }
    }

    /// <summary>
    /// 自检框架扮演用户：一律允许。
    /// <para>
    /// 这里返回已完成的 Task —— 于是整条异步申报链在自检里是<b>同步完成</b>的，自检用例不必为它
    /// 改成异步（但这不等于它可以被同步 API 顶替：生产路径上的界面宿主必须回到 UI 线程，
    /// 那才是必须异步的原因）。
    /// </para>
    /// </summary>
    private static Task<IReadOnlyList<SafetyUiHost.Answer?>> AutoAllowHandler(
        IReadOnlyList<AskRequest> requests, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.FromResult<IReadOnlyList<SafetyUiHost.Answer?>>(
            requests.Select(_ => new SafetyUiHost.Answer(
                allowed: true,
                ttl: TimeSpan.FromHours(24),   // 12.2 许可有效期上限
                maxUses: 1000)).ToList());     // 12.2 单许可最大可用次数上限；不给出即单次许可（6.1.3）
    }

    /// <summary>
    /// 自检专用：把申报答复换成「无人答复」。
    /// <para>
    /// 用来复现<b>真实启动期</b>的处境——那时 <see cref="Initialize"/> 还没跑，SRT 会自建一份没有
    /// 界面宿主的客户端，任何申报都无人答复，按 3.4 一律 <c>Deny</c>。自检默认用的「一律允许」挂钩
    /// 会把这种处境整个盖住，于是「启动期读写被拒」这类缺陷在自检里看不见（2026-09-26 就是这么
    /// 漏掉的：设置、会话、授权、旧配置迁移在真实启动时全被拒，自检却 52 项全绿）。
    /// 用例 48 借这个入口把处境摆回来。
    /// </para>
    /// </summary>
    internal static void UseNoAnswerHandlerForSelfTest()
        => UiHost.AskHandler = (requests, _) =>
            Task.FromResult<IReadOnlyList<SafetyUiHost.Answer?>>(new SafetyUiHost.Answer?[requests.Count]);

    /// <summary>自检专用：把申报答复恢复为「自检框架扮演用户、一律允许」（<see cref="InitializeHeadless"/> 的默认形态）。</summary>
    internal static void UseAutoAllowHandlerForSelfTest() => UiHost.AskHandler = AutoAllowHandler;

    /// <summary>
    /// 退出前释放：Dispose 不撤销许可、不删除审计（4.10 第 5 条）。
    /// <para>
    /// 顺序有讲究：先放守护客户端（它持有命名管道与两个长期存活的循环），
    /// 再注销拦截层与申报客户端（各自归还一份运行时持有），
    /// 最后**拆除进程环境**——<c>AuditStore</c> 的补写定时器在环境拆除时才真正停掉。
    /// </para>
    /// <para>
    /// 为什么最后那步必须显式调用：本程序里 <c>AuditRecord.For</c> 与守护客户端都会"无主借用"
    /// 进程运行时（见 <c>SafetyEnvironment</c> 的说明），借用方没有对称的归还点，
    /// 因此运行时被钉住，只能由宿主在退出时拆除。这是本程序对"定时器不靠进程退出兜底"的兑现点。
    /// </para>
    /// </summary>
    public static void Shutdown()
    {
        _guard?.Dispose();
        InterceptClient.Current?.Dispose();
        _client?.Dispose();
        // 解绑：交回默认行为，避免 SRT 长期持有一个已 Dispose 的客户端
        // （下次访问时它会按默认配置重建一份，不会再用到这一份）。
        SafeRuntime.SetClient(null);
        // 进程环境拆除：等最后一份持有归还后，把审计存储（含补写定时器）放下。
        // 4.10 第 5 条：待写队列保留在磁盘上，下次启动由 AuditStore.Initialize 接管，不丢记录。
        SafetyEnvironment.Shutdown();
    }

    // ───────────────────────────── 申报 ─────────────────────────────

    /// <summary>申报一次敏感行为（供 SRT 尚未覆盖的收口点使用）。</summary>
    public static async Task<Decision> AskAsync(string action, string target, string reason, MatchMode matchMode = MatchMode.Exact)
    {
        var client = _client;
        if (client is null)
        {
            // 未接入（自检等）：fail-closed。
            return new Decision(DecisionStatus.Deny, PssCode.NoModule, "安全模块未初始化", "req-none");
        }

        var request = new AskRequest(action, target, reason, matchMode);

        // 库按 4.3 第 3 条在调用栈内等答复，只是这个"等"现在是 await 一个任务，不是占住一个线程。
        // 界面因此全程可交互——原先为了在 UI 线程上等它，只能在 UI 线程里跑嵌套消息泵
        // （Dispatcher.PushFrame），那是 9.6.4 / D-14 明确要消除的形态；超时仍由库侧
        // 按 AskTimeout 施加，不需要调用方再套一层。
        return await client.AskAsync(request);
    }

    /// <summary>申报并处理拒绝：拒绝时弹出提示并返回 false（SRT 未覆盖的收口点的统一入口）。</summary>
    public static async Task<bool> EnsureAllowedAsync(string action, string target, string reason, string operationName,
                                                     MatchMode matchMode = MatchMode.Exact)
    {
        var decision = await AskAsync(action, target, reason, matchMode);
        if (decision.IsAllowed)
        {
            return true;
        }
        if (Quiet)
        {
            return false;
        }
        MessageBox.Show(
            $"「{operationName}」被安全模块拒绝。\n\n行为：{DescribeAction(action)}\n对象：{target}\n原因：{decision.Detail}",
            "PRT 阅读器 · 安全模块",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    /// <summary>行为编号的展示文本（编号 + 4.6 条目名）。</summary>
    public static string DescribeAction(string action) =>
        BehaviorCatalog.Find(action) is { } item ? $"{action} {item.Title}" : action;

    // ───────────────────────────── 界面 ─────────────────────────────

    /// <summary>打开安全中心（许可 / 审计 / 行为清单）。</summary>
    public static void OpenCenter(SafetyUiPage page = SafetyUiPage.Audit)
    {
        if (_client is null)
        {
            return;
        }
        var window = new SecurityCenterWindow(_client, page);
        window.Owner = Application.Current?.MainWindow;
        window.Show();
    }

    /// <summary>
    /// 在 UI 线程上弹出授权对话框，返回在对话框**关闭时**完成的 <see cref="Task"/>。
    /// <para>
    /// 这里不使用嵌套消息泵：<c>ShowDialog</c> 自身就是一个模态消息循环，等待期间界面照常响应；
    /// 而库那一侧等的是一个任务而不是一个线程。两者都不需要"在 UI 线程里再泵一层消息循环"。
    /// </para>
    /// <para>
    /// 超时（<see cref="AskTimeout"/>）由库侧计时并放弃等待，界面不因超时被强行关闭——
    /// 用户正在读的卡片不该在到点时被夺走；对话框何时关由用户决定。
    /// </para>
    /// </summary>
    private static Task<IReadOnlyList<SafetyUiHost.Answer?>> AskViaDialogAsync(
        IReadOnlyList<AskRequest> requests, CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return Task.FromResult<IReadOnlyList<SafetyUiHost.Answer?>>(new SafetyUiHost.Answer?[requests.Count]);
        }

        // 界面不存在时（例如无头场景未按 Quiet 初始化）一律按"用户未决定"处理（3.4）。
        _ = cancellationToken;

        // 已经在 UI 线程上：**直接开**对话框，返回已完成的 Task。
        // 这一支不能省，也不能改回 InvokeAsync：SRT（模块四）的接口是同步的，
        // 它会在调用栈里等这个 Task 完成；而 InvokeAsync 是把操作排进 Dispatcher 队列，
        // 只有等调用方返回后队列才会被处理。两者互等就是死锁。
        // 这不是"在 UI 线程里再泵一层消息循环"（9.6.4 / D-14 禁的是 Dispatcher.PushFrame）：
        // ShowDialog 本身就是模态消息循环，等待期间界面照常响应。
        if (dispatcher.CheckAccess())
        {
            return Task.FromResult(ShowAskDialog(requests));
        }

        return dispatcher.InvokeAsync(() => ShowAskDialog(requests)).Task;
    }

    private static IReadOnlyList<SafetyUiHost.Answer?> ShowAskDialog(IReadOnlyList<AskRequest> requests)
    {
        var dialog = new SafetyAskDialog(requests)
        {
            Owner = Application.Current?.MainWindow,
        };
        return dialog.ShowDialog() == true
            ? dialog.CollectAnswers()
            : new SafetyUiHost.Answer?[requests.Count]; // 未决 = 拒绝（3.4）
    }

    /// <summary>打开安全中心的界面回调形态（需要在 UI 线程上建窗口）。</summary>
    private static Task ShowCenterAsync(SafetyUiPage page)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return Task.CompletedTask;
        }
        // 同 AskViaDialogAsync：已在 UI 线程就直接做，别排队——排队在同步调用方那里会互等。
        if (dispatcher.CheckAccess())
        {
            OpenCenter(page);
            return Task.CompletedTask;
        }
        return dispatcher.InvokeAsync(() => OpenCenter(page)).Task;
    }

    private static void ShowTerminationNotice(TerminationNotice notice)
    {
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                ShowTerminationNoticeCore(notice);
            }
            else
            {
                dispatcher.Invoke(() => ShowTerminationNoticeCore(notice));
            }
        }
        catch
        {
            // 通知窗口打不开不影响守护按窗口期强制终止（2.4）。
        }
    }

    private static void ShowTerminationNoticeCore(TerminationNotice notice)
    {
        MessageBox.Show(
            "安全守护服务发出终止通知：\n\n" +
            $"规则：{notice.Rule}\n事件：{notice.EventId}\n对象：{notice.Target}\n" +
            $"终止期限：{notice.Deadline.LocalDateTime:HH:mm:ss.fff}\n\n" +
            "请在期限内保存工作；详情可在守护服务处查询（Explain）。",
            "PRT 阅读器 · 安全守护",
            MessageBoxButton.OK,
            MessageBoxImage.Stop);
    }
}
