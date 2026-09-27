using System.Windows;
using System.Windows.Media;
using Prt.App.Rendering;

namespace Prt.App.Theming;

/// <summary>
/// 应用界面配色（窗口 / 面板 / 菜单 / 编辑器等外壳的颜色集合）。
/// <para>
/// 取值对齐 WinUI 3 的「浅色 / 深色」设计令牌：基底 <c>#F3F3F3</c> / <c>#202020</c>，
/// 强调色 <c>#0078D4</c> / <c>#60CDFF</c>，控件描边用近乎不可见的浅描边，
/// 交互反馈统一走「悬浮 / 按下」两档半透明填充。
/// </para>
/// <para>
/// 与「文档预览主题」（<see cref="Prt.Core.Rendering.PrtTheme"/>）解耦：
/// 本类只负责编辑器外壳，预览主题由 PRT 主题体系单独驱动。
/// </para>
/// </summary>
public sealed class AppPalette
{
    public required bool IsDark { get; init; }

    // ───────────────────────────── 层次（背景） ─────────────────────────────

    /// <summary>窗口底色（WinUI 3 <c>SolidBackgroundFillColorBase</c>）。</summary>
    public required string WindowBg { get; init; }

    /// <summary>次级面板底色。</summary>
    public required string PanelBg { get; init; }

    /// <summary>卡片 / 控件层底色（WinUI 3 <c>CardBackgroundFillColorDefault</c>）。</summary>
    public required string PanelAltBg { get; init; }

    /// <summary>自绘标题栏底色。</summary>
    public required string TitleBarBg { get; init; }

    // ───────────────────────────── 描边与文字 ─────────────────────────────

    /// <summary>分隔线色。</summary>
    public required string Border { get; init; }

    /// <summary>主文字色。</summary>
    public required string Text { get; init; }

    /// <summary>次级文字色。</summary>
    public required string SubtleText { get; init; }

    /// <summary>禁用态文字色。</summary>
    public required string TextDisabled { get; init; }

    // ───────────────────────────── 强调色 ─────────────────────────────

    /// <summary>强调色（WinUI 3 <c>AccentFillColorDefault</c>）。</summary>
    public required string Accent { get; init; }

    public required string AccentHover { get; init; }

    public required string AccentPressed { get; init; }

    /// <summary>强调色之上的前景色。</summary>
    public required string OnAccent { get; init; }

    /// <summary>列表 / 树选中项的背景着色。</summary>
    public required string SelectionTint { get; init; }

    // ───────────────────────────── 交互填充 ─────────────────────────────

    /// <summary>悬浮填充。</summary>
    public required string HoverBg { get; init; }

    /// <summary>按下填充。</summary>
    public required string PressedBg { get; init; }

    /// <summary>控件默认填充（WinUI 3 <c>ControlFillColorDefault</c>）。</summary>
    public required string ControlFill { get; init; }

    public required string ControlFillDisabled { get; init; }

    /// <summary>控件默认描边。</summary>
    public required string ControlStroke { get; init; }

    /// <summary>控件强描边（按下 / 聚焦）。</summary>
    public required string ControlStrokeStrong { get; init; }

    public required string ControlStrokeDisabled { get; init; }

    // ───────────────────────────── 标题栏按钮 ─────────────────────────────

    public required string CloseHover { get; init; }

    public required string ClosePressed { get; init; }

    public required string OnClose { get; init; }

    // ───────────────────────────── 滚动条 ─────────────────────────────

    public required string ScrollThumb { get; init; }

    public required string ScrollThumbHover { get; init; }

    public required string ScrollThumbPressed { get; init; }

    // ───────────────────────────── 编辑器 ─────────────────────────────

    public required string EditorBg { get; init; }

    public required string EditorText { get; init; }

    /// <summary>选区基色（实际画刷会叠加透明度，见 <see cref="Apply"/>）。</summary>
    public required string EditorSelection { get; init; }

    // ───────────────────────────── 语义状态色 ─────────────────────────────

    /// <summary>成功态前景色（激活成功等提示）。</summary>
    public required string Success { get; init; }

    /// <summary>成功态底色（提示条背景）。</summary>
    public required string SuccessTint { get; init; }

    /// <summary>失败 / 危险态前景色（校验失败、超出授权量等提示）。</summary>
    public required string Danger { get; init; }

    /// <summary>失败 / 危险态底色（提示条背景）。</summary>
    public required string DangerTint { get; init; }

    // ───────────────────────────── 启动界面 ─────────────────────────────

    /// <summary>内置启动图的渐变起点色。</summary>
    public required string SplashArtStart { get; init; }

