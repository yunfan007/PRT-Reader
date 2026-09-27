using System.Globalization;
using System.Windows.Media;

namespace Prt.App.Rendering;

/// <summary>
/// 颜色解析工具：把 PRT 主题变量里的十六进制色值（`#rgb` / `#rrggbb` / `#aarrggbb`）
/// 转成 WPF 的 <see cref="Color"/> / <see cref="Brush"/>。
/// 纯函数、无状态（除了一层解析缓存），供渲染层与界面主题共用。
/// </summary>
internal static class ColorUtil
{
    private static readonly Dictionary<string, SolidColorBrush> BrushCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>解析十六进制色值；非法输入返回回退色。</summary>
    public static Color Parse(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var text = value.Trim();
        if (text.StartsWith('#'))
        {
            text = text[1..];
        }

        // 支持 #rgb / #rrggbb / #aarrggbb。
        if (text.Length == 3)
        {
            text = string.Concat(text[0], text[0], text[1], text[1], text[2], text[2]);
        }

        if (text.Length is not (6 or 8)
            || !uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
        {
            return fallback;
        }

        return text.Length == 6
            ? Color.FromRgb((byte)(raw >> 16), (byte)(raw >> 8), (byte)raw)
            : Color.FromArgb((byte)(raw >> 24), (byte)(raw >> 16), (byte)(raw >> 8), (byte)raw);
    }

    /// <summary>解析为冻结的画刷（带缓存，避免大量行内元素重复创建）。</summary>
    public static SolidColorBrush Brush(string? value, Color fallback)
    {
        var key = value ?? string.Empty;
        if (BrushCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var brush = new SolidColorBrush(Parse(value, fallback));
        brush.Freeze();
        BrushCache[key] = brush;
        return brush;
    }

    /// <summary>以给定透明度派生一个新画刷（用于选区、悬浮等层次色）。</summary>
    public static SolidColorBrush WithAlpha(string? value, byte alpha, Color fallback)
    {
        var color = Parse(value, fallback);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>判断色值是否偏暗，用于决定图标 / 边框的对比色。</summary>
    public static bool IsDark(string? value)
    {
        var color = Parse(value, Colors.White);
        // 相对亮度（ITU-R BT.601 近似），阈值 0.5。
        var luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
        return luminance < 0.5;
    }

    /// <summary>按比例混合两个色值（ratio=0 取 a，ratio=1 取 b）。</summary>
    public static string Blend(string a, string b, double ratio)
    {
        var ca = Parse(a, Colors.White);
        var cb = Parse(b, Colors.White);
        ratio = Math.Clamp(ratio, 0, 1);
        var r = (byte)Math.Round(ca.R + (cb.R - ca.R) * ratio);
        var g = (byte)Math.Round(ca.G + (cb.G - ca.G) * ratio);
        var bl = (byte)Math.Round(ca.B + (cb.B - ca.B) * ratio);
        return $"#{r:X2}{g:X2}{bl:X2}";
    }
}
