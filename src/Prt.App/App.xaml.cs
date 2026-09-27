using System.Windows;
using System.Windows.Threading;
using Prt.App.Services;
using Prt.App.Theming;
using Prt.App.Views;

namespace Prt.App;

/// <summary>
/// 应用程序入口。负责初始化界面配色与语言、接管未处理异常，并创建启动界面与主窗口。
/// </summary>
public partial class App : Application
{
    /// <summary>当前界面配色（供各视图查询当前明暗状态）。</summary>
    public static AppPalette Palette { get; private set; } = AppPalette.Light();

    /// <summary>
    /// 异常诊断日志的文件名。落点＝**程序所在文件夹**（本程序不写用户其它目录，
    /// 见《设计取舍》第 13 条）；程序目录不可写时静默降级为内存计数，不改投别处。
    /// </summary>
    private const string ExceptionLogFileName = "prt-app-exception.log";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 自检模式：验证解析 / 渲染 / 导出链路后退出，不显示任何窗口。
        var selfTest = e.Args.Length > 0 && string.Equals(e.Args[0], "--selftest", StringComparison.OrdinalIgnoreCase);

        // 便携化：把历史版本写在 %APPDATA% 下的本程序配置搬进程序目录（只做一次，
        // 见《设计取舍》第 13 条）。自检**不**执行——自检必须无副作用地跑，
        // 不该去搬、更不该去删用户的真实文件。
        if (!selfTest)
        {
            LegacyConfigMigration.RunOnce();
        }

        // 设置与语言必须最先加载：界面文案在 XAML 加载期解析，配色也要在窗口构造前定好。
        SettingsStore.Load();
        Localizer.Apply(SettingsStore.Current.Language);
        ApplyPalette(AppPalette.Get(SettingsStore.Current.InterfaceDark));

        // 恢复已存储的授权（未激活或已过期按免费版运行）。
        Activation.LoadStoredLicense();

        if (selfTest)
        {
            var reportPath = e.Args.Length > 1 ? e.Args[1] : null;
            // 自检无头接入：自检过程扮演用户角色自动允许申报（默认拒绝只作用于真实运行）。
            Services.SafetyBridge.InitializeHeadless();
            // 自检要覆盖异步链路（申报、会话恢复、界面回调），所以它本身也是异步的。
            // OnStartup 无法 await：这里起一个不等待的任务，跑完再按退出码关停——
            // OnStartup 返回后消息循环照常运行，异步用例因此能真正跑起来。
            _ = RunSelfTestAsync(reportPath);
            return;
        }

        // PSS 安全模块（接入模式 A）：创建模块一/二、注册界面挂钩、连接守护服务。
        Services.SafetyBridge.Initialize();

        // 启动界面：无界面模式（自检）不会走到这里。主窗口构造期间一直显示，
        // 就绪后淡出——至少显示 0.8 秒，避免启动过快时一闪而过。
        SplashWindow? splash = null;
        if (SettingsStore.Current.ShowSplash)
        {
            splash = new SplashWindow();
            splash.Show();
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // 不等待、也不观察返回值：本方法是"启动画面自行淡出"，与启动流程无因果关系
        // （启动不该等它）。它内部已完整 try/catch，不会把异常漏到无人承接的地方；
        // 显式的 `_ =` 是为了让"这次确实不打算等"成为代码里看得见的事实，而不是被编译器沉默放过。
        _ = splash?.DismissAfterMinimum();

        // 支持命令行直接打开文件：Prt.App.exe <文件路径> [...]
        if (e.Args.Length > 0)
        {
            // 打开要过安全申报，申报必须能回到 UI 线程，因而不能在 OnStartup 里同步等。
            // 这里不等待也不观察：OnStartup 返回后消息循环照常跑，申报弹窗与打开流程继续完成。
            // OpenPathAsync 内部已完整 try/catch（失败弹提示），不会把异常漏到无人承接的地方。
            _ = OpenCommandLineFilesAsync(window, e.Args);
        }
    }

