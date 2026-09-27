using System.Text;
using Prt.Core.Diagnostics;
using Prt.Core.Syntax;

namespace Prt.Core.Parsing;

/// <summary>
/// 解析辅助工具：围栏识别、参数解析、保留符号与转义、受控取值解析。
/// 对应《PRT 标准》6.2（保留符号）、6.3（转义）、8.1（围栏语法）、12.1（受控取值）。
/// </summary>
internal static class ParsingHelpers
{
    /// <summary>
    /// 保留符号集合（标准 6.2）。出现在 `\` 之后时按字面输出，不触发标记。
    /// 说明：标准 6.2 的保留符号表中含 `==`、`{{ }}`、`[[ ]]`、`:::`、`##`、`%%` 等
    /// 多字符记号，其组成字符同样计入可转义集合。
    /// </summary>
    public static readonly HashSet<char> ReservedSymbols = new()
    {
        '*', '_', '~', '+', '^', '=', '#', '%', '|', '@', '\\',
        '{', '}', '[', ']', '(', ')', '!', ':',
    };

    /// <summary>行首前导空白上限（标准 6.4「行首」定义：前导空格不超过 3 个）。</summary>
    public const int MaxIndentForLineStart = 3;

    /// <summary>去掉前导空白并判断是否可视为「行首」。</summary>
    public static bool IsLineStart(string line, out string trimmed, out int indent)
    {
        indent = 0;
        while (indent < line.Length && line[indent] == ' ')
        {
            indent++;
        }
        trimmed = line[indent..];
        return indent <= MaxIndentForLineStart;
    }

    /// <summary>解析结果：围栏行信息。</summary>
    public readonly struct FenceLine
    {
        public FenceLine(bool isFence, bool isEnd, string? typeName, string header, bool isBare = false)
        {
            IsFence = isFence;
            IsEnd = isEnd;
            TypeName = typeName;
            Header = header;
            IsBare = isBare;
        }

        /// <summary>是否为围栏行。</summary>
        public bool IsFence { get; }

        /// <summary>是否为结束行（`:::` / `::: end` / `::: end 类型`）。</summary>
        public bool IsEnd { get; }

        /// <summary>起始行的类型名；结束行在给出类型名时同样填充。</summary>
        public string? TypeName { get; }

        /// <summary>起始行类型名之后的头部文本（含参数与自由文本）。</summary>
        public string Header { get; }

        /// <summary>
        /// 是否为「裸结束行」`:::`（无 `end` 关键字、无类型名）。
        /// 用于 9.1 节 `meta` 等结构块内「`:::` 作为可选分隔行、可留可删」的约定。
        /// </summary>
        public bool IsBare { get; }
    }

    /// <summary>
    /// 识别围栏行（标准 8.1）。仅当行首（前导空白不超过 3 个）出现连续三个冒号时触发。
    /// </summary>
    public static FenceLine ParseFenceLine(string line)
    {
        if (!IsLineStart(line, out var trimmed, out _))
        {
            return new FenceLine(false, false, null, string.Empty);
        }
        if (!trimmed.StartsWith(":::", StringComparison.Ordinal))
        {
            return new FenceLine(false, false, null, string.Empty);
        }

        var rest = trimmed[3..].Trim();
        if (rest.Length == 0)
        {
            // 单独的 `:::`：通用结束行（闭合最近一个未闭合围栏）。
            return new FenceLine(true, true, null, string.Empty, isBare: true);
        }

        // `::: end` 或 `::: end 类型名`
        if (rest == "end" || rest.StartsWith("end ", StringComparison.Ordinal))
        {
            var typeName = rest.Length == 3 ? null : rest[4..].Trim();
            return new FenceLine(true, true, typeName?.Length == 0 ? null : typeName, string.Empty);
        }

        // 起始行：第一个空白分隔的词为类型名，其余为头部。
        var spaceIndex = IndexOfWhitespace(rest);
        if (spaceIndex < 0)
        {
            return new FenceLine(true, false, rest, string.Empty);
        }
        return new FenceLine(true, false, rest[..spaceIndex], rest[spaceIndex..].Trim());
    }