    /// <summary>内置启动图的渐变终点色。</summary>
    public required string SplashArtEnd { get; init; }

    /// <summary>浅色界面配色（默认，对齐 WinUI 3 Light）。</summary>
    public static AppPalette Light() => new()
    {
        IsDark = false,
        WindowBg = "#f3f3f3",
        PanelBg = "#fafafa",
        PanelAltBg = "#ffffff",
        TitleBarBg = "#f3f3f3",
        Border = "#e5e5e5",
        Text = "#1b1b1b",
        SubtleText = "#5d5d5d",
        TextDisabled = "#a6a6a6",
        Accent = "#0078d4",
        AccentHover = "#106ebe",
        AccentPressed = "#005a9e",
        OnAccent = "#ffffff",
        SelectionTint = "#eaf3fb",
        HoverBg = "#f2f2f2",
        PressedBg = "#e8e8e8",
        ControlFill = "#fbfbfb",
        ControlFillDisabled = "#f5f5f5",
        ControlStroke = "#e1e1e1",
        ControlStrokeStrong = "#c9c9c9",
        ControlStrokeDisabled = "#ededed",
        CloseHover = "#c42b1c",
        ClosePressed = "#b02418",
        OnClose = "#ffffff",
        ScrollThumb = "#9a9a9a",
        ScrollThumbHover = "#6e6e6e",
        ScrollThumbPressed = "#4d4d4d",
        EditorBg = "#ffffff",
        EditorText = "#1b1b1b",
        EditorSelection = "#bbddf6",
        Success = "#0f7b0f",
        SuccessTint = "#eaf6ea",
        Danger = "#c42b1c",
        DangerTint = "#fdecea",
        SplashArtStart = "#e6f1fb",
        SplashArtEnd = "#fbfdff",
    };

    /// <summary>深色界面配色（对齐 WinUI 3 Dark）。</summary>
    public static AppPalette Dark() => new()
    {
        IsDark = true,
        WindowBg = "#202020",
        PanelBg = "#272727",
        PanelAltBg = "#2b2b2b",
        TitleBarBg = "#202020",
        Border = "#353535",
        Text = "#ffffff",
        SubtleText = "#c5c5c5",
        TextDisabled = "#6e6e6e",
        Accent = "#60cdff",
        AccentHover = "#4fc7f5",
        AccentPressed = "#3fb8e8",
        OnAccent = "#0a0a0a",
        SelectionTint = "#2b3d4d",
        HoverBg = "#333333",
        PressedBg = "#3d3d3d",
        ControlFill = "#2f2f2f",
        ControlFillDisabled = "#272727",
        ControlStroke = "#3a3a3a",
        ControlStrokeStrong = "#4d4d4d",
        ControlStrokeDisabled = "#333333",
        CloseHover = "#c42b1c",
        ClosePressed = "#b02418",
        OnClose = "#ffffff",
        ScrollThumb = "#6e6e6e",
        ScrollThumbHover = "#9a9a9a",
        ScrollThumbPressed = "#b8b8b8",
        EditorBg = "#1b1b1b",
        EditorText = "#f0f0f0",
        EditorSelection = "#2a4a6b",
        Success = "#6ccb5f",
        SuccessTint = "#1e3320",
        Danger = "#ff99a4",
        DangerTint = "#3b1f1f",
        SplashArtStart = "#1c2a38",
        SplashArtEnd = "#242424",
    };

    /// <summary>按名称取界面配色（未知名称回退浅色）。</summary>
    public static AppPalette Get(bool dark) => dark ? Dark() : Light();

