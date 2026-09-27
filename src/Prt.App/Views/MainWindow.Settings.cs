using System.Windows;
using Prt.App.Models;
using Prt.App.Services;
using Prt.App.Theming;
using StructLayoutKind = System.Runtime.InteropServices.LayoutKind;

namespace Prt.App.Views;

// 设置的捕获、应用与持久化（MainWindow 的 partial；D-06）
// 拆分依据：《工程改进方案》§4.8 / CRS 9.6.4 D-06；拆分**只做位置迁移**，未改行为。
public partial class MainWindow : Window
{
    // ─────────────────────────────── 设置 ───────────────────────────────

    /// <summary>把一份设置整体应用到界面（菜单勾选 + 全部已打开视图）。</summary>
    private void ApplySettingsToUi(AppSettings settings, bool announce)
    {
        _previewTheme = string.Equals(settings.PreviewTheme, AppSettings.PreviewThemeFollow, StringComparison.Ordinal)
            ? null
            : settings.PreviewTheme;

        PreviewThemeFollowMenuItem.IsChecked = _previewTheme is null;
        PreviewThemeDefaultMenuItem.IsChecked = _previewTheme == "default";
        PreviewThemeDarkMenuItem.IsChecked = _previewTheme == "dark";
        PreviewThemePrintMenuItem.IsChecked = _previewTheme == "print";
        PreviewThemeAccessibleMenuItem.IsChecked = _previewTheme == "accessible";

        // 配色：先切外壳，再由各视图重算「跟随」的预览主题（外壳深 → 预览深）。
        SetInterfaceDark(settings.InterfaceDark, persist: false, announce: false);

        SetViewMode(ParseViewMode(settings.DefaultViewMode));

        ShowOutlineMenuItem.IsChecked = settings.ShowOutline;
        ShowProblemsMenuItem.IsChecked = settings.ShowProblems;
        ApplyOutlineVisibility();
        ApplyProblemsVisibility();

        WordWrapMenuItem.IsChecked = settings.WordWrap;
        StrictMenuItem.IsChecked = settings.StrictMode;

        foreach (var entry in _entries)
        {
            entry.View.SetPreviewTheme(_previewTheme);
            entry.View.SetWordWrap(settings.WordWrap);
            entry.View.SetZoom(settings.Zoom);
            entry.View.SetStrictOverride(settings.StrictMode ? true : null);
        }

        UpdateStatusBar();
        UpdateOutline();

        if (announce)
        {
            SetStatus(Localizer.T("status.settings.applied"));
        }
    }

    /// <summary>启动时按已存设置初始化界面。</summary>
    private void ApplyPersistedSettings() => ApplySettingsToUi(SettingsStore.Current, announce: false);

    /// <summary>自检用：走一遍「把一份设置应用到界面」的完整路径（含配色、视图、缩放、折行、严格）。</summary>
    internal void ApplySettingsForTest(AppSettings settings) => ApplySettingsToUi(settings, announce: false);

    /// <summary>自检用：当前界面状态。</summary>
    internal AppSettings CaptureSettingsForTest() => CaptureSettingsFromUi();

    /// <summary>把当前界面状态收敛成一份设置（用于打快照与持久化）。</summary>
    private AppSettings CaptureSettingsFromUi()
    {
        var settings = SettingsStore.Current.Clone();
        settings.InterfaceDark = InterfaceDarkMenuItem.IsChecked == true;
        settings.PreviewTheme = _previewTheme ?? AppSettings.PreviewThemeFollow;
        settings.DefaultViewMode = _viewMode switch
        {
            ViewMode.EditorOnly => AppSettings.ViewModeEditor,
            ViewMode.PreviewOnly => AppSettings.ViewModePreview,
            _ => AppSettings.ViewModeSplit,
        };
        settings.WordWrap = WordWrapMenuItem.IsChecked == true;
        settings.StrictMode = StrictMenuItem.IsChecked == true;
        settings.ShowOutline = ShowOutlineMenuItem.IsChecked == true;
        settings.ShowProblems = ShowProblemsMenuItem.IsChecked == true;
        settings.Zoom = ActiveView?.Zoom ?? SettingsStore.Current.Zoom;
        return settings;
    }

    private static ViewMode ParseViewMode(string? name) => name switch
    {
        AppSettings.ViewModeEditor => ViewMode.EditorOnly,
        AppSettings.ViewModePreview => ViewMode.PreviewOnly,
        _ => ViewMode.Split,
    };

