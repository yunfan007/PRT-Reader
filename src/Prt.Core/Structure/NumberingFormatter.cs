using System.Globalization;
using System.Text;

namespace Prt.Core.Structure;

/// <summary>
/// 章节与条目编号格式化（《PRT 标准》9.2 编号样式表、9.4 默认引用文本映射表）。
/// </summary>
internal static class NumberingFormatter
{
    /// <summary>中文数字（用于「第一章」「第X节」「一、」等）。支持 0–999。</summary>
    public static string ChineseNumber(int value)
    {
        if (value <= 0)
        {
            // 编号会写进导出文件（HTML / Markdown / 纯文本），必须跨区域稳定：
            // 在 ar-SA 等区域下 int.ToString() 会产出阿拉伯-印度数字，导出结果无法与源文对齐。
            return value.ToString(CultureInfo.InvariantCulture);
        }
        if (value < 10)
        {
            return value switch
            {
                1 => "一", 2 => "二", 3 => "三", 4 => "四", 5 => "五",
                6 => "六", 7 => "七", 8 => "八", 9 => "九", _ => value.ToString(CultureInfo.InvariantCulture),
            };
        }
        if (value == 10)
        {
            return "十";
        }
        if (value < 20)
        {
            return "十" + ChineseNumber(value - 10);
        }
        if (value < 100)
        {
            var tens = value / 10;
            var ones = value % 10;
            return ChineseNumber(tens) + "十" + (ones == 0 ? string.Empty : ChineseNumber(ones));
        }
        var hundreds = value / 100;
        var remainder = value % 100;
        var sb = new StringBuilder();
        sb.Append(ChineseNumber(hundreds)).Append('百');
        if (remainder > 0)
        {
            sb.Append(ChineseNumber(remainder));
        }
        return sb.ToString();
    }

    /// <summary>罗马数字（用于 `roman` 编号样式）。</summary>
    public static string Roman(int value)
    {
        if (value <= 0)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
        var numerals = new (int Value, string Text)[]
        {
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
            (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
            (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
        };
        var sb = new StringBuilder();
        var remaining = value;
        foreach (var (v, text) in numerals)
        {
            while (remaining >= v)
            {
                sb.Append(text);
                remaining -= v;
            }
        }
        return sb.ToString();
    }

    /// <summary>按编号样式生成章节 / 标题在「块头」上的显示编号。</summary>
    public static string FormatHeadingNumber(IReadOnlyList<int> counters, int level, NumberingStyle style)
    {
        if (style == NumberingStyle.None || level <= 0)
        {
            return string.Empty;
        }

        switch (style)
        {
            case NumberingStyle.Mixed:
                return level == 1
                    ? "第" + ChineseNumber(counters[1]) + "章"
                    : DottedArabic(counters, level);

            case NumberingStyle.Cn:
                return level switch
                {
                    1 => "第" + ChineseNumber(counters[1]) + "章",
                    2 => "第" + ChineseNumber(counters[2]) + "节",
                    3 => ChineseNumber(counters[3]) + "、",
                    _ => DottedArabic(counters, level),
                };

            case NumberingStyle.Roman:
                if (level == 1)
                {
                    return Roman(counters[1]);
                }
                var roman = new StringBuilder(Roman(counters[1]));
                for (var i = 2; i <= level; i++)
                {
                    roman.Append('.').Append(counters[i].ToString(CultureInfo.InvariantCulture));
                }
                return roman.ToString();

            default:
                return DottedArabic(counters, level);
        }
    }

    /// <summary>按编号样式生成 `[[ref]]` 的默认引用文本（标准 9.4 映射表）。</summary>
    public static string FormatSectionReference(
        IReadOnlyList<int> counters,
        int level,
        NumberingStyle style,
        string? fallbackText)
    {
        if (style == NumberingStyle.None)
        {
            return string.IsNullOrEmpty(fallbackText) ? string.Empty : fallbackText!;
        }

        switch (style)
        {
            case NumberingStyle.Mixed:
                return level == 1
                    ? "第" + ChineseNumber(counters[1]) + "章"
                    : "第" + DottedArabic(counters, level) + "节";

            case NumberingStyle.Cn:
            {
                var sb = new StringBuilder();
                for (var i = 1; i <= level; i++)
                {
                    sb.Append(i switch
                    {
                        1 => "第" + ChineseNumber(counters[1]) + "章",
                        2 => "第" + ChineseNumber(counters[2]) + "节",
                        3 => ChineseNumber(counters[3]) + "、",
                        _ => counters[i] + ".",
                    });
                }
                return sb.ToString();
            }

            case NumberingStyle.Roman:
                if (level == 1)
                {
                    return Roman(counters[1]);
                }
                var roman = new StringBuilder(Roman(counters[1]));
                for (var i = 2; i <= level; i++)
                {
                    roman.Append('.').Append(counters[i].ToString(CultureInfo.InvariantCulture));
                }
                return roman.ToString();

            default:
                return DottedArabic(counters, level);
        }
    }

    private static string DottedArabic(IReadOnlyList<int> counters, int level)
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= level && i < counters.Count; i++)
        {
            if (i > 1)
            {
                sb.Append('.');
            }
            sb.Append(counters[i].ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