    private static async Task OpenCommandLineFilesAsync(MainWindow window, string[] args)
    {
        try
        {
            await window.OpenPathsFromCommandLineAsync(args);
        }
        catch (Exception ex)
        {
            // 吞掉的是"命令行参数打开失败"；降级到"窗口照常可用，用户可手动打开"。
            // 何时应传播：不需要——启动路径上的任意失败都不该让主窗口打不开。
            MessageBox.Show(
                "命令行指定的文件未能打开：\n" + ex.Message,
                "PRT 阅读器",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>跑自检并按退出码关停；自检自身的异常一律折算为失败码。</summary>
    private static async Task RunSelfTestAsync(string? reportPath)
    {
        var code = 1;
        try
        {
            code = await Services.SelfTest.RunAsync(reportPath);
        }
        catch (Exception ex)
        {
            // 吞掉的是"自检框架自身的异常"；降级到"退出码 1 + 报告里记一笔"。
            // 若让异常逃逸，进程会以未处理异常结束，报告文件可能根本没写出来——
            // 那比"自检失败"更难排查。
            try
            {
                if (!string.IsNullOrWhiteSpace(reportPath))
                {
                    // 报告落盘经 SRT（3.8）：报告路径由命令行给出，可能是用户目录也可能是程序目录，
                    // 行为编号交给 SRT 按落点归类，本处不再自己判断。
                    Perisc.Safety.SafeRuntime.File.AppendText(
                        reportPath!,
                        "\n[失败] 自检框架异常 —— " + ex.GetType().Name + "：" + ex.Message + "\n",
                        "写入自检报告");
                }
            }
            catch
            {
                // 连报告都写不出：只剩退出码可表达失败，不再往上报（此处也没有更下游的处置方）。
            }
            code = 1;
        }
        Current.Shutdown(code);
    }

    /// <summary>切换界面配色（浅色 / 深色）。</summary>
    public static void ApplyPalette(AppPalette palette)
    {
        Palette = palette;
        AppPalette.Apply(Current, palette);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 释放安全模块：Dispose 不撤销许可、不删除审计（PSS 4.10 第 5 条）。
        Services.SafetyBridge.Shutdown();
        base.OnExit(e);
    }

    /// <summary>
    /// 异常日志写入失败次数。
    /// 这条路径没有界面可报（异常本身来自 UI 线程崩溃），所以降级出口只能是"留一个可观察的计数"——
    /// 否则写盘失败会被彻底吞掉，事后既无日志也无痕迹（9.6.4 / D-09 要防的正是这种情形）。
    /// </summary>
    internal static int ExceptionLogWriteFailures { get; private set; }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 异常落盘：无界面场景（自检等）便于事后定位。
        // 落点是**程序所在文件夹**（便携化，见《设计取舍》第 13 条）。这类「本程序自己在程序目录里
        // 写出的运行期数据」按 PSS 4.6 不在 63 项内，故不经 SRT 申报（理由与边界见 PortableStorage
        // 类注释）——也因此它不会在崩溃兜底这条路径上弹授权窗。
        // 程序目录不可写时**不**改投 %TEMP%：本程序的落点只有程序目录一处，写不进去就降级为内存计数。
        if (!Services.PortableStorage.TryAppendText(
                ExceptionLogFileName,
                // 异常日志要跨环境可比对与解析，时刻格式显式用不变文化：
                // 在 th-TH / ar-SA 等区域下，默认格式化会输出佛历 / 回历年份或非 ASCII 数字。
                AppClock.Now().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                    + "  " + e.Exception.ToString() + Environment.NewLine,
                "写入异常诊断日志",
                out _))
        {
            // 降级到「内存计数」：这条路径没有更下游的处置方（异常已在下方统一兜底提示用户，
            // 日志只是事后定位手段），留一个可观察的计数就是它唯一的失败出口。
            // 这里**不能**改用 MessageBox 补偿：正处在崩溃兜底路径上，弹窗本身就是二次故障源。
            ExceptionLogWriteFailures++;
        }
        // UI 线程异常统一兜底，避免直接崩溃丢失未保存内容。
        MessageBox.Show(
            "发生未处理的异常，当前操作已中止：\n\n" + e.Exception.Message,
            "PRT 阅读器",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