    /// <summary>把配色写入应用资源字典；XAML 一律通过 DynamicResource 引用这些键。</summary>
    public static void Apply(Application application, AppPalette palette)
    {
        var resources = application.Resources;

        void Set(string key, string value) => resources[key] = ColorUtil.Brush(value, Colors.Gray);

        Set(ResourceKeys.WindowBg, palette.WindowBg);
        Set(ResourceKeys.PanelBg, palette.PanelBg);
        Set(ResourceKeys.PanelAltBg, palette.PanelAltBg);
        Set(ResourceKeys.TitleBarBg, palette.TitleBarBg);

        Set(ResourceKeys.Border, palette.Border);
        Set(ResourceKeys.Text, palette.Text);
        Set(ResourceKeys.SubtleText, palette.SubtleText);
        Set(ResourceKeys.TextDisabled, palette.TextDisabled);

        Set(ResourceKeys.Accent, palette.Accent);
        Set(ResourceKeys.AccentHover, palette.AccentHover);
        Set(ResourceKeys.AccentPressed, palette.AccentPressed);
        Set(ResourceKeys.OnAccent, palette.OnAccent);
        Set(ResourceKeys.SelectionTint, palette.SelectionTint);

        Set(ResourceKeys.HoverBg, palette.HoverBg);
        Set(ResourceKeys.PressedBg, palette.PressedBg);
        Set(ResourceKeys.ControlFill, palette.ControlFill);
        Set(ResourceKeys.ControlFillDisabled, palette.ControlFillDisabled);
        Set(ResourceKeys.ControlStroke, palette.ControlStroke);
        Set(ResourceKeys.ControlStrokeStrong, palette.ControlStrokeStrong);
        Set(ResourceKeys.ControlStrokeDisabled, palette.ControlStrokeDisabled);

        Set(ResourceKeys.CloseHover, palette.CloseHover);
        Set(ResourceKeys.ClosePressed, palette.ClosePressed);
        Set(ResourceKeys.OnClose, palette.OnClose);

        Set(ResourceKeys.ScrollThumb, palette.ScrollThumb);
        Set(ResourceKeys.ScrollThumbHover, palette.ScrollThumbHover);
        Set(ResourceKeys.ScrollThumbPressed, palette.ScrollThumbPressed);

        Set(ResourceKeys.EditorBg, palette.EditorBg);
        Set(ResourceKeys.EditorText, palette.EditorText);
        Set(ResourceKeys.EditorSelection, palette.EditorSelection);

        Set(ResourceKeys.Success, palette.Success);
        Set(ResourceKeys.SuccessTint, palette.SuccessTint);
        Set(ResourceKeys.Danger, palette.Danger);
        Set(ResourceKeys.DangerTint, palette.DangerTint);

        // 选区画刷需要半透明，单独构造。
        var selection = ColorUtil.WithAlpha(palette.EditorSelection, 0xB0, Colors.SteelBlue);
        resources[ResourceKeys.EditorSelectionBrush] = selection;

        // 内置启动图的渐变（启动界面用；自定义启动图存在时不会用到）。
        var splashArt = new LinearGradientBrush(
            ColorUtil.Parse(palette.SplashArtStart, Colors.White),
            ColorUtil.Parse(palette.SplashArtEnd, Colors.White),
            new Point(0, 0),
            new Point(1, 1));
        splashArt.Freeze();
        resources[ResourceKeys.SplashArt] = splashArt;
    }

    /// <summary>应用资源键常量，避免界面上散落魔法字符串。</summary>
    internal static class ResourceKeys
    {
        public const string WindowBg = "App.WindowBg";
        public const string PanelBg = "App.PanelBg";
        public const string PanelAltBg = "App.PanelAltBg";
        public const string TitleBarBg = "App.TitleBarBg";

        public const string Border = "App.Border";
        public const string Text = "App.Text";
        public const string SubtleText = "App.SubtleText";
        public const string TextDisabled = "App.TextDisabled";

        public const string Accent = "App.Accent";
        public const string AccentHover = "App.AccentHover";
        public const string AccentPressed = "App.AccentPressed";
        public const string OnAccent = "App.OnAccent";
        public const string SelectionTint = "App.SelectionTint";

        public const string HoverBg = "App.HoverBg";
        public const string PressedBg = "App.PressedBg";
        public const string ControlFill = "App.ControlFill";
        public const string ControlFillDisabled = "App.ControlFillDisabled";
        public const string ControlStroke = "App.ControlStroke";
        public const string ControlStrokeStrong = "App.ControlStrokeStrong";
        public const string ControlStrokeDisabled = "App.ControlStrokeDisabled";

        public const string CloseHover = "App.CloseHover";
        public const string ClosePressed = "App.ClosePressed";
        public const string OnClose = "App.OnClose";

        public const string ScrollThumb = "App.ScrollThumb";
        public const string ScrollThumbHover = "App.ScrollThumbHover";
        public const string ScrollThumbPressed = "App.ScrollThumbPressed";

        public const string EditorBg = "App.EditorBg";
        public const string EditorText = "App.EditorText";
        public const string EditorSelection = "App.EditorSelection";
        public const string EditorSelectionBrush = "App.EditorSelectionBrush";

        /// <summary>语义状态色（提示条 / 结果文字）。</summary>
        public const string Success = "App.Success";
        public const string SuccessTint = "App.SuccessTint";
        public const string Danger = "App.Danger";
        public const string DangerTint = "App.DangerTint";

        /// <summary>内置启动图的渐变画刷。</summary>
        public const string SplashArt = "App.SplashArt";
    }
}