    private void OnViewModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag })
        {
            return;
        }

        SetViewMode(tag switch
        {
            "editor" => ViewMode.EditorOnly,
            "preview" => ViewMode.PreviewOnly,
            _ => ViewMode.Split,
        });
    }

    private void SetViewMode(ViewMode mode)
    {
        _viewMode = mode;
        ViewEditorMenuItem.IsChecked = mode == ViewMode.EditorOnly;
        ViewSplitMenuItem.IsChecked = mode == ViewMode.Split;
        ViewPreviewMenuItem.IsChecked = mode == ViewMode.PreviewOnly;

        ModeEditorToggle.IsChecked = mode == ViewMode.EditorOnly;
        ModeSplitToggle.IsChecked = mode == ViewMode.Split;
        ModePreviewToggle.IsChecked = mode == ViewMode.PreviewOnly;

        foreach (var entry in _entries)
        {
            entry.View.ApplyViewMode(mode);
        }

        StatusModeText.Text = ModeLabel(mode);
    }

    private void OnViewModeToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag })
        {
            return;
        }

        SetViewMode(tag switch
        {
            "editor" => ViewMode.EditorOnly,
            "preview" => ViewMode.PreviewOnly,
            _ => ViewMode.Split,
        });
    }

    private void OnToggleWordWrapClick(object sender, RoutedEventArgs e)
    {
        var view = ActiveView;
        if (view is null)
        {
            return;
        }

        // 菜单项自身已完成勾选切换，这里以勾选状态为准（避免二次取反）。
        // 自动换行是全局设置项（与设置页一致），对所有已打开视图统一生效。
        var wrap = WordWrapMenuItem.IsChecked == true;
        foreach (var entry in _entries)
        {
            entry.View.SetWordWrap(wrap);
        }

        PersistUiSettings();
        SetStatus(wrap ? Localizer.T("status.wordwrap.on") : Localizer.T("status.wordwrap.off"));
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e) => Zoom(+0.1d);

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => Zoom(-0.1d);

    private void OnZoomResetClick(object sender, RoutedEventArgs e) => Zoom(0d);

    private void Zoom(double delta)
    {
        var view = ActiveView;
        if (view is null)
        {
            return;
        }

        var next = Math.Abs(delta) < 0.001 ? 1d : view.Zoom + delta;
        view.SetZoom(next);
        PersistUiSettings();
        SetStatus(Localizer.T("status.zoom", $"{next * 100:0}"));
    }

    private void OnToggleStrictClick(object sender, RoutedEventArgs e)
    {
        var forced = StrictMenuItem.IsChecked == true;
        foreach (var entry in _entries)
        {
            entry.View.SetStrictOverride(forced ? true : null);
        }

        PersistUiSettings();
        SetStatus(forced ? Localizer.T("status.strict.forced") : Localizer.T("status.strict.follow"));
    }

    private void OnInterfaceThemeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag })
        {
            return;
        }

        SetInterfaceDark(tag == "dark", persist: true, announce: true);
    }

    /// <summary>
    /// 切换界面配色。设置页预览时走 <paramref name="persist"/>=false（不落盘）；
    /// 同时通知各视图重算「跟随」的预览主题，避免出现「外壳深色、预览仍是白底」。
    /// </summary>
    private void SetInterfaceDark(bool dark, bool persist, bool announce)
    {
        InterfaceLightMenuItem.IsChecked = !dark;
        InterfaceDarkMenuItem.IsChecked = dark;
        App.ApplyPalette(AppPalette.Get(dark));

        foreach (var entry in _entries)
        {
            entry.View.NotifyInterfaceThemeChanged();
        }

        if (persist)
        {
            PersistUiSettings();
        }

        if (announce)
        {
            SetStatus(dark ? Localizer.T("status.interface.dark") : Localizer.T("status.interface.light"));
        }
    }

    /// <summary>「设置写不进程序目录」是否已弹窗提示过（同一原因只提示一次）。</summary>
    private bool _settingsSaveFailureReported;

    /// <summary>把当前界面状态写回 settings.json（菜单直接改动设置项时调用）。</summary>
    private void PersistUiSettings()
    {
        SettingsStore.Update(CaptureSettingsFromUi());
        if (SettingsStore.TrySave(out var error))
        {
            return;
        }

        // 设置只落程序目录（《设计取舍》第 13 条），写不进去必须让用户知道，
        // 否则他会以为改动已保存、下次启动却发现全丢。
        // 提示只报一次：同一个原因不必每点一次菜单就弹一遍。
        SetStatus(Localizer.T("settings.save.failed", error));
        if (_settingsSaveFailureReported)
        {
            return;
        }

        _settingsSaveFailureReported = true;
        MessageBox.Show(this, Localizer.T("settings.save.failed", error), Localizer.T("app.name"),
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnPreviewThemeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag })
        {
            return;
        }

        _previewTheme = tag == "follow" ? null : tag;

        PreviewThemeFollowMenuItem.IsChecked = tag == "follow";
        PreviewThemeDefaultMenuItem.IsChecked = tag == "default";
        PreviewThemeDarkMenuItem.IsChecked = tag == "dark";
        PreviewThemePrintMenuItem.IsChecked = tag == "print";
        PreviewThemeAccessibleMenuItem.IsChecked = tag == "accessible";

        foreach (var entry in _entries)
        {
            entry.View.SetPreviewTheme(_previewTheme);
        }

        PersistUiSettings();
        UpdateStatusBar();
        SetStatus(tag == "follow"
            ? Localizer.T("status.previewtheme.follow")
            : Localizer.T("status.previewtheme", tag));
    }

}
