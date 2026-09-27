namespace Prt.Core.Rendering;

/// <summary>
/// 命名色到具体色值的映射（《PRT 标准》12.1 节命名色；附录 C 主题变量的取值来源）。
/// <para>
/// 说明：标准只规定「颜色仅接受命名色」，未规定各命名色的具体 RGB 值；
/// 本实现据此定义一套确定性调色板，并允许 `::: theme` 按附录 C 变量逐项覆盖。
/// </para>
/// </summary>
public static class ColorPalette
{
    private static readonly Dictionary<NamedColor, string> Colors = new()
    {
        // 中性色
        [NamedColor.Black] = "#000000",
        [NamedColor.White] = "#ffffff",
        [NamedColor.Gray] = "#808080",
        [NamedColor.Navy] = "#000080",

        // 彩色
        [NamedColor.Red] = "#d32f2f",
        [NamedColor.Orange] = "#ef6c00",
        [NamedColor.Yellow] = "#f9a825",
        [NamedColor.Green] = "#2e7d32",
        [NamedColor.Teal] = "#00796b",
        [NamedColor.Blue] = "#1565c0",
        [NamedColor.Purple] = "#6a1b9a",
        [NamedColor.Cyan] = "#00838f",
        [NamedColor.Magenta] = "#ad1457",
        [NamedColor.Brown] = "#6d4c41",
        [NamedColor.Olive] = "#827717",
        [NamedColor.Maroon] = "#8e0000",

        // 语义色
        [NamedColor.Danger] = "#c62828",
        [NamedColor.Warning] = "#ef6c00",
        [NamedColor.Info] = "#1565c0",
        [NamedColor.Success] = "#2e7d32",
    };

    /// <summary>取命名色的十六进制字符串。</summary>
    public static string ToHex(NamedColor color) => Colors.TryGetValue(color, out var hex) ? hex : "#000000";

    /// <summary>取命名色的中文/英文显示名（用于 HTML 类名，一律使用英文小写）。</summary>
    public static string ToName(NamedColor color) => color switch
    {
        NamedColor.Black => "black",
        NamedColor.White => "white",
        NamedColor.Gray => "gray",
        NamedColor.Navy => "navy",
        NamedColor.Red => "red",
        NamedColor.Orange => "orange",
        NamedColor.Yellow => "yellow",
        NamedColor.Green => "green",
        NamedColor.Teal => "teal",
        NamedColor.Blue => "blue",
        NamedColor.Purple => "purple",
        NamedColor.Cyan => "cyan",
        NamedColor.Magenta => "magenta",
        NamedColor.Brown => "brown",
        NamedColor.Olive => "olive",
        NamedColor.Maroon => "maroon",
        NamedColor.Danger => "danger",
        NamedColor.Warning => "warning",
        NamedColor.Info => "info",
        NamedColor.Success => "success",
        _ => "black",
    };

    /// <summary>字号档位到像素值（标准 12.1：仅改变字号，不改变行高）。</summary>
    public static double FontSizePixel(FontSizeScale size) => size switch
    {
        FontSizeScale.Small => 12d,
        FontSizeScale.Normal => 16d,
        FontSizeScale.Large => 20d,
        FontSizeScale.XLarge => 26d,
        _ => 16d,
    };

    /// <summary>字号档位到相对倍率（用于 HTML 的 em 单位）。</summary>
    public static string FontSizeEm(FontSizeScale size) => size switch
    {
        FontSizeScale.Small => "0.85em",
        FontSizeScale.Normal => "1em",
        FontSizeScale.Large => "1.25em",
        FontSizeScale.XLarge => "1.6em",
        _ => "1em",
    };
}
