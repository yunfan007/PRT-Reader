using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using Prt.Installer.Pages;
using System.Windows.Controls;
using System.Windows.Media;
using Prt.Installer.Install;

namespace Prt.Installer;

/// <summary>安装向导的四页。</summary>
internal enum Stage
{
    Welcome,
    Options,
    Progress,
    Done,
}

/// <summary>
/// 向导外壳：品牌头 + 步骤条 + 内容区 + 底栏按钮。
/// 页面切换、按钮编排、安装执行都收在这里，页面控件只管自己那一屏的交互。
/// </summary>
public partial class MainWindow : Window
{
    private static readonly Color BrandBlue = Color.FromRgb(0x0E, 0x5B, 0xC5);
    private static readonly Color BrandLight = Color.FromRgb(0x3E, 0x90, 0xFF);
    private static readonly Color InkGray = Color.FromRgb(0x1B, 0x27, 0x33);
    private static readonly Color MutedGray = Color.FromRgb(0x9A, 0xA8, 0xB8);

    private readonly List<StepItem> _steps = new()
    {
        new StepItem { Index = "1", Name = "欢迎" },
        new StepItem { Index = "2", Name = "安装选项" },
        new StepItem { Index = "3", Name = "安装中" },
        new StepItem { Index = "4", Name = "完成" },
    };

    private readonly WelcomePage _welcomePage = new();
    private readonly OptionsPage _optionsPage = new();
    private readonly ProgressPage _progressPage = new();
    private readonly DonePage _donePage = new();

    private Stage _stage = Stage.Welcome;
    private InstallOutcome? _outcome;

    public MainWindow()
    {
        InitializeComponent();
        Steps = _steps;
        Present(Stage.Welcome);

        if (InstallState.Read() is { } installed)
        {
            HeaderSubtitle.Text = $"检测到已安装版本 {installed.Version} 于 {installed.InstallDirectory}，本向导将覆盖它。";
        }
    }

    public IReadOnlyList<StepItem> Steps { get; }

    private void Present(Stage stage)
    {
        _stage = stage;

        object page = stage switch
        {
            Stage.Welcome => _welcomePage,
            Stage.Options => _optionsPage,
            Stage.Progress => _progressPage,
            _ => _donePage,
        };

        ContentFrame.Content = page;
        RefreshStepBar();
        RefreshActionBar();
    }

    /// <summary>底栏按钮按阶段换装：每一步该有几个按钮、点了做什么，都写在这里。</summary>
    private void RefreshActionBar()
    {
        ActionBar.Children.Clear();

        void Add(string label, bool isPrimary, RoutedEventHandler handler, bool enabled = true)
        {
            var style = (Style)FindResource(isPrimary ? "I.PrimaryButton" : "I.SecondaryButton");
            var button = new Button
            {
                Content = label,
                Style = style,
                IsEnabled = enabled,
                Margin = new Thickness(0, 0, 10, 0),
                MinWidth = 96,
            };
            button.Click += handler;
            ActionBar.Children.Add(button);
        }

        switch (_stage)
        {
            case Stage.Welcome:
                Add("下一步", true, (_, _) => Present(Stage.Options));
                break;

            case Stage.Options:
                Add("上一步", false, (_, _) => Present(Stage.Welcome));
                Add("开始安装", true, OnInstall);
                break;

            case Stage.Progress:
                Add("取消安装", false, OnCancel, enabled: _progressPage.CanCancel);
                break;

            case Stage.Done:
                Add("打开安装目录", false, OnOpenInstallDirectory);
                Add("启动阅读器", true, OnLaunchReader);
                break;
        }
    }

    private void RefreshStepBar()
    {
        var currentIndex = (int)_stage;
        for (var i = 0; i < _steps.Count; i++)
        {
            var step = _steps[i];
            var isDone = i < currentIndex;
            var isCurrent = i == currentIndex;

            step.DotFill = new SolidColorBrush(isDone ? BrandBlue : Colors.Transparent);
            step.DotStroke = new SolidColorBrush(isCurrent ? BrandLight : (isDone ? BrandBlue : MutedGray));
            step.DotForeground = new SolidColorBrush(isDone ? Colors.White : (isCurrent ? BrandBlue : MutedGray));
            step.BarFill = new SolidColorBrush(i <= currentIndex ? BrandBlue : MutedGray);
            step.LabelColor = new SolidColorBrush(isCurrent ? BrandBlue : (isDone ? InkGray : MutedGray));
        }
    }

    private void OnInstall(object sender, RoutedEventArgs e)
    {
        var options = _optionsPage.CollectOptions();
        if (string.IsNullOrWhiteSpace(options.InstallDirectory))
        {
            StatusText.Text = "请先选择安装位置。";
            return;
        }

        if (!_optionsPage.IsLocationWritable(options.InstallDirectory))
        {
            var result = MessageBox.Show(
                this,
                "该位置无法写入（可能被占用或无权限）。仍要尝试安装吗？",
                "安装位置不可写",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        Present(Stage.Progress);
        _ = RunInstall(options);
    }

    private async Task RunInstall(InstallOptions options)
    {
        _progressPage.Begin(options);
        StatusText.Text = "正在安装，请稍候…";

        try
        {
            // ConfigureAwait(true)：本进程是单窗口 STA 应用，回到 UI 上下文才能安全更新进度页。
            var outcome = await InstallPlan.RunAsync(
                options,
                _progressPage.Log,
                _progressPage.Phase,
                _progressPage.Cancellation.Token).ConfigureAwait(true);

            _outcome = outcome;
            _progressPage.Succeed();
            _optionsPage.LastOutcome = outcome;
            _donePage.Show(outcome, options);
            Present(Stage.Done);
            StatusText.Text = "安装完成。";
        }
        catch (OperationCanceledException)
        {
            _progressPage.Cancel();
            StatusText.Text = "安装已取消。";
            var retry = MessageBox.Show(this, "安装未完成。是否返回上一步并重新选择安装位置？", "安装已取消",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            Present(retry == MessageBoxResult.Yes ? Stage.Options : Stage.Done);
            if (retry != MessageBoxResult.Yes)
            {
                Application.Current.Shutdown(3);
            }
        }
        catch (Exception ex)
        {
            _progressPage.Fail(ex.Message);
            StatusText.Text = "安装失败：" + ex.Message;
            MessageBox.Show(this, "安装失败：" + ex.Message, "安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _progressPage.RequestCancel();
    }

    private void OnOpenInstallDirectory(object sender, RoutedEventArgs e)
    {
        if (_outcome is not null && Directory.Exists(_outcome.InstallDirectory))
        {
            Process.Start("explorer.exe", _outcome.InstallDirectory);
        }
    }

    private void OnLaunchReader(object sender, RoutedEventArgs e)
    {
        if (_outcome is not null && File.Exists(_outcome.LauncherPath))
        {
            Process.Start(new ProcessStartInfo(_outcome.LauncherPath) { UseShellExecute = true });
        }
    }
}

/// <summary>步骤条的一格（纯展示数据，故只放只读属性。</summary>
public sealed class StepItem
{
    public string Index { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public Brush DotFill { get; set; } = Brushes.Transparent;
    public Brush DotStroke { get; set; } = Brushes.Transparent;
    public Brush DotForeground { get; set; } = Brushes.Transparent;
    public Brush BarFill { get; set; } = Brushes.Transparent;
    public Brush LabelColor { get; set; } = Brushes.Gray;
}
