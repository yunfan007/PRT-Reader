using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Prt.App.Services;

namespace Prt.App.Views;

/// <summary>
/// 总设置页：把分散在菜单里的显示 / 编辑 / 启动 / 语言选项集中到一处。
/// <para>
/// 交互口径：**一改即变**。每次选项变化都把整份草稿通过 <see cref="SettingsChanged"/>
/// 交给主窗口立即应用（仅预览、不落盘）；点「确定」由主窗口写入 settings.json，
/// 取消或直接关闭窗口则由主窗口回滚到打开设置页之前的状态。
/// </para>
/// <para>
/// 启动图是文件操作，**只在确认后**才真正复制进用户数据目录；在此之前本窗口只持有
/// 用户选中的源文件路径（<see cref="PendingSplashSource"/>），取消不会产生任何文件。
/// </para>
/// </summary>
/// <remarks>
/// 可见性必须与 XAML 生成的 <c>public partial</c> 一致（否则 CS0262）。
/// </remarks>
public partial class SettingsWindow : Window
{
    /// <summary>初始化期间抑制变更事件（避免 LoadFrom 触发一轮无意义的预览）。</summary>
    private bool _loading;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    /// <summary>窗口句柄就绪后同步系统标题栏的明暗（深色界面下不再出现浅色标题栏）。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Theming.DarkTitleBar.Apply(this);
    }

    /// <summary>设置草稿；确认后由主窗口落盘。</summary>
    public AppSettings Draft { get; private set; } = new();

    /// <summary>用户新选的启动图源文件；null 表示本次不改动启动图。</summary>
    public string? PendingSplashSource { get; private set; }

    /// <summary>用户点了「恢复默认」，即清掉自定义启动图。</summary>
    public bool ResetSplashRequested { get; private set; }

    /// <summary>任一选项变化时触发（参数为最新草稿）。</summary>
    public event EventHandler<AppSettings>? SettingsChanged;

    /// <summary>用主窗口的当前状态初始化各项，保证打开设置时看到的与实际一致。</summary>
    public void LoadFrom(AppSettings settings)
    {
        _loading = true;
        Draft = settings.Clone();

        InterfaceBox.SelectedIndex = Draft.InterfaceDark ? 1 : 0;
        PreviewThemeBox.SelectedIndex = Draft.PreviewTheme switch
        {
            "default" => 1,
            "dark" => 2,
            "print" => 3,
            "accessible" => 4,
            _ => 0,
        };
        ZoomSlider.Value = Math.Clamp(Draft.Zoom * 100d, ZoomSlider.Minimum, ZoomSlider.Maximum);
        ZoomText.Text = $"{ZoomSlider.Value:0}%";
        WordWrapBox.IsChecked = Draft.WordWrap;
        StrictBox.IsChecked = Draft.StrictMode;
        ViewModeBox.SelectedIndex = Draft.DefaultViewMode switch
        {
            AppSettings.ViewModeEditor => 0,
            AppSettings.ViewModePreview => 2,
            _ => 1,
        };
        SplashBox.IsChecked = Draft.ShowSplash;
        LanguageBox.SelectedIndex = Draft.Language switch
        {
            Localizer.Chinese => 1,
            Localizer.English => 2,
            _ => 0,
        };

        RefreshSplashPreview();
        _loading = false;
    }

    private void OnZoomSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ZoomText is not null)
        {
            ZoomText.Text = $"{e.NewValue:0}%";
        }

        OnAnySettingChanged(sender, e);
    }

    private void OnAnySettingChanged(object sender, RoutedEventArgs e)
    {
        // 两重保护：
        // ① _loading：LoadFrom 期间逐项赋值会触发事件，此时不应把「半成品」当成用户改动；
        // ② !IsInitialized：XAML 装载期也会触发一次——Slider 的 Minimum/Maximum 会把 Value
        //    从 0 钳到下限从而抛 ValueChanged，而那一刻后面的页签（WordWrapBox 等）还没被解析，
        //    Collect() 会读到空字段并抛 NullReferenceException，表现为「设置窗口打不开」。
        if (_loading || !IsInitialized)
        {
            return;
        }

        Collect();
        SettingsChanged?.Invoke(this, Draft);
    }

    /// <summary>把界面上的选择收进草稿。</summary>
    private void Collect()
    {
        Draft.InterfaceDark = InterfaceBox.SelectedIndex == 1;
        Draft.PreviewTheme = PreviewThemeBox.SelectedIndex switch
        {
            1 => "default",
            2 => "dark",
            3 => "print",
            4 => "accessible",
            _ => AppSettings.PreviewThemeFollow,
        };
        Draft.Zoom = ZoomSlider.Value / 100d;
        Draft.WordWrap = WordWrapBox.IsChecked == true;
        Draft.StrictMode = StrictBox.IsChecked == true;
        Draft.DefaultViewMode = ViewModeBox.SelectedIndex switch
        {
            0 => AppSettings.ViewModeEditor,
            2 => AppSettings.ViewModePreview,
            _ => AppSettings.ViewModeSplit,
        };
        Draft.ShowSplash = SplashBox.IsChecked == true;
        Draft.Language = LanguageBox.SelectedIndex switch
        {
            1 => Localizer.Chinese,
            2 => Localizer.English,
            _ => Localizer.SystemDefault,
        };
        Draft.Normalize();
    }

    private void OnBrowseSplashClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Localizer.T("settings.splash.image"),
            Filter = "图片 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        PendingSplashSource = dialog.FileName;
        ResetSplashRequested = false;
        RefreshSplashPreview();
    }

    private void OnResetSplashClick(object sender, RoutedEventArgs e)
    {
        PendingSplashSource = null;
        ResetSplashRequested = true;
        RefreshSplashPreview();
    }

    /// <summary>
    /// 刷新启动图预览：优先显示本次新选的图片，其次显示已保存的自定义图，
    /// 都没有时退回「内置图片」的画法示意。
    /// </summary>
    private void RefreshSplashPreview()
    {
        var path = PendingSplashSource
                   ?? (ResetSplashRequested ? null : SettingsStore.ResolveSplashImage());

        // 不在预览前做 File.Exists 探测：那是绕开 SRT 的文件访问（3.8），
        // 而这条路径本来就有 try/catch——图片读不出来（不存在 / 损坏 / 无权限）一律退回内置预览。
        if (path is not null)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                SplashPreview.Source = bitmap;
                SplashPreview.Visibility = Visibility.Visible;
                SplashBuiltinPreview.Visibility = Visibility.Collapsed;
                return;
            }
            catch
            {
                // 图片读不出来就退回内置预览，不打断设置流程。
            }
        }

        SplashPreview.Source = null;
        SplashPreview.Visibility = Visibility.Collapsed;
        SplashBuiltinPreview.Visibility = Visibility.Visible;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        Collect();
        DialogResult = true;
    }
}
