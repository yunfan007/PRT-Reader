namespace Prt.Core.Text;

/// <summary>
/// 源文本中的一个物理行。行号从 1 起，Offset 为该行首字符在规范化文本中的字符偏移。
/// 对应《PRT 标准》6.1 节：行分隔符 CRLF/CR/LF 均视为行尾，解析前统一规范化为 LF。
/// </summary>
public sealed class SourceLine
{
    public SourceLine(int lineNumber, int offset, string text)
    {
        LineNumber = lineNumber;
        Offset = offset;
        Text = text;
    }

    /// <summary>行号（从 1 起）。</summary>
    public int LineNumber { get; }

    /// <summary>该行首字符在规范化文本中的字符偏移。</summary>
    public int Offset { get; }

    /// <summary>该行文本（不含行尾符）。</summary>
    public string Text { get; }

    /// <summary>该行结束位置的字符偏移（不含行尾符）。</summary>
    public int End => Offset + Text.Length;

    /// <summary>该行是否为空白行（仅含空白字符）。</summary>
    public bool IsBlank => Text.Trim().Length == 0;

    /// <summary>返回从 <paramref name="column"/>（从 1 起）开始的子串。</summary>
    public string SliceFrom(int column)
    {
        var index = column - 1;
        if (index <= 0)
        {
            return Text;
        }
        return index >= Text.Length ? string.Empty : Text[index..];
    }
}

/// <summary>
/// 规范化后的源文本。负责按行切分、以及「字符偏移 ↔ 行/列」的双向换算，
/// 供错误报告输出「行号:列号」使用（《PRT 标准》14.4 节，行列均从 1 起）。
/// </summary>
public sealed class SourceText
{
    private readonly List<SourceLine> _lines;

    private SourceText(string text, List<SourceLine> lines)
    {
        Text = text;
        _lines = lines;
    }

    /// <summary>规范化（行尾统一为 LF）后的完整源文本。</summary>
    public string Text { get; }

    /// <summary>全部物理行。</summary>
    public IReadOnlyList<SourceLine> Lines => _lines;

    public int LineCount => _lines.Count;

    /// <summary>由任意文本构造 SourceText；CRLF 与 CR 均规范化为 LF。</summary>
    public static SourceText From(string? text)
    {
        text ??= string.Empty;

        // 规范化行尾：先处理 CRLF，再处理单独的 CR，保证 LF 语义唯一。
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = new List<SourceLine>();
        var lineNumber = 1;

        var start = 0;
        for (var i = 0; i <= normalized.Length; i++)
        {
            if (i == normalized.Length || normalized[i] == '\n')
            {
                var lineText = normalized.Substring(start, i - start);
                lines.Add(new SourceLine(lineNumber, start, lineText));
                lineNumber++;
                start = i + 1;
            }
        }

        // 空文本也保证至少有一行，便于统一处理。
        if (lines.Count == 0)
        {
            lines.Add(new SourceLine(1, 0, string.Empty));
        }

        return new SourceText(normalized, lines);
    }

    /// <summary>按行号（从 1 起）取行；越界时钳制到有效范围。</summary>
    public SourceLine GetLine(int lineNumber)
    {
        if (lineNumber < 1)
        {
            lineNumber = 1;
        }
        if (lineNumber > _lines.Count)
        {
            lineNumber = _lines.Count;
        }
        return _lines[lineNumber - 1];
    }

    /// <summary>将字符偏移换算为「行号, 列号」（均从 1 起）。</summary>
    public (int Line, int Column) GetLineColumn(int offset)
    {
        if (offset < 0)
        {
            offset = 0;
        }
        if (offset > Text.Length)
        {
            offset = Text.Length;
        }

        // 线性扫描足够快（文档级别），且避免引入额外索引结构。
        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            var line = _lines[i];
            if (offset >= line.Offset)
            {
                return (line.LineNumber, offset - line.Offset + 1);
            }
        }
        return (1, 1);
    }
}
