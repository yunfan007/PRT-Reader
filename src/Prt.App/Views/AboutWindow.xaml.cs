using System.Windows;
using System.Windows.Media;
using Prt.App.Services;
using Prt.App.Theming;
using Prt.Core;

namespace Prt.App.Views;

/// <summary>
/// 「关于」对话框：品牌标、程序名与版本、符合性等级、功能一览、当前授权与落款。
/// <para>
/// 「符合性声明 / 快捷键 / 激活与授权」三个动作由调用方（主窗口）注入，
/// 与「帮助」菜单共用同一套实现，避免同一段文案在两处各写一份。
/// </para>
/// </summary>
public partial class AboutWindow : Window
{
    /// <summary>窗口高度相对屏幕工作区的留白（上下各留出屏幕的 5%）。</summary>
    private const double WorkAreaMarginRatio = 0.05;

    public AboutWindow()
    {
        InitializeComponent();

        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height * (1 - WorkAreaMarginRatio * 2));

        VersionText.Text = Localizer.T("about.version", SplashWindow.AppVersion);

        // 符合性等级取自类库的能力声明，避免版本号在本窗口里被写死而与代码脱节。
        SpecText.Text = Localizer.T("about.spec", PrtCapabilities.SpecificationVersion);

        RefreshLicense();
    }

    /// <summary>「符合性声明」的落地动作（由主窗口注入）。</summary>
    public Action? ShowComplianceRequested { get; set; }

    /// <summary>「快捷键」的落地动作（由主窗口注入）。</summary>
    public Action? ShowShortcutsRequested { get; set; }

    /// <summary>「激活与授权」的落地动作（由主窗口注入）。</summary>
    public Action? ShowActivationRequested { get; set; }

    /// <summary>窗口句柄就绪后同步系统标题栏的明暗（深色界面下不再出现浅色标题栏）。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
    }

    private void RefreshLicense()
    {
        LicenseText.Text = Activation.CurrentSummary();
        StorageText.Text = Activation.StoragePath;

        var license = Activation.Current;
        if (license is null)
        {
            SetBadge(Localizer.T("activation.badge.none"), neutral: true);
        }
        else if (license.IsPerpetual)
        {
            SetBadge(license.LevelName, neutral: false);
        }
        else
        {
            SetBadge(Localizer.T("activation.badge.vip"), neutral: false);
        }
    }

    private void SetBadge(string text, bool neutral)
    {
        LevelBadgeText.Text = text;
        if (neutral)
        {
            LevelBadge.Background = FindBrush(AppPalette.ResourceKeys.ControlFill);
            LevelBadge.BorderBrush = FindBrush(AppPalette.ResourceKeys.Border);
            LevelBadgeText.Foreground = FindBrush(AppPalette.ResourceKeys.SubtleText);
        }
        else
        {
            LevelBadge.Background = FindBrush(AppPalette.ResourceKeys.Accent);
            LevelBadge.BorderBrush = FindBrush(AppPalette.ResourceKeys.Accent);
            LevelBadgeText.Foreground = FindBrush(AppPalette.ResourceKeys.OnAccent);
        }
    }

    private Brush FindBrush(string key)
        => (Brush)(TryFindResource(key) as Brush ?? Brushes.Gray);

    private void OnActivationClick(object sender, RoutedEventArgs e)
    {
        ShowActivationRequested?.Invoke();

        // 激活窗口是模态的，返回时本窗口仍需显示旧授权——回来补一次刷新，避免卡片与徽章停在旧状态。
        RefreshLicense();
    }

    private void OnComplianceClick(object sender, RoutedEventArgs e) => ShowComplianceRequested?.Invoke();

    private void OnShortcutsClick(object sender, RoutedEventArgs e) => ShowShortcutsRequested?.Invoke();
}
