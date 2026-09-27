using System.Collections.ObjectModel;
using System.Windows.Controls;
using Prt.Installer.Install;

namespace Prt.Installer.Pages;

/// <summary>
/// 安装进度页。释放素材是 IO 密集且时长不均，所以进度条走不确定态、进度以"阶段 + 日志"表达，
/// 比假装能报精确百分比更可信。
/// </summary>
public partial class ProgressPage : UserControl
{
    private readonly ObservableCollection<string> _lines = new();

    public ProgressPage()
    {
        InitializeComponent();
        LogList.ItemsSource = _lines;
    }

    public CancellationTokenSource Cancellation { get; } = new();

    public bool CanCancel { get; private set; } = true;

    public void Begin(InstallOptions options)
    {
        _lines.Clear();
        Overall.IsIndeterminate = true;
        PhaseText.Text = "准备安装…";
        CanCancel = true;
        Log("目标位置：" + options.InstallDirectory);
        Log("文件关联：" + (options.AssociateFiles ? "开启" : "关闭") +
            "　右键菜单：" + (options.ShellMenus ? "开启" : "关闭"));
    }

    /// <summary>阶段文字（每一步安装干什么）。</summary>
    public void Phase(string title) => Dispatcher.Invoke(() => PhaseText.Text = title);

    /// <summary>一条日志。</summary>
    public void Log(string line) => Dispatcher.Invoke(() =>
    {
        _lines.Add(line);
        if (_lines.Count > 400)
        {
            _lines.RemoveAt(0);
        }

        // 日志是"最新在下面"的阅读习惯，滚到底即可。
        if (LogList.Items.Count > 0)
        {
            LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
        }
    });

    public void Succeed()
    {
        Overall.IsIndeterminate = false;
        Overall.Value = 100;
        PhaseText.Text = "安装完成";
        CanCancel = false;
    }

    public void Fail(string message)
    {
        Overall.IsIndeterminate = false;
        PhaseText.Text = "安装失败";
        Log("失败：" + message);
        CanCancel = false;
    }

    public void Cancel()
    {
        Overall.IsIndeterminate = false;
        PhaseText.Text = "已取消";
        CanCancel = false;
        Log("已请求取消安装。");
    }

    /// <summary>取消是"尽力而为"：正在写 160 MB 运行时时不是立刻能停，界面给一次请求即可。</summary>
    public void RequestCancel()
    {
        Cancellation.Cancel();
        Cancel();
    }
}
