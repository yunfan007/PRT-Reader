using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using Prt.Installer.Install;

namespace Prt.Uninstaller;

/// <summary>
/// 卸载器入口。
/// `--cleanup` 是 self-delete 的续命模式：卸载界面把可执行文件挪到 %TEMP% 之后启动的那个副本，
/// 等原进程真正退出、文件句柄释放干净，再删掉残留。它不显示窗口、不闪黑窗。
/// `/quiet` 是静默卸载：不弹窗口跑完整套清理，收尾走同一套自删。
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// <see cref="Application.OnStartup(StartupEventArgs)"/> 是框架定的 <c>void</c> 签名，
    /// 签名改不了，所以这里保持同步，异步部分下沉到 <see cref="CleanupAsync"/> /
    /// <see cref="RunSilentAsync"/> 两个 <c>async Task</c> 方法里。
    /// 原先这里直接写 <c>async void</c>：表面上省事，实则异常既无处承接、调用方也无法等待
    /// ——CRS 9.6.4 D-22 明列非事件处理器的 <c>async void</c> 为扣分/必修项。
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length > 0 && e.Args[0] == "--cleanup")
        {
            CleanupAsync(e.Args).GetAwaiter().GetResult();
            return;
        }

        if (e.Args.Length > 0 && e.Args[0] == "/quiet")
        {
            RunSilentAsync(e.Args).GetAwaiter().GetResult();
            return;
        }
    }

    /// <summary>
    /// 自删续命模式（<c>--cleanup</c>）的收尾：等原进程交还句柄，删掉它，再退出。
    /// </summary>
    private static async Task CleanupAsync(string[] args)
    {
        // 给原进程留出退出的时间：它释放句柄之前，删除会失败。
        await Task.Delay(2000).ConfigureAwait(true);

        try
        {
            if (args.Length > 1 && args[1].Length > 0 && File.Exists(args[1]))
            {
                File.Delete(args[1]);
            }

            var self = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(self) && File.Exists(self))
            {
                File.Delete(self);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 残留一两个文件比"卡在卸载界面"好，交给用户手工删。
        }

        Current.Shutdown(0);
    }

    /// <summary>
    /// 静默卸载：给批量部署与自动化验证用，不弹任何窗口。
    /// 与向导版走同一条 UninstallPlan，因此"卸干净"的定义在两种入口下完全一致。
    /// </summary>
    private static async Task RunSilentAsync(string[] args)
    {
        var logPath = args.FirstOrDefault(a => a.StartsWith("/log=", StringComparison.Ordinal))
                      ?.Substring("/log=".Length);
        var log = (string message) =>
        {
            if (string.IsNullOrEmpty(logPath))
            {
                return;                       // 没指定日志就静默，不为了"写日志"去造一个文件
            }

            try
            {
                File.AppendAllText(logPath, message + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 日志写不进去不该拖垮卸载本身。
            }
        };

        var state = InstallState.Read();
        if (state is null)
        {
            log("未检测到安装信息（HKCU\\Software\\PRT Reader）。");
            Current.Shutdown(2);
            return;
        }

        log("开始静默卸载：" + state.InstallDirectory);
        var outcome = new UninstallPlan(state, log).Run();
        log(outcome.DirectoryRemoved ? "安装目录已删除。" : "安装目录未能删除（仍有文件被占用）。");

        // 自删：卸载器就住在安装目录里，目录删不掉往往正是因为它自己还开着。
        // 把当前 exe 复制到 %TEMP% 后由那个副本收尾——与向导版同一套做法，
        // 既不闪 cmd 黑窗，也能把"正在被自己占用"的文件删干净。
        SelfDelete();
    }

    /// <summary>
    /// 先把自身复制到 %TEMP%\prt-uninstall\ 再启动它去删原文件，然后立刻退出。
    /// 复制出来的副本带 <c>--cleanup</c> 参数（见本文件开头），等两秒让原进程交还句柄。
    /// </summary>
    private static void SelfDelete()
    {
        var original = Environment.ProcessPath;
        if (string.IsNullOrEmpty(original) || !File.Exists(original))
        {
            return;
        }

        var staging = Path.Combine(Path.GetTempPath(), "prt-uninstall");
        Directory.CreateDirectory(staging);
        var copy = Path.Combine(staging, Path.GetFileName(original));

        try
        {
            File.Copy(original, copy, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;                             // 复制不了就别折腾，残留文件留给用户手工删
        }

        var psi = new ProcessStartInfo(copy);
        psi.ArgumentList.Add("--cleanup");
        psi.ArgumentList.Add(original);
        psi.UseShellExecute = false;
        psi.WindowStyle = ProcessWindowStyle.Hidden;
        try
        {
            Process.Start(psi);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return;
        }

        Environment.Exit(0);
    }
}
