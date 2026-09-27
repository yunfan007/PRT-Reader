using System.Text;
using Prt.Core.Diagnostics;
using Prt.Core.Syntax;

namespace Prt.Core.Parsing;

/// <summary>
/// 行内标记解析器，实现《PRT 标准》第 7 章的行内语法与 6.4 节的歧义判定优先级。
/// <para>优先级（自上而下先命中者优先，标准 6.4）：转义 → 行内代码 → 双花括号插值 →
/// 单花括号术语链接 → 双波浪删除线 → 单波浪下标 → 双括号指令 → 方括号链接/图片 →
/// 双等号高亮 → 成对加号下划线 → 上标/脚注 → 内联注释 → 强调（三星/双星/单星/下划线）。</para>
/// </summary>
internal sealed class InlineParser
{
    private readonly string _text;
    private readonly int _line;
    private readonly int _baseColumn;
    private readonly PrtOptions _options;
    private readonly DiagnosticBag _diagnostics;

    private InlineParser(string text, int line, int column, PrtOptions options, DiagnosticBag diagnostics)
    {
        _text = text;
        _line = line;
        _baseColumn = column;
        _options = options;
        _diagnostics = diagnostics;
    }

    /// <summary>解析一行内的行内标记序列。</summary>
    public static List<PrtInline> Parse(
        string text,
        int line,
        int column,
        PrtOptions options,
        DiagnosticBag diagnostics)
    {
        var parser = new InlineParser(text, line, column, options, diagnostics);
        return parser.ParseRange(0, text.Length);
    }

    /// <summary>取行内纯文本（用于标题、目录、引用默认文本等）。</summary>
    public static string ToPlainText(IEnumerable<PrtInline> inlines)
    {
        var sb = new StringBuilder();
        AppendPlainText(sb, inlines);
        return sb.ToString();
    }

