using System.Diagnostics;
using System.IO.Pipes;

namespace Perisc.Safety.SafeGuard;

/// <summary>
/// 宿主启动器入口（PSS 标准 3.4、7.1，模块三）。
/// <para>
/// 它是接入程序的<b>唯一启动入口</b>：程序不自带用户可直接启动的可执行入口，
/// 启动权在这里。流程固定为「校验运行体哈希 → 建作业对象 → 启动主程序（子进程）→ 监听管道」。
/// </para>
/// <para>
/// 校验用的基准取自同目录的 <c>Safe.data</c>（<see cref="GuardIntegrityBaseline"/>）：
/// 它由 <c>build.ps1</c> 在发布时生成、随包分发，因此与本批发布的运行体天然同源；
/// 启动器打开后<b>独占持有</b>该文件到进程退出，运行期间谁也不能替换它。
/// 基准缺失即拒绝启动（3.4 二），不再有"本机首次登记后放行"的宽松档。
/// </para>
/// <para>
/// 父子进程关系即终止权的来源：不需要安装为系统服务，也不需要管理员权限（3.4 一）。
/// </para>
/// </summary>
/// <remarks>
/// 用法：
/// <code>
/// Perisc.Safety.SafeGuard --program Prt.App.dll [--program-id 阅读器] [--hash sha256:…]
///                         [--pipe 管道名] [--instances N] [--dotnet 路径] [-- 主程序参数…]
/// </code>
/// <para>
/// <b>不带参数直接运行（双击）</b>：自动启用同目录内那个唯一可由 dotnet 宿主启动的 DLL
/// （判据见 <see cref="InferProgramPath"/>），不需要用户记命令行；此时**没有控制台可附**
/// （启动器是 GUI 子系统程序，系统不为它建控制台，见 <see cref="GuardConsole"/>），
/// 于是屏幕上既没有黑框也没有终端窗口——黑框有两个潜在来源，两个都要压住：
/// 启动器自己的（GUI 子系统，系统不建）与 dotnet 宿主子进程的（控制台子系统程序、
/// 父进程无控制台时系统会替它新建一个，见 <see cref="GuardLauncher.Launch"/> 的
/// <c>graphical</c> 参数）。屏幕上只显示「安全系统启动中」的图形界面
/// （见 <see cref="GuardSplash"/>），主程序接入后淡出；启动失败改由系统弹窗告知。
/// 从 cmd / PowerShell 启动时则附回父控制台，输出照旧、不显示界面。
/// </para>
/// <para>
/// 启动界面纯属观感：它不显示假进度、不拦截操作、不参与裁决，在或不在都不改变
/// 哈希校验、作业对象、管道监听与终止规则（3.4 / 6.2）的执行。
/// </para>
/// </remarks>
public static class Program
{
    private const string DefaultPipeName = "Perisc.Safety.Guard.v1";