    private static int IndexOfWhitespace(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>头部解析结果：键值参数、标志参数与剩余自由文本。</summary>
    public sealed class HeaderParts
    {
        public Dictionary<string, string> Parameters { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Flags { get; } = new();

        public string FreeText { get; set; } = string.Empty;
    }

    /// <summary>
    /// 解析围栏头部：前导的 `名称=值` 视为参数（值为裸文本或双引号字符串），
    /// 单独出现的 `名称` 视为标志参数（等价于 `名称=true`），其余保留为自由文本。
    /// </summary>
    public static HeaderParts ParseHeader(string header)
    {
        var result = new HeaderParts();
        var pos = 0;
        var length = header.Length;

        while (pos < length)
        {
            while (pos < length && char.IsWhiteSpace(header[pos]))
            {
                pos++;
            }
            if (pos >= length)
            {
                break;
            }

            var nameStart = pos;
            while (pos < length && IsNameChar(header[pos]))
            {
                pos++;
            }
            var name = header[nameStart..pos];

            // 标志参数：其后不是 `=`，且名称合法。
            if (pos >= length || header[pos] != '=')
            {
                if (name.Length > 0 && IsValidName(name))
                {
                    result.Flags.Add(name);
                    continue;
                }
                // 非法名称：整体作为自由文本。
                result.FreeText = header[nameStart..].Trim();
                return result;
            }

            // 键值参数。
            if (!IsValidName(name))
            {
                result.FreeText = header[nameStart..].Trim();
                return result;
            }

            pos++; // 跳过 '='
            string value;
            if (pos < length && header[pos] == '"')
            {
                pos++;
                var sb = new StringBuilder();
                while (pos < length && header[pos] != '"')
                {
                    sb.Append(header[pos]);
                    pos++;
                }
                if (pos < length)
                {
                    pos++; // 跳过结尾引号
                }
                value = sb.ToString();
            }
            else
            {
                var valueStart = pos;
                while (pos < length && !char.IsWhiteSpace(header[pos]))
                {
                    pos++;
                }
                value = header[valueStart..pos];
            }

            result.Parameters[name] = value;
        }

        return result;
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-';

    private static bool IsValidName(string name)
    {
        if (name.Length == 0)
        {
            return false;
        }
        foreach (var c in name)
        {
            if (!(c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c == '_' || c == '-'))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>解析命名色（标准 12.1）。</summary>
    public static bool TryParseNamedColor(string? text, out NamedColor color)
    {
        color = NamedColor.Black;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        switch (text.Trim().ToLowerInvariant())
        {
            case "black": color = NamedColor.Black; return true;
            case "white": color = NamedColor.White; return true;
            case "gray": case "grey": color = NamedColor.Gray; return true;
            case "navy": color = NamedColor.Navy; return true;
            case "red": color = NamedColor.Red; return true;
            case "orange": color = NamedColor.Orange; return true;
            case "yellow": color = NamedColor.Yellow; return true;
            case "green": color = NamedColor.Green; return true;
            case "teal": color = NamedColor.Teal; return true;
            case "blue": color = NamedColor.Blue; return true;
            case "purple": color = NamedColor.Purple; return true;
            case "cyan": color = NamedColor.Cyan; return true;
            case "magenta": color = NamedColor.Magenta; return true;
            case "brown": color = NamedColor.Brown; return true;
            case "olive": color = NamedColor.Olive; return true;
            case "maroon": color = NamedColor.Maroon; return true;
            case "danger": color = NamedColor.Danger; return true;
            case "warning": color = NamedColor.Warning; return true;
            case "info": color = NamedColor.Info; return true;
            case "success": color = NamedColor.Success; return true;
            default: return false;
        }
    }

    /// <summary>解析对齐取值（标准 12.1）。</summary>
    public static bool TryParseAlignment(string? text, out TextAlignment alignment)
    {
        alignment = TextAlignment.Left;
        switch (text?.Trim().ToLowerInvariant())
        {
            case "left": alignment = TextAlignment.Left; return true;
            case "center": alignment = TextAlignment.Center; return true;
            case "right": alignment = TextAlignment.Right; return true;
            case "justify": alignment = TextAlignment.Justify; return true;
            default: return false;
        }
    }

    /// <summary>解析字号档位（标准 12.1）。</summary>
    public static bool TryParseFontSize(string? text, out FontSizeScale size)
    {
        size = FontSizeScale.Normal;
        switch (text?.Trim().ToLowerInvariant())
        {
            case "small": size = FontSizeScale.Small; return true;
            case "normal": size = FontSizeScale.Normal; return true;
            case "large": size = FontSizeScale.Large; return true;
            case "xlarge": size = FontSizeScale.XLarge; return true;
            default: return false;
        }
    }

    /// <summary>解析章节编号样式（标准 9.2）。</summary>
    public static bool TryParseNumberingStyle(string? text, out NumberingStyle style)
    {
        style = NumberingStyle.Mixed;
        switch (text?.Trim().ToLowerInvariant())
        {
            case "mixed": style = NumberingStyle.Mixed; return true;
            case "cn": style = NumberingStyle.Cn; return true;
            case "arabic": style = NumberingStyle.Arabic; return true;
            case "roman": style = NumberingStyle.Roman; return true;
            case "none": style = NumberingStyle.None; return true;
            default: return false;
        }
    }

    /// <summary>解析布尔参数（标准 8.1：布尔值统一用 true/false）。</summary>
    public static bool TryParseBoolean(string? text, out bool value)
    {
        value = false;
        switch (text?.Trim().ToLowerInvariant())
        {
            case "true": value = true; return true;
            case "false": value = false; return true;
            default: return false;
        }
    }

    /// <summary>语义块类型名映射（标准 8.3）。</summary>
    public static bool TryParseSemanticKind(string typeName, out SemanticKind kind)
    {
        switch (typeName)
        {
            case "note": kind = SemanticKind.Note; return true;
            case "warning": kind = SemanticKind.Warning; return true;
            case "danger": kind = SemanticKind.Danger; return true;
            case "attention": kind = SemanticKind.Attention; return true;
            case "example": kind = SemanticKind.Example; return true;
            case "definition": kind = SemanticKind.Definition; return true;
            case "quote": kind = SemanticKind.Quote; return true;
            case "comment": kind = SemanticKind.Comment; return true;
            case "info": kind = SemanticKind.Info; return true;
            case "tip": kind = SemanticKind.Tip; return true;
            case "result": kind = SemanticKind.Result; return true;
            // `task` 与 `todo` 为同一类型的两个别名（标准 8.3）。
            case "task": case "todo": kind = SemanticKind.Task; return true;
            default: kind = SemanticKind.Note; return false;
        }
    }

    /// <summary>
    /// 判断是否为表格分隔行（标准 10.1）。形如 `| :--- | ---: | :---: |`，
    /// 每个单元格由可选冒号与至少一个短横线组成。
    /// </summary>
    public static bool IsTableSeparatorRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || (!trimmed.Contains('-', StringComparison.Ordinal) && !trimmed.Contains('=', StringComparison.Ordinal)))
        {
            return false;
        }

        var cells = SplitTableRow(line);
        if (cells.Count == 0)
        {
            return false;
        }

        foreach (var cell in cells)
        {
            var c = cell.Trim();
            if (c.Length == 0)
            {
                return false;
            }
            var start = 0;
            var end = c.Length;
            if (c[0] == ':')
            {
                start++;
            }
            if (end > start && c[end - 1] == ':')
            {
                end--;
            }
            if (end - start < 1)
            {
                return false;
            }
            for (var i = start; i < end; i++)
            {
                if (c[i] != '-')
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>切分管道表的一行，尊重 `\|` 转义（标准 10.1 / 6.3）。</summary>
    public static List<string> SplitTableRow(string line)
    {
        var cells = new List<string>();
        var trimmed = line.Trim();

        // 去掉首尾可选的竖线。
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }
        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed[..^1];
        }

        var sb = new StringBuilder();
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c == '\\' && i + 1 < trimmed.Length && trimmed[i + 1] == '|')
            {
                sb.Append("\\|"); // 保留转义序列，交由行内解析器处理
                i++;
                continue;
            }
            if (c == '|')
            {
                cells.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        cells.Add(sb.ToString());
        return cells;
    }

    /// <summary>由分隔行推导各列对齐方式。</summary>
    public static List<TableAlignment?> ParseAlignments(IReadOnlyList<string> separatorCells)
    {
        var result = new List<TableAlignment?>();
        foreach (var cell in separatorCells)
        {
            var c = cell.Trim();
            var left = c.StartsWith(':');
            var right = c.EndsWith(':') && c.Length > 1;
            if (left && right)
            {
                result.Add(TableAlignment.Center);
            }
            else if (right)
            {
                result.Add(TableAlignment.Right);
            }
            else if (left)
            {
                result.Add(TableAlignment.Left);
            }
            else
            {
                result.Add(null);
            }
        }
        return result;
    }

    /// <summary>
    /// 处理转义（标准 6.3）。返回去除转义后的字面文本。
    /// 该函数用于「非行内解析」场合（如 refs 行、参数值），行内解析另有内联处理。
    /// </summary>
    public static string Unescape(string text)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                var next = text[i + 1];
                if (next == '\\')
                {
                    sb.Append('\\');
                    i++;
                    continue;
                }
                if (ReservedSymbols.Contains(next))
                {
                    sb.Append(next);
                    i++;
                    continue;
                }
                // 非保留符号：输出字面反斜杠并保留该字符。
                sb.Append('\\');
                continue;
            }
            if (c == '\\')
            {
                sb.Append('\\');
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>判断字符串是否匹配固定位数日期时间文本 `YYYY-MM-DD[THH:MM[:SS]]`（标准 2.2）。</summary>
    public static bool IsDateShape(string text)
    {
        if (text.Length != 10 && text.Length != 16 && text.Length != 19)
        {
            return false;
        }
        return IsDigits(text, 0, 4) && text[4] == '-' && IsDigits(text, 5, 2) && text[7] == '-'
               && IsDigits(text, 8, 2)
               && (text.Length == 10 || (text[10] == 'T' && IsDigits(text, 11, 2) && text[13] == ':'
                   && IsDigits(text, 14, 2)
                   && (text.Length == 16 || (text[16] == ':' && IsDigits(text, 17, 2)))));
    }

    private static bool IsDigits(string text, int start, int count)
    {
        if (start + count > text.Length)
        {
            return false;
        }
        for (var i = start; i < start + count; i++)
        {
            if (text[i] is < '0' or > '9')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>是否为合法的变量名（标准 11.1）：字母开头，可含字母、数字、下划线。</summary>
    public static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }
        if (!char.IsLetter(name[0]) && name[0] != '_')
        {
            return false;
        }
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>将诊断写入集合；严格模式下为错误、宽松模式下为警告。</summary>
    public static void Report(
        DiagnosticBag diagnostics,
        bool strict,
        string code,
        string message,
        int line,
        int column)
    {
        if (strict)
        {
            diagnostics.Error(code, message, line, column);
        }
        else
        {
            diagnostics.Warn(code, message, line, column);
        }
    }
}
