using System.IO;
using System.Windows;
using System.Windows.Threading;
using Prt.Installer.Install;

namespace Prt.Installer;

/// <summary>安装器入口。双击即向导；`/uninstall` 直接进卸载页，`/install` 走静默安装。</summary>
public partial class App : Application
{
    /// <summary>
    /// 静态构造里挂钩进程级异常：OnStartup 里那个 DispatcherUnhandledException 拦不到
    /// **Application 自身装载**（App.xaml 的那次 XAML 解析就发生在 OnStartup 之前），
    /// 而恰恰是这一段的失败最要命——窗口还没出现，界面上什么也没有，
    /// 事件查看器里只有一句"代码执行异常"。此处先落盘，事后才有得查。
    /// </summary>
    static App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                WriteCrash(ex);
            }
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 兜底：向导与静默安装都不该"无声无息地消失"。任何一个都没来得及处理的异常，
        // 都先落到日志里——装了一半没装完却没有任何交代，是最难支援的一种失败。
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrash(args.Exception);
            args.Handled = true;            // 已落盘，交给下面的收尾逻辑决定要不要继续
        };

        if (e.Args.Length == 0)
        {
            return;
        }

        var head = e.Args[0];

        // 支持 `PRT-Installer.exe /uninstall` 直接进卸载页：重装前常常想先卸干净的入口。
        if (head.Equals("/uninstall", StringComparison.OrdinalIgnoreCase))
        {
            if (InstallState.Read() is null)
            {
                Shutdown(2);
                return;
            }

            // 交给安装目录里的卸载器，避免安装器自己卸载自己（文件占用）。
            Shutdown(1);
            return;
        }

        if (head.Equals("/install", StringComparison.OrdinalIgnoreCase))
        {
            _ = RunSilentAsync(e.Args);
        }
    }

    /// <summary>
    /// 静默安装。这里刻意**不创建任何窗口**——Application 没有 Window 也照常跑，
    /// 而弹一个"正在安装"的假窗口再自动关掉，只会让远程部署的机器上多一次莫名其妙的闪窗。
    /// </summary>
    private static async Task RunSilentAsync(string[] args)
    {
        int exitCode;
        try
        {
            exitCode = await SilentInstall.RunAsync(args).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 这里兜的是"连 SilentInstall 自己的 try 都没罩住"的那类异常
            // （多半是在 RunAsync 之前就炸了）。宁可把异常写全，也不要留一个 0xE0434352。
            WriteCrash(ex);
            exitCode = 1;
        }

        Current.Shutdown(exitCode);
    }

    /// <summary>把异常写进 <c>/log=</c> 指定的日志，没指定就写在 %TEMP% 下。</summary>
    private static void WriteCrash(Exception exception)
    {
        var path = Path.Combine(Path.GetTempPath(), "prt-installer-crash.log");
        try
        {
            File.AppendAllText(
                path,
                "PRT 安装器异常（" +
                InstallClock.Now().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) +
                "）" +
                Environment.NewLine + exception.ToString() + Environment.NewLine + DescribeXamlLocation(exception) +
                Environment.NewLine);
        }
        catch (Exception logEx) when (logEx is IOException or UnauthorizedAccessException)
        {
            // 连日志都写不进去时，就不必再为它做什么了。
        }
    }

    /// <summary>
    /// 把 XAML 解析失败的位置补在栈后面。<c>exception.ToString()</c> 只有"行号 10，位置 23"，
    /// 而这一串数字指的到底是 App.baml 还是某个 Page.baml，不读栈上是看不出来的——
    /// XamlParseException 恰好带 BaseUri，正好用来回答这个问题。
    /// </summary>
    private static string DescribeXamlLocation(Exception exception)
    {
        var lines = new List<string>();
        for (var node = exception; node is not null; node = node.InnerException)
        {
            if (node is not System.Windows.Markup.XamlParseException xaml) continue;
            lines.Add("XAML 位置：BaseUri=" + (xaml.BaseUri?.ToString() ?? "(无)") +
                      " 行号=" + xaml.LineNumber + " 位置=" + xaml.LinePosition);
        }

        return lines.Count == 0 ? string.Empty : string.Join(Environment.NewLine, lines);
    }
}