    public static async Task<int> Main(string[] args)
    {
        // 启动器是 GUI 子系统程序（见 csproj）：系统不会为它建控制台，双击时屏幕上不会出现黑框——
        // 这是"不产生"，而不是"产生了再藏起来"。命令行用法在这里附回父控制台并重接标准流：
        // 附上了就有输出、走命令行路径；附不上就是**图形路径**（双击，或从没有控制台的宿主启动），
        // 失败提示改走系统弹窗。判定依据与代价见 GuardConsole 的说明。
        var graphical = !GuardConsole.AttachToParentConsole();

        // 图形化启动界面：只在「双击启动」这一条路径上出现（无参数 + 没有可附的控制台）。
        // 从 cmd / PowerShell 启动时不显示——命令行输出与脚本调用不受影响，
        // 自动化环境里也少一个需要桌面会话的窗口。
        // TryStart 起不来时返回 null，后续 Stage/Complete 都是空操作，启动流程照常。
        using var splash = graphical && args.Length == 0 ? GuardSplash.TryStart() : null;

        // 失败一律经此出口。命令行启动时看得到 stderr；图形路径下没有控制台，
        // 改用系统弹窗——否则"拒绝启动"会退化成一闪而过、用户无从知晓的静默失败（3.4）。
        int Report(string message, int exitCode)
        {
            Console.Error.WriteLine(message);
            if (graphical)
            {
                // 先撤启动界面再弹窗：两者同时可见时，用户第一眼看到的会是一句看不懂的
                // "安全系统启动中"，失败原因反而在后面——那正是 3.4 要禁止的"静默失败"。
                splash?.Dispose();
                GuardConsole.ShowFatal(message);
            }
            return exitCode;
        }

        if (!TryParse(args, out var options, out var error))
        {
            return Report(error + Environment.NewLine + Usage, 2);
        }

        GuardStore store;
        try
        {
            store = new GuardStore();
        }
        catch (Exception ex)
        {
            return Report($"存储初始化失败：{StoreRootHint} 不可写：{ex.Message}", 2);
        }

        // 事件保留至少 90 天（6.2.4 二）：每次启动时清一次超期分片。
        var purged = store.PurgeExpiredEvents(TimeSpan.FromDays(90));
        var (startupEventId, _) = store.AppendEvent(GuardRules.EventStarted, options.ProgramId, Environment.ProcessId,
            $"宿主启动器启动（pipe={options.PipeName}，instances={options.Instances}，清理超期分片 {purged} 个）");
        Console.WriteLine($"启动自检事件：{startupEventId}　存储：{store.Root}");

        // ── 启动前校验（3.4 二，规范性）：不一致不得启动 ──
        splash?.Stage("正在校验运行体完整性…");
        var actualHash = GuardLauncher.ComputeHash(options.ProgramPath);

        // 基准来源按强度排列：① --hash 显式声明（部署方当场指定，升级与取证时用）；
        // ② 程序目录的 Safe.data——**随发布包分发的出厂声明**，由 build.ps1 在发布时生成。
        // 两者都拿不到（文件缺失 / 读不出 / 里面没有这个运行体）时**拒绝启动**：没有基准就证明不了
        // 运行体完整，按 3.4 二 只能不放行。这里刻意不再保留原先的"首次登记"一档——它等于
        // "没有基准就放行"，而基准落在本机即可被删掉绕过；出厂声明随包分发之后，那一档既无必要也无好处，
        // 顺带消除了"本机残留基线导致重建后拒启、要手工清掉才能跑"这一类需要人工干预的状态。
        GuardIntegrityBaseline? baseline = null;
        var baselineError = string.Empty;
        if (options.DeclaredHash is null)
        {
            baseline = GuardIntegrityBaseline.Open(out baselineError);
        }

        // 句柄独占持有到 Main 返回（进程退出）：运行期间谁也不能改写或删除这份基准。
        // 用一个只承担生命周期的别名——using 的作用域就是方法体，正是"整段运行期"；
        // 对 null 是空操作，故 --hash 路径（不开文件）走这里也安全。
        using var baselineLease = baseline;

        var declaredHash = options.DeclaredHash ?? baselineLease?.Find(options.ProgramPath);
        if (declaredHash is null)
        {
            var reason = baselineLease is null
                ? baselineError
                : "基准文件里没有「" + Path.GetFileName(options.ProgramPath) + "」的条目";
            var (missingId, missingEvidence) = store.AppendEvent(GuardRules.BaselineMissing,
                options.ProgramId, 0, $"缺少运行体哈希基准：{reason}（实际 {actualHash}）");
            return Report(
                $"拒绝启动：缺少运行体完整性基准（{GuardRules.BaselineMissing}）：" +
                GuardIntegrityBaseline.FileName + "。" + Environment.NewLine +
                $"　原因：{reason}" + Environment.NewLine +
                $"　事件：{missingId}　证据：{missingEvidence}" + Environment.NewLine +
                $"　{GuardIntegrityBaseline.Hint}", 3);
        }

        if (!string.Equals(declaredHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            var (mismatchId, evidence) = store.AppendEvent(GuardRules.HashMismatch, options.ProgramId, 0,
                $"运行体哈希与已声明发布包不一致：声明 {declaredHash}，实际 {actualHash}");
            return Report(
                $"拒绝启动：运行体哈希不一致（{GuardRules.HashMismatch}）。" + Environment.NewLine +
                $"　事件：{mismatchId}　证据：{evidence}", 3);
        }

        using var job = GuardJob.Create();
        using var supervisor = new GuardSupervisor(store, job);

        // 先把启动界面显示足了，再启动主程序——**主程序在界面显示期间压根不启动**。
        // 子进程一刻没创建，屏幕上就没有第二个窗口能把本界面压到后面，于是"至少显示
        // GuardSplash.MinimumVisibleMs（800 ms）"是用户确实看得见的时长；反过来（先起主程序、
        // 再靠置顶去保时长）会压住主程序自己的启动画面与需要用户操作的授权弹窗，不走那条路。
        // 阶段文字仍是真实阶段：本段之后紧接着就是启动主程序，没有伪造的中间进度。
        if (splash is not null)
        {
            splash.Stage("正在启动主程序…");
            await splash.EnsureMinimumVisibleAsync().ConfigureAwait(false);
        }

        Process child;
        try
        {
            child = GuardLauncher.Launch(options.ProgramPath, options.ProgramArguments, options.ProgramId, job,
                graphical);
        }
        catch (Exception ex)
        {
            store.AppendEvent(GuardRules.EventStopped, options.ProgramId, Environment.ProcessId,
                "启动主程序失败：" + ex.Message);
            return Report($"启动主程序失败：{ex.Message}", 4);
        }

        Console.WriteLine($"已启动主程序（pid={child.Id}）：{options.ProgramPath}");
        Console.WriteLine("守护已就绪，等待主程序接入……（Ctrl+C 退出）");
        splash?.Stage("等待主程序接入…");

        // 主程序接入（5.2 的 guard.connect 成功）即视为安全系统就绪：启动界面到此收工。
        // 这个挂钩只做界面收尾，不参与任何裁决——许可权威在模块一，界面不是（3.9）。
        // 回调自身不抛异常（见 GuardSplash 的类说明），故这里不需要兜底。
        Action? onProgramConnected = splash is null ? null : splash.Complete;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // 只起**一个**接受循环，把并发上限交给管道自身的实例数（--instances）。
        // 原先按 --instances 起 N 个循环、每个循环各自建 NamedPipeServerStream 且
        // 各自声明 maxNumberOfServerInstances = 1：同一管道名的第二个实例必然拿到
        // ERROR_PIPE_BUSY（"所有的管道范例都在使用中"），于是 7 个循环每秒刷一条"监听异常"，
        // 而真正能服务的连接数仍然只有 1——上限写在循环个数上是写错了地方。
        _ = AcceptLoopAsync(store, supervisor, options.PipeName, options.Instances, onProgramConnected, cts.Token);

        try
        {
            // 主程序退出即视为本次监护结束：宿主启动器不比主程序活得更久。
            await child.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            Console.WriteLine($"主程序已退出（退出码 {child.ExitCode}）。");
        }
        catch (OperationCanceledException)
        {
            // 吞掉的是"Ctrl+C 后等待被取消"这一**预期信号**；下面仍会走停机留痕。
        }
        finally
        {
            cts.Cancel();
            store.AppendEvent(GuardRules.EventStopped, options.ProgramId, Environment.ProcessId, "宿主启动器退出");
        }

        Console.WriteLine("已退出。");
        return 0;
    }

    /// <summary>存储根目录的提示文本（仅用于错误消息；实际落点见 <see cref="GuardStore"/>）。</summary>
    private const string StoreRootHint = @"%LOCALAPPDATA%\Perisc\SafeGuard";

    private const string Usage =
        "用法：Perisc.Safety.SafeGuard --program <主程序.dll> [--program-id <标识>] [--hash sha256:…]\n" +
        "                             [--pipe <管道名>] [--instances N] [--dotnet <dotnet.exe>] [-- <主程序参数…>]\n" +
        "不带参数运行（双击）时自动启用同目录内的主程序；程序标识随之取启动器给出的值，\n" +
        "命令行显式指定 --program-id 时以命令行值为准。";

    private static async Task AcceptLoopAsync(GuardStore store, GuardSupervisor supervisor,
                                              string pipeName, int maxInstances, Action? onProgramConnected,
                                              CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, maxNumberOfServerInstances: maxInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                // 接受即换新监听实例：本管道交给会话，循环重建监听。
                var session = new Session(pipe, store, null, cancellationToken, supervisor, onProgramConnected);
                _ = session.RunAsync().ContinueWith(_ => pipe.Dispose(), TaskScheduler.Default);
                pipe = null; // 所有权移交
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"监听异常：{ex.Message}");
                pipe?.Dispose();
                try
                {
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private static bool TryParse(string[] args, out LaunchOptions options, out string error)
    {
        options = new LaunchOptions();
        error = string.Empty;

        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            switch (argument)
            {
                case "--program":
                    if (i + 1 >= args.Length) { error = "缺少 --program 的取值"; return false; }
                    options.ProgramPath = args[++i];
                    break;
                case "--program-id":
                    if (i + 1 >= args.Length) { error = "缺少 --program-id 的取值"; return false; }
                    options.ProgramId = args[++i];
                    break;
                case "--hash":
                    if (i + 1 >= args.Length) { error = "缺少 --hash 的取值"; return false; }
                    options.DeclaredHash = args[++i];
                    break;
                case "--pipe":
                    if (i + 1 >= args.Length) { error = "缺少 --pipe 的取值"; return false; }
                    options.PipeName = args[++i];
                    break;
                case "--instances":
                    if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out var n) || n is < 1 or > 64)
                    { error = "--instances 取值必须是 1~64"; return false; }
                    options.Instances = n;
                    i++;
                    break;
                case "--dotnet":
                    if (i + 1 >= args.Length) { error = "缺少 --dotnet 的取值"; return false; }
                    options.DotnetHost = args[++i];
                    break;
                case "--":
                    // 其后全部透传给主程序：宿主的参数与主程序的参数必须能分清。
                    for (var j = i + 1; j < args.Length; j++) { rest.Add(args[j]); }
                    i = args.Length;
                    break;
                default:
                    error = "未知参数：" + argument;
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(options.ProgramPath))
        {
            // 双击启动没有任何参数可给，因此缺省时按「同目录唯一应用」推断主程序。
            // 推断不成立时**不猜**：宁可要求显式 --program，也不要拉错程序（fail-closed）。
            var inferred = InferProgramPath(out var detail);
            if (inferred is null)
            {
                error = "未指定 --program，且无法自动推断主程序：" + detail +
                        "。双击启动要求宿主启动器与主程序 DLL 放在同一目录。";
                return false;
            }
            options.ProgramPath = inferred;
        }
        if (!File.Exists(options.ProgramPath))
        {
            error = "主程序 DLL 不存在：" + options.ProgramPath;
            return false;
        }
        if (string.IsNullOrWhiteSpace(options.ProgramId))
        {
            options.ProgramId = Path.GetFileNameWithoutExtension(options.ProgramPath);
        }
        options.ProgramArguments = rest;
        return true;
    }