    private static void AppendPlainText(StringBuilder sb, IEnumerable<PrtInline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextInline t: sb.Append(t.Text); break;
                case CodeInline c: sb.Append(c.Text); break;
                case EmphasisInline e: AppendPlainText(sb, e.Children); break;
                case StrikeInline s: AppendPlainText(sb, s.Children); break;
                case HighlightInline h: AppendPlainText(sb, h.Children); break;
                case UnderlineInline u: AppendPlainText(sb, u.Children); break;
                case SuperscriptInline sup: AppendPlainText(sb, sup.Children); break;
                case SubscriptInline sub: AppendPlainText(sb, sub.Children); break;
                case ColorInline col: AppendPlainText(sb, col.Children); break;
                case BackgroundColorInline bg: AppendPlainText(sb, bg.Children); break;
                case SizeInline size: AppendPlainText(sb, size.Children); break;
                case LinkInline link:
                    if (link.IsImage)
                    {
                        sb.Append(link.Alt);
                    }
                    else
                    {
                        AppendPlainText(sb, link.Children);
                    }
                    break;
                case FootnoteInline f: break;
                case KbdInline k: sb.Append('[').Append(k.Keys).Append(']'); break;
                case ContentsInline: break;
                case RefInline r: sb.Append(r.DisplayText ?? r.TargetId); break;
                case TermLinkInline t2: sb.Append(t2.DisplayText ?? t2.Key); break;
                case InterpolationInline i: sb.Append(i.LiteralText); break;
                case DateLiteralInline d: sb.Append(d.Text); break;
                case LineBreakInline: sb.Append(' '); break;
                case LiteralInline l: sb.Append(l.Text); break;
            }
        }
    }

    private List<PrtInline> ParseRange(int start, int end)
    {
        var result = new List<PrtInline>();
        var literal = new StringBuilder();
        var i = start;

        void FlushLiteral()
        {
            if (literal.Length > 0)
            {
                result.Add(new TextInline(literal.ToString()));
                literal.Clear();
            }
        }

        while (i < end)
        {
            var c = _text[i];
            var column = _baseColumn + i;

            // 1. 转义（最高优先级，标准 6.3）。
            if (c == '\\')
            {
                if (i + 1 < end)
                {
                    var next = _text[i + 1];
                    if (next == '\\')
                    {
                        literal.Append('\\');
                        i += 2;
                        continue;
                    }
                    if (ParsingHelpers.ReservedSymbols.Contains(next))
                    {
                        literal.Append(next);
                        i += 2;
                        continue;
                    }
                    // 非保留符号：输出字面反斜杠并原样保留该字符（标准 6.3）。
                    literal.Append('\\').Append(next);
                    i += 2;
                    continue;
                }
                literal.Append('\\');
                i++;
                continue;
            }

            // 2. 行内代码：内部不再进行任何标记判定。
            if (c == '`')
            {
                var close = _text.IndexOf('`', i + 1);
                if (close >= 0 && close < end)
                {
                    FlushLiteral();
                    result.Add(new CodeInline(_text[(i + 1)..close]) { Line = _line, Column = column });
                    i = close + 1;
                    continue;
                }
                literal.Append(c);
                i++;
                continue;
            }

            // 3/4. 双花括号插值优先于单花括号术语链接。
            if (c == '{' && i + 1 < end && _text[i + 1] == '{')
            {
                var close = _text.IndexOf("}}", i + 2, StringComparison.Ordinal);
                if (close >= 0 && close < end)
                {
                    FlushLiteral();
                    // 【COMP 不支持】此处仅构造节点，渲染阶段按第 13 章降级为原文。
                    result.Add(new InterpolationInline(_text[(i + 2)..close]) { Line = _line, Column = column });
                    i = close + 2;
                    continue;
                }
                literal.Append(c);
                i++;
                continue;
            }

            if (c == '{')
            {
                var close = FindChar('}', i + 1, end);
                if (close >= 0)
                {
                    var inner = _text[(i + 1)..close];
                    var (key, display) = SplitKeyAndDisplay(inner);
                    if (key.Length > 0)
                    {
                        FlushLiteral();
                        result.Add(new TermLinkInline { Key = key, DisplayText = display, Line = _line, Column = column });
                        i = close + 1;
                        continue;
                    }
                }
                literal.Append(c);
                i++;
                continue;
            }

            // 5. 双波浪删除线优先于单波浪下标。
            if (c == '~' && i + 1 < end && _text[i + 1] == '~')
            {
                var close = _text.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (close >= 0 && close < end)
                {
                    FlushLiteral();
                    var node = new StrikeInline { Line = _line, Column = column };
                    node.Children.AddRange(ParseRange(i + 2, close));
                    result.Add(node);
                    i = close + 2;
                    continue;
                }
                literal.Append(c);
                i++;
                continue;
            }

            if (c == '~')
            {
                var close = FindChar('~', i + 1, end);
                if (close >= 0 && close > i + 1)
                {
                    FlushLiteral();
                    var node = new SubscriptInline { Line = _line, Column = column };
                    node.Children.AddRange(ParseRange(i + 1, close));
                    result.Add(node);
                    i = close + 1;
                    continue;
                }
                literal.Append(c);
                i++;
                continue;
            }

            // 6. 双括号指令族；单个方括号与圆括号组合判定为链接；前置 ! 判定为图片。
            if (c == '[' && i + 1 < end && _text[i + 1] == '[')
            {
                var close = _text.IndexOf("]]", i + 2, StringComparison.Ordinal);
                if (close >= 0 && close < end)
                {
                    var inner = _text[(i + 2)..close];
                    FlushLiteral();
                    ParseDirective(inner, column, i, close + 2, end, result, literal, ref i);
                    continue;
                }
                literal.Append(c);
                i++;
                continue;
            }

            if (c == '!' && i + 1 < end && _text[i + 1] == '[')
            {
                if (TryParseLink(i + 1, end, isImage: true, column, result, out var consumed))
                {
                    FlushLiteral();
                    i = consumed;
                    continue;
                }
                literal.Append(c);
                i++;
                continue;
            }

            if (c == '[')
            {
                if (TryParseLink(i, end, isImage: false, column, result, out var consumed))
                {
                    FlushLiteral();
                    i = consumed;
                    continue;
                }
                literal.Append(c);
                i++;
                continue;
            }

            // 7. 双等号高亮。
            if (c == '=' && i + 1 < end && _text[i + 1] == '=')
            {
                var close = _text.IndexOf("==", i + 2, StringComparison.Ordinal);
                if (close >= 0 && close < end && close > i + 2)
                {
                    FlushLiteral();
                    var node = new HighlightInline { Line = _line, Column = column };
                    node.Children.AddRange(ParseRange(i + 2, close));
                    result.Add(node);
                    i = close + 2;
                    continue;
                }
                literal.Append(c);
                i++;
                continue;
            }

            // 7'. 行内成对加号为下划线（表格单元格内的 ++ 已由表格解析器先行处理，标准 6.4/10.3）。
            // 特例：`+` 紧邻 `[[kbd:…]]` 时作为组合键的连接符，按字面输出（标准 7.3）。
            if (c == '+')
            {
                if (IsKbdSeparatorAhead(i, end))
                {
                    literal.Append('+');
                    i++;
                    continue;
                }

                var close = FindChar('+', i + 1, end);
                if (close >= 0 && close > i + 1)
                {
                    FlushLiteral();
                    var node = new UnderlineInline { Line = _line, Column = column };
                    node.Children.AddRange(ParseRange(i + 1, close));
                    result.Add(node);
                    i = close + 1;
                    continue;
                }
                literal.Append(c);
                i++;
                continue;
            }

            // 上标 / 脚注：`^[` 开头为脚注，成对 `^…^` 为上标。
            if (c == '^')
            {
                if (i + 1 < end && _text[i + 1] == '[')
                {
                    var close = FindChar(']', i + 2, end);
                    if (close >= 0)
                    {
                        FlushLiteral();
                        result.Add(new FootnoteInline { Content = _text[(i + 2)..close], Line = _line, Column = column });
                        i = close + 1;
                        continue;
                    }
                    literal.Append(c);
                    i++;
                    continue;
                }

                var close2 = FindChar('^', i + 1, end);
                if (close2 >= 0 && close2 > i + 1)
                {
                    FlushLiteral();
                    var node = new SuperscriptInline { Line = _line, Column = column };
                    node.Children.AddRange(ParseRange(i + 1, close2));
                    result.Add(node);
                    i = close2 + 1;
                    continue;
                }
                literal.Append(c);
                i++;
                continue;
            }

            // 8. 内联注释 `%%…%%`：仅在成对出现时触发，内容不进入渲染。
            if (c == '%' && i + 1 < end && _text[i + 1] == '%')
            {
                var close = _text.IndexOf("%%", i + 2, StringComparison.Ordinal);
                if (close >= 0 && close < end)
                {
                    FlushLiteral();
                    i = close + 2;
                    continue;
                }
                literal.Append(c);
                i++;
                continue;
            }

            // 强调：三星（加粗斜体）→ 双星（加粗）→ 单星/单下划线（斜体）。
            if (c == '*' || c == '_')
            {
                if (TryParseEmphasis(i, end, c, column, result, out var consumed))
                {
                    FlushLiteral();
                    i = consumed;
                    continue;
                }
                literal.Append(c);
                i++;
                continue;
            }

            // 日期字面量：`@` 前为词边界且后接固定位数日期时间文本。
            // 【COMP 不支持】不构造日期值，仅按字面保留（标准 6.2 / 11.2.2 字形回退）。
            if (c == '@' && IsWordBoundaryBefore(i, start))
            {
                var len = MatchDateShape(i + 1, end);
                if (len > 0 && (i + 1 + len >= end || !char.IsLetterOrDigit(_text[i + 1 + len])))
                {
                    FlushLiteral();
                    result.Add(new DateLiteralInline(_text.Substring(i, len + 1)) { Line = _line, Column = column });
                    i = i + 1 + len;
                    continue;
                }
            }

            literal.Append(c);
            i++;
        }

        FlushLiteral();
        return result;
    }

    /// <summary>解析指令 `[[…]]`；配对指令会递归解析其包裹内容。</summary>
    private void ParseDirective(
        string inner,
        int column,
        int startIndex,
        int afterDirective,
        int end,
        List<PrtInline> result,
        StringBuilder literal,
        ref int i)
    {
        var trimmed = inner.Trim();

        // 指令名与参数之间可用 `:` 或空白分隔（标准 9.3 写作 `[[contents depth=2]]`，
        // 而 7.3 / 7.5 写作 `[[kbd:Ctrl+K]]`）。此处按「最先出现的分隔符」切分，
        // 两种写法因此等价。
        var separator = -1;
        for (var index = 0; index < trimmed.Length; index++)
        {
            if (trimmed[index] == ':' || char.IsWhiteSpace(trimmed[index]))
            {
                separator = index;
                break;
            }
        }

        string name;
        string argument;
        if (separator < 0)
        {
            name = trimmed;
            argument = string.Empty;
        }
        else
        {
            name = trimmed[..separator];
            var rest = trimmed[separator..].TrimStart();
            if (rest.StartsWith(':'))
            {
                rest = rest[1..];
            }
            argument = rest.Trim();
        }

        switch (name)
        {
            case "kbd":
                result.Add(new KbdInline { Keys = argument.Trim(), Line = _line, Column = column });
                i = afterDirective;
                return;

            case "contents":
            {
                var parts = ParsingHelpers.ParseHeader(argument);
                var node = new ContentsInline { Line = _line, Column = column };
                if (parts.Parameters.TryGetValue("depth", out var depthText)
                    && int.TryParse(depthText, out var depth) && depth > 0)
                {
                    node.Depth = depth;
                }
                if (parts.Parameters.TryGetValue("mode", out var modeText)
                    && modeText.Trim().Equals("numbered", StringComparison.OrdinalIgnoreCase))
                {
                    node.Mode = "numbered";
                }
                result.Add(node);
                i = afterDirective;
                return;
            }

            case "ref":
            {
                var (target, display) = SplitKeyAndDisplay(argument);
                result.Add(new RefInline
                {
                    TargetId = target.Trim(),
                    DisplayText = string.IsNullOrEmpty(display) ? null : display.Trim(),
                    Line = _line,
                    Column = column,
                });
                i = afterDirective;
                return;
            }

            // Q19 确认项：`[[term:键]]` 作为 `{键}` 的等价别名。
            case "term":
            {
                var (key, display) = SplitKeyAndDisplay(argument);
                result.Add(new TermLinkInline
                {
                    Key = key.Trim(),
                    DisplayText = string.IsNullOrEmpty(display) ? null : display.Trim(),
                    Line = _line,
                    Column = column,
                });
                i = afterDirective;
                return;
            }

            case "color" when ParsingHelpers.TryParseNamedColor(argument, out var color):
            {
                if (TryParsePairedDirective("color", afterDirective, end, out var innerStart, out var innerEnd, out var closeEnd))
                {
                    var node = new ColorInline { Color = color, Line = _line, Column = column };
                    node.Children.AddRange(ParseRange(innerStart, innerEnd));
                    result.Add(node);
                    i = closeEnd;
                    return;
                }
                break;
            }

            case "bg" when ParsingHelpers.TryParseNamedColor(argument, out var bgColor):
            {
                if (TryParsePairedDirective("bg", afterDirective, end, out var innerStart, out var innerEnd, out var closeEnd))
                {
                    var node = new BackgroundColorInline { Color = bgColor, Line = _line, Column = column };
                    node.Children.AddRange(ParseRange(innerStart, innerEnd));
                    result.Add(node);
                    i = closeEnd;
                    return;
                }
                break;
            }

            case "size" when ParsingHelpers.TryParseFontSize(argument, out var size):
            {
                if (TryParsePairedDirective("size", afterDirective, end, out var innerStart, out var innerEnd, out var closeEnd))
                {
                    var node = new SizeInline { Size = size, Line = _line, Column = column };
                    node.Children.AddRange(ParseRange(innerStart, innerEnd));
                    result.Add(node);
                    i = closeEnd;
                    return;
                }
                break;
            }

            case "color":
            case "bg":
                // 非法颜色：标准 12.1 规定颜色仅接受命名色。
                ParsingHelpers.Report(
                    _diagnostics,
                    _options.Strict,
                    PrtDiagnosticCodes.InvalidColor,
                    $"非法颜色「{argument.Trim()}」，仅接受命名色（标准 12.1）",
                    _line,
                    column);
                break;

            case "size":
                ParsingHelpers.Report(
                    _diagnostics,
                    _options.Strict,
                    PrtDiagnosticCodes.InvalidAttribute,
                    $"非法字号「{argument.Trim()}」，取值为 small/normal/large/xlarge",
                    _line,
                    column);
                break;

            default:
                ParsingHelpers.Report(
                    _diagnostics,
                    _options.Strict,
                    PrtDiagnosticCodes.UnknownDirective,
                    $"未知指令「{name}」，已按纯文本降级",
                    _line,
                    column);
                break;
        }

        // 未识别或未闭合：按字面输出原文（降级，不丢内容）。
        literal.Append(_text[startIndex..afterDirective]);
        i = afterDirective;
    }

    /// <summary>查找配对指令 `[[/name]]`，返回其内部范围与结束偏移。</summary>
    private bool TryParsePairedDirective(
        string name,
        int from,
        int end,
        out int innerStart,
        out int innerEnd,
        out int closeEnd)
    {
        innerStart = 0;
        innerEnd = 0;
        closeEnd = 0;

        var marker = "[[/" + name + "]]";
        var index = _text.IndexOf(marker, from, StringComparison.Ordinal);
        if (index < 0 || index + marker.Length > end)
        {
            return false;
        }
        innerStart = from;
        innerEnd = index;
        closeEnd = index + marker.Length;
        return true;
    }

    private bool TryParseEmphasis(
        int index,
        int end,
        char marker,
        int column,
        List<PrtInline> result,
        out int consumed)
    {
        consumed = index;

        // 三星：加粗 + 斜体。
        if (marker == '*' && index + 2 < end && _text[index + 1] == '*' && _text[index + 2] == '*')
        {
            var close = _text.IndexOf("***", index + 3, StringComparison.Ordinal);
            if (close >= 0 && close < end && close > index + 3)
            {
                var node = new EmphasisInline { Bold = true, Italic = true, Line = _line, Column = column };
                node.Children.AddRange(ParseRange(index + 3, close));
                result.Add(node);
                consumed = close + 3;
                return true;
            }
        }

        // 双星：加粗。
        if (marker == '*' && index + 1 < end && _text[index + 1] == '*')
        {
            var close = _text.IndexOf("**", index + 2, StringComparison.Ordinal);
            if (close >= 0 && close < end && close > index + 2)
            {
                var node = new EmphasisInline { Bold = true, Line = _line, Column = column };
                node.Children.AddRange(ParseRange(index + 2, close));
                result.Add(node);
                consumed = close + 2;
                return true;
            }
        }

        // 单星或单下划线：斜体。
        var single = FindChar(marker, index + 1, end);
        if (single >= 0 && single > index + 1)
        {
            var node = new EmphasisInline { Italic = true, Line = _line, Column = column };
            node.Children.AddRange(ParseRange(index + 1, single));
            result.Add(node);
            consumed = single + 1;
            return true;
        }

        return false;
    }

    private bool TryParseLink(
        int index,
        int end,
        bool isImage,
        int column,
        List<PrtInline> result,
        out int consumed)
    {
        consumed = index;
        var labelEnd = FindChar(']', index + 1, end);
        if (labelEnd < 0 || labelEnd + 1 >= end || _text[labelEnd + 1] != '(')
        {
            return false;
        }
        var urlEnd = FindChar(')', labelEnd + 2, end);
        if (urlEnd < 0)
        {
            return false;
        }

        var label = _text[(index + 1)..labelEnd];
        var url = _text[(labelEnd + 2)..urlEnd].Trim();

        if (isImage)
        {
            result.Add(new LinkInline
            {
                IsImage = true,
                Alt = label,
                Url = url,
                Line = _line,
                Column = column,
            });
        }
        else
        {
            var node = new LinkInline { Url = url, Line = _line, Column = column };
            node.Children.AddRange(ParseRange(index + 1, labelEnd));
            result.Add(node);
        }

        consumed = urlEnd + 1;
        return true;
    }

    /// <summary>查找下一个未被转义的指定字符。</summary>
    private int FindChar(char target, int from, int end)
    {
        for (var i = from; i < end; i++)
        {
            if (_text[i] == '\\')
            {
                i++;
                continue;
            }
            if (_text[i] == target)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>拆分「键 | 显示名」；兼容简写「键: 显示名」。</summary>
    private static (string Key, string? Display) SplitKeyAndDisplay(string inner)
    {
        var bar = inner.IndexOf('|', StringComparison.Ordinal);
        if (bar >= 0)
        {
            var key = inner[..bar].Trim();
            var display = inner[(bar + 1)..].Trim();
            return (key, display.Length == 0 ? null : display);
        }

        var colon = inner.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            var key = inner[..colon].Trim();
            var display = inner[(colon + 1)..].Trim();
            return (key, display.Length == 0 ? null : display);
        }

        return (inner.Trim(), null);
    }

    /// <summary>判断 `+` 之后是否紧跟键盘按键指令，若是则该 `+` 为组合键连接符。</summary>
    private bool IsKbdSeparatorAhead(int index, int end)
    {
        var from = index + 1;
        if (from + 5 > end)
        {
            return false;
        }
        return string.Compare(_text, from, "[[kbd", 0, 5, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private bool IsWordBoundaryBefore(int index, int start)
    {
        if (index <= start)
        {
            return true;
        }
        var prev = _text[index - 1];
        return char.IsWhiteSpace(prev) || char.IsPunctuation(prev) || char.IsSymbol(prev);
    }

    /// <summary>从 <paramref name="from"/> 起尝试匹配日期时间文本，返回匹配长度；0 表示不匹配。</summary>
    private int MatchDateShape(int from, int end)
    {
        // 依次尝试 19 / 16 / 10 三种长度（YYYY-MM-DD[THH:MM[:SS]]）。
        foreach (var length in new[] { 19, 16, 10 })
        {
            if (from + length > end)
            {
                continue;
            }
            var candidate = _text.Substring(from, length);
            if (ParsingHelpers.IsDateShape(candidate))
            {
                return length;
            }
        }
        return 0;
    }
}
