using System.Text;

namespace Prt.App.Services;

/// <summary>整行的「可视编辑」目标形态：给光标所在行换一种角色，不需要手打标记。</summary>
internal enum LineKind
{
    /// <summary>普通段落（去掉行首标记）。</summary>
    Plain,

    /// <summary>一级标题。</summary>
    Heading1,

    /// <summary>二级标题。</summary>
    Heading2,

    /// <summary>三级标题。</summary>
    Heading3,

    /// <summary>四级标题。</summary>
    Heading4,

    /// <summary>五级标题。</summary>
    Heading5,

    /// <summary>六级标题。</summary>
    Heading6,

    /// <summary>无序列表项。</summary>
    Bullet,

    /// <summary>有序列表项。</summary>
    Ordered,

    /// <summary>任务清单项。</summary>
    Task,

    /// <summary>引用行。</summary>
    Quote,

    /// <summary>包成代码块（整行变成三行围栏）。</summary>
    Code,
}

/// <summary>
/// 「这一行」的可视编辑：把光标所在行在「标题 / 列表 / 任务 / 引用 / 代码块 / 普通段落」之间切换，
/// 以及整行上下移动。纯文本换算，不接触控件，可被自检覆盖。
/// </summary>
internal static class LineEdit
{
    /// <summary>把光标所在行改写为目标形态；返回 null 表示无需改动。</summary>
    public static EditResult? Apply(string text, int caret, LineKind kind)
    {
        var (start, end) = PrtSnippets.LineBounds(text, caret);
        var line = text[start..end];
        var (indent, body) = SplitLine(line);

        if (kind == LineKind.Code)
        {
            // 已是代码块的单行（以 ``` 起止）则再包一层没有意义——保持原样。
            if (body.StartsWith("```", StringComparison.Ordinal))
            {
                return null;
            }

            var fenced = "```" + Environment.NewLine + body + Environment.NewLine + "```";
            return new EditResult(start, line.Length, fenced, start + 4, body.Length);
        }

        var prefix = kind switch
        {
            LineKind.Heading1 => "# ",
            LineKind.Heading2 => "## ",
            LineKind.Heading3 => "### ",
            LineKind.Heading4 => "#### ",
            LineKind.Heading5 => "##### ",
            LineKind.Heading6 => "###### ",
            LineKind.Bullet => "- ",
            LineKind.Ordered => "1. ",
            LineKind.Task => "- [ ] ",
            LineKind.Quote => "> ",
            _ => string.Empty,
        };

        var rewritten = indent + prefix + body;
        if (string.Equals(rewritten, line, StringComparison.Ordinal))
        {
            return null;
        }

        return new EditResult(start, line.Length, rewritten, start + indent.Length + prefix.Length, body.Length);
    }

    /// <summary>把光标所在行上移（delta &lt; 0）或下移（delta &gt; 0）一行；边界处返回 null。</summary>
    public static EditResult? MoveLine(string text, int caret, int delta)
    {
        if (delta == 0)
        {
            return null;
        }

        var (start, end) = PrtSnippets.LineBounds(text, caret);
        var line = text[start..end];
        var newline = PrtSnippets.NewlineOf(text);

        if (delta < 0)
        {
            // 与上一行交换（上一行存在时）。
            if (start == 0)
            {
                return null;
            }

            var previousEnd = start;
            var previousStart = previousEnd;
            while (previousStart > 0 && text[previousStart - 1] is not ('\r' or '\n'))
            {
                previousStart--;
            }

            var previousLine = text[previousStart..previousEnd];
            previousLine = previousLine.TrimEnd('\r', '\n');
            var separator = text[(previousStart + previousLine.Length)..start];

            var built = line + separator + previousLine;
            return new EditResult(previousStart, start - previousStart + line.Length, built,
                previousStart, line.Length);
        }

        // 下移：与下一行交换（下一行存在时）。
        var nextStart = end;
        while (nextStart < text.Length && text[nextStart] is '\r' or '\n')
        {
            nextStart++;
        }

        if (nextStart >= text.Length)
        {
            return null;
        }

        var nextEnd = nextStart;
        while (nextEnd < text.Length && text[nextEnd] is not ('\r' or '\n'))
        {
            nextEnd++;
        }

        var nextLine = text[nextStart..nextEnd];
        var separatorAfter = text[end..nextStart];

        var rebuilt = nextLine + separatorAfter + line;
        var caretOffset = nextLine.Length + separatorAfter.Length;
        return new EditResult(start, nextEnd - start, rebuilt, start + caretOffset, line.Length);
    }

    /// <summary>拆出行的「缩进」与「去掉行首标记后的内容」。</summary>
    private static (string Indent, string Body) SplitLine(string line)
    {
        var index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
        {
            index++;
        }

        var indent = line[..index];
        var rest = line[index..];

        // 行首标记：标题 / 任务项 / 列表 / 引用（按最长匹配优先）。
        var body = StripMarkers(rest);
        return (indent, body);
    }

    private static string StripMarkers(string rest)
    {
        if (rest.Length == 0)
        {
            return rest;
        }

        // 标题：### 后跟空格
        var hashes = 0;
        while (hashes < rest.Length && rest[hashes] == '#')
        {
            hashes++;
        }

        if (hashes is >= 1 and <= 6 && hashes < rest.Length && rest[hashes] == ' ')
        {
            return rest[(hashes + 1)..].TrimStart();
        }

        // 任务项：- [ ] / - [x]
        if (rest.StartsWith("- [", StringComparison.Ordinal) && rest.Length > 4 && rest[4] == ']')
        {
            return rest[5..].TrimStart();
        }

        // 无序列表：- * + 后跟空格
        if (rest.Length > 1 && rest[0] is '-' or '*' or '+' && rest[1] == ' ')
        {
            return rest[2..].TrimStart();
        }

        // 有序列表：数字 + . 后跟空格
        var digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
        {
            digits++;
        }

        if (digits > 0 && digits + 1 < rest.Length && rest[digits] == '.' && rest[digits + 1] == ' ')
        {
            return rest[(digits + 2)..].TrimStart();
        }

        // 引用：> 后跟空格
        if (rest.Length > 1 && rest[0] == '>' && rest[1] == ' ')
        {
            return rest[2..].TrimStart();
        }

        return rest;
    }
}
