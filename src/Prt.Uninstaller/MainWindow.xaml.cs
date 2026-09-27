using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Prt.Installer.Install;
using Prt.Uninstaller.Platform;

namespace Prt.Uninstaller;

/// <summary>卸载向导：确认 → 清理 → 完成（完成后自删，见 <see cref="SelfDelete"/>）。</summary>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<string> _lines = new();
    private InstallStateSnapshot? _state;
    private bool _finished;

    public MainWindow()
    {
        InitializeComponent();
        LogList.ItemsSource = _lines;

        _state = InstallState.Read();
        if (_state is null)
        {
            WhereText.Text = "未检测到本程序的安装信息（HKCU\\Software\\PRT Reader 不存在）。";
            UninstallButton.IsEnabled = false;
            return;
        }

        WhereText.Text = "安装目录：" + _state.InstallDirectory;
        Subtitle.Text = "版本 " + (_state.Version.Length > 0 ? _state.Version : "未知") + "　·　按用户安装，卸载不影响其他程序";
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (_state is null || _finished)
        {
            return;
        }

        UninstallButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        Log("开始卸载…");

        var plan = new UninstallPlan(_state, Log);

        // 单窗口 STA 应用：必须回到 UI 上下文才能安全动控件，故用 ConfigureAwait(true)
        // （ analyser 只在缺显式配置时才报 CA2007）。
        var outcome = await Task.Run(() => plan.Run()).ConfigureAwait(true);

        _finished = true;
        UninstallButton.Content = "完成";
        UninstallButton.Click -= OnUninstall;
        UninstallButton.Click += OnFinish;
        UninstallButton.IsEnabled = true;

        var tail = outcome.DirectoryRemoved
            ? "安装目录已删除。"
            : "仍有文件被占用，安装目录未能删除，请关闭阅读器后重试。";
        Log(tail);
        Subtitle.Text = tail;

        if (outcome.DirectoryRemoved)
        {
            // 目录删空之后自身也在其中：把自己挪到 %TEMP% 让那一份收尾，避免"卸载程序删不掉自己"。
            SelfDelete();
        }
    }

    private void OnFinish(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Log(string line) => Dispatcher.Invoke(() =>
    {
        _lines.Add(line);
        if (LogList.Items.Count > 0)
        {
            LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
        }
    });

    /// <summary>
    /// 自删：把当前可执行文件移动到 %TEMP% 并启动那个副本，由它等待原进程退出后
    /// 删除安装目录、删除自身。全程不起 cmd、不弹黑窗口——本工程对"任何情况下都不能有黑窗"
    /// 的标准与阅读器一致。
    /// </summary>
    private void SelfDelete()
    {
        string? selfPath = null;
        try
        {
            selfPath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(selfPath) || !File.Exists(selfPath))
            {
                return;
            }

            var tempDirectory = Path.Combine(Path.GetTempPath(), "prt-uninstall");
            Directory.CreateDirectory(tempDirectory);
            var stagedPath = Path.Combine(tempDirectory, Path.GetFileName(selfPath));

            File.Copy(selfPath, stagedPath, true);
            Process.Start(new ProcessStartInfo(stagedPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = "--cleanup \"" + selfPath + "\"",
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log("自删失败（可手工删除安装目录）：" + ex.Message);
        }

        Environment.Exit(0);
    }
}
