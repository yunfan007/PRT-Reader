using System.Windows.Media;
using Prt.Core;
using Prt.Core.Rendering;
using Prt.Core.Syntax;

namespace Prt.App.Rendering;

/// <summary>
/// 预览样式：把一份 <see cref="PrtTheme"/> 解析为 WPF 可直接使用的画刷、字体与尺寸。
/// 渲染器只依赖本类，不直接读取主题变量字典，从而把「主题」与「排版逻辑」解耦。
/// </summary>
internal sealed class PreviewStyle
{
    private readonly PrtTheme _theme;

    public PreviewStyle(PrtTheme theme)
    {
        _theme = theme;

        PageBackground = Brush("--page-bg", "#ffffff");
        Text = Brush("--page-text", "#1f2328");
        SubtleText = Brush("--page-text-secondary", "#57606a");
        MutedText = Brush("--page-text-muted", "#8c959f");
        Border = Brush("--page-border", "#d0d7de");
        Heading = Brush("--heading-color", "#1f2328");
        Link = Brush("--link-color", "#0969da");
        CodeBackground = Brush("--code-bg", "#f6f8fa");
        CodeText = Brush("--code-text", "#1f2328");
        KbdBackground = Brush("--kbd-bg", "#f6f8fa");
        KbdText = Brush("--kbd-text", "#1f2328");
        KbdBorder = Brush("--kbd-border", "#d0d7de");
        HighlightBackground = Brush("--highlight-bg", "#fff3a3");
        Accent = Brush("--page-brand", "#0969da");
        Surface = Brush("--page-surface", "#f6f8fa");

        SansFamily = new FontFamily(_theme.Get("--font-sans-serif", "Microsoft YaHei UI, Segoe UI"));
        MonoFamily = new FontFamily(_theme.Get("--font-mono", "Consolas, Courier New"));

        // 正文与各级标题字号：建立稳定的纵向层次（标准 12 章「字号档位」的呈现侧映射）。
        BodyFontSize = 15d;
        HeadingSizes = new[] { 26d, 22d, 19d, 17d, 15.5d, 14.5d, 14d };
    }

    public string ThemeName => _theme.Name;

    public Brush PageBackground { get; }

    public Brush Text { get; }

    public Brush SubtleText { get; }

    public Brush MutedText { get; }

    public Brush Border { get; }

    public Brush Heading { get; }

    public Brush Link { get; }

    public Brush CodeBackground { get; }

    public Brush CodeText { get; }

    public Brush KbdBackground { get; }

    public Brush KbdText { get; }

    public Brush KbdBorder { get; }

    public Brush HighlightBackground { get; }

    public Brush Accent { get; }

    public Brush Surface { get; }

    public FontFamily SansFamily { get; }

    public FontFamily MonoFamily { get; }

    public double BodyFontSize { get; }

    /// <summary>下标 0 对应一级标题。</summary>
    public double[] HeadingSizes { get; }

    /// <summary>取标题字号（层级从 1 起，越界时钳制）。</summary>
    public double HeadingSize(int level) => HeadingSizes[Math.Clamp(level, 1, HeadingSizes.Length) - 1];

    /// <summary>语义块（提示框）底色。</summary>
    public Brush CalloutBackground(SemanticKind kind)
        => ColorUtil.Brush(_theme.CalloutBackground(kind), Surface is SolidColorBrush s ? s.Color : Colors.Transparent);

    /// <summary>语义块（提示框）侧边色。</summary>
    public Brush CalloutBorder(SemanticKind kind)
        => ColorUtil.Brush(_theme.CalloutBorder(kind), Border is SolidColorBrush s ? s.Color : Colors.Transparent);

    /// <summary>命名色（行内前景色 / 背景色）。</summary>
    public static SolidColorBrush NamedBrush(NamedColor color)
        => ColorUtil.Brush(ColorPalette.ToHex(color), Colors.Black);

    /// <summary>是否深色主题（用于图片占位等需要自适应的场景）。</summary>
    public bool IsDark => ColorUtil.IsDark(_theme.Get("--page-bg", "#ffffff"));

    /// <summary>读取主题变量中的色值；变量不是合法色值时回退默认色。</summary>
    private SolidColorBrush Brush(string key, string fallback)
    {
        var value = _theme.Get(key, fallback);
        return value.StartsWith('#')
            ? ColorUtil.Brush(value, Colors.Transparent)
            : ColorUtil.Brush(fallback, Colors.Transparent);
    }
}