    /// <summary>
    /// 推断主程序（双击启动用）：同目录里，除启动器自身之外，唯一同时存在同名
    /// <c>&lt;名称&gt;.runtimeconfig.json</c> 的 DLL。
    /// <para>
    /// 判据取 <c>.runtimeconfig.json</c>，因为它是「一个可由 dotnet 宿主直接启动的应用」的
    /// 标志——类库不产出它（本目录里的 <c>Prt.Core.dll</c>、<c>Perisc.Safety.dll</c> 都没有），
    /// 于是能把主程序与依赖库区分开，<b>且不需要在启动器里硬编码主程序名</b>：
    /// 主程序改名后推断照样成立。
    /// </para>
    /// <para>
    /// 候选为 0 个或多个时返回 null（由调用方报错）：多个候选意味着本目录里住着不止一个应用，
    /// 那时"启动哪一个"必须有明确答案，不能靠猜。
    /// </para>
    /// </summary>
    /// <param name="detail">推断结果或失败原因的说明（用于错误消息）。</param>
    /// <returns>推断出的主程序绝对路径；无法唯一确定时为 null。</returns>
    private static string? InferProgramPath(out string detail)
    {
        const string suffix = ".runtimeconfig.json";
        var directory = AppContext.BaseDirectory;
        var self = typeof(Program).Assembly.GetName().Name;

        var candidates = new List<string>();
        foreach (var runtimeConfig in Directory.EnumerateFiles(directory, "*" + suffix, SearchOption.TopDirectoryOnly))
        {
            var stem = Path.GetFileName(runtimeConfig)[..^suffix.Length];
            if (string.Equals(stem, self, StringComparison.OrdinalIgnoreCase))
            {
                continue;   // 启动器自己的 runtimeconfig：它不是主程序
            }
            var assembly = Path.Combine(directory, stem + ".dll");
            if (File.Exists(assembly))
            {
                candidates.Add(assembly);
            }
        }

        if (candidates.Count == 1)
        {
            detail = candidates[0];
            return candidates[0];
        }

        detail = candidates.Count == 0
            ? $"同目录（{directory}）没有找到可启动的主程序 DLL"
            : "同目录有多个候选主程序：" + string.Join("、", candidates.Select(Path.GetFileName));
        return null;
    }

    private sealed class LaunchOptions
    {
        public string ProgramPath = string.Empty;
        public string ProgramId = string.Empty;
        public string? DeclaredHash;
        public string? DotnetHost;
        public string PipeName = DefaultPipeName;
        public int Instances = 8;
        public List<string> ProgramArguments = new();
    }
}
