using System.Windows;
using System.Windows.Media;
using Perisc.Safety;
using Prt.App.Services;
using Prt.App.Theming;

namespace Prt.App.Views;

/// <summary>
/// 激活与授权对话框：展示当前授权状态（限时激活码显示为 VIP），接收离线激活码并即时校验、落盘。
/// <para>
/// 版面约束：正文在 ScrollViewer 内滚动，窗口高度上界钳到屏幕工作区；
/// 「结果提示 + 按钮」固定在底部条。授权信息（签发理由等）再长也不会把输入框和按钮挤出可视区。
/// </para>
/// </summary>
public partial class ActivationWindow : Window
{
    /// <summary>窗口高度相对屏幕工作区的留白（上下各留出屏幕的 5%，避免贴边与遮挡任务栏）。</summary>
    private const double WorkAreaMarginRatio = 0.05;

    public ActivationWindow()
    {
        InitializeComponent();

        // 内容短时窗口照旧自适应高度；内容长于屏幕工作区时在此封顶，改由正文内部滚动。
        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height * (1 - WorkAreaMarginRatio * 2));

        RefreshStatus();
        Loaded += (_, _) => CodeBox.Focus();
    }

    /// <summary>窗口句柄就绪后同步系统标题栏的明暗（深色界面下不再出现浅色标题栏）。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
    }

    private void RefreshStatus()
    {
        StatusText.Text = Activation.CurrentSummary();
        StorageText.Text = Localizer.T("activation.file.hint", Activation.StoragePath);

        // 徽章随授权状态换色：未激活=中性灰，限时 VIP=主题强调色，永久授权=强调色。
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

        RefreshIssuerDetails(license);
    }

    /// <summary>
    /// 显示新版激活码的签发信息（签发时间 / 授权量 / 签发人 / 签发理由）。
    /// 旧版 5 段激活码没有这些字段，整块隐藏而不是显示一串「未填」。
    /// </summary>
    private void RefreshIssuerDetails(LicenseInfo? license)
    {
        if (license is null || !license.HasIssuerInfo)
        {
            IssuerSection.Visibility = Visibility.Collapsed;
            return;
        }

        IssuedAtText.Text = OrNone(license.IssuedAt);

        // 授权量 + 本副本已激活次数（只统计本副本，不做联网统计）。
        var used = DeviceActivation.CountOf(license.Code);
        var seats = license.Seats ?? 0;
        var exceeded = used > seats;
        SeatsText.Text = exceeded
            ? Localizer.T("activation.seats.exceeded", seats, used)
            : Localizer.T("activation.quota.value", seats) + " · " + Localizer.T("activation.used", used);
        SeatsText.Foreground = FindBrush(exceeded
            ? AppPalette.ResourceKeys.Danger
            : AppPalette.ResourceKeys.Text);

        IssuerText.Text = OrNone(license.IssuedBy);
        ReasonText.Text = OrNone(license.Reason);
        IssuerSection.Visibility = Visibility.Visible;
    }

    /// <summary>空字段统一显示词条表里的「未填写」，避免把空白行或占位符暴露给用户。</summary>
    private static string OrNone(string? value)
        => string.IsNullOrWhiteSpace(value) ? Localizer.T("activation.none") : value!;

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

    /// <summary>底部结果提示条：成功 / 失败两种语义色，图标与文字同色。</summary>
    private void ShowMessage(string text, bool success)
    {
        var foreground = FindBrush(success
            ? AppPalette.ResourceKeys.Success
            : AppPalette.ResourceKeys.Danger);

        MessageBar.Background = FindBrush(success
            ? AppPalette.ResourceKeys.SuccessTint
            : AppPalette.ResourceKeys.DangerTint);
        MessageBar.BorderBrush = foreground;
        MessageBar.BorderThickness = new Thickness(1);
        MessageIcon.Text = success ? "\uE73E" : "\uE783";
        MessageIcon.Foreground = foreground;
        MessageText.Foreground = foreground;
        MessageText.Text = text;
        MessageBar.Visibility = Visibility.Visible;
    }

    private Brush FindBrush(string key)
        => (Brush)(TryFindResource(key) as Brush ?? Brushes.Gray);

    private void OnPasteClick(object sender, RoutedEventArgs e)
    {
        // 剪贴板读取经 SRT（DEV-04，3.8）。Value 为 null 即"剪贴板里没有文本"，直接返回。
        var read = SafeRuntime.Device.GetClipboardText("读取剪贴板中的激活码");
        if (!read.Executed || read.Value is null)
        {
            return;
        }

        CodeBox.Text = read.Value.Trim();
        CodeBox.CaretIndex = CodeBox.Text.Length;
        CodeBox.Focus();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        CodeBox.Clear();
        MessageBar.Visibility = Visibility.Collapsed;
        CodeBox.Focus();
    }

    private void OnActivateClick(object sender, RoutedEventArgs e)
    {
        if (!Activation.TryVerify(CodeBox.Text, out var license, out var error))
        {
            ShowMessage(Localizer.T("activation.message.failed", error), success: false);
            return;
        }

        // 落盘失败不阻止本次激活，但必须明说——否则用户会以为已保存，重启后才发现恢复成未激活。
        var saved = Activation.Activate(license, out var saveError);
        RefreshStatus();

        var message = Localizer.T(
            "activation.message.ok",
            license.Username,
            license.Email,
            license.DisplayLevelName,
            license.ExpiryText);

        if (license.Seats is { } seats)
        {
            message += Localizer.T("activation.message.seats", seats);
        }

        if (license.HasIssuerInfo)
        {
            message += Localizer.T("activation.message.used", DeviceActivation.CountOf(license.Code));
        }

        if (!saved)
        {
            ShowMessage(Localizer.T("activation.message.notsaved", saveError), success: false);
            return;
        }

        ShowMessage(message, success: true);
    }
}
