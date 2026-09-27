namespace Prt.Core.Structure;

using Prt.Core.Syntax;

/// <summary>脚注条目（标准 7.2）。编号与排序均按脚注在文档中的物理首次出现顺序确定。</summary>
public sealed class FootnoteEntry
{
    public FootnoteEntry(int number, string content)
    {
        Number = number;
        Content = content;
    }

    /// <summary>脚注序号（从 1 起）。</summary>
    public int Number { get; }

    /// <summary>脚注内容。</summary>
    public string Content { get; }

    /// <summary>引用次数（同一内容被多处引用时复用同一编号）。</summary>
    public int ReferenceCount { get; set; } = 1;
}

/// <summary>目录条目（标准 9.3）。</summary>
public sealed class TocEntry
{
    /// <summary>层级（从 1 起）。</summary>
    public int Level { get; set; }

    /// <summary>自动编号文本（可能为空）。</summary>
    public string Number { get; set; } = string.Empty;

    /// <summary>标题文本。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>锚点 id（章节才有）。</summary>
    public string? AnchorId { get; set; }

    /// <summary>是否为 section 块（否则为隐式标题块）。</summary>
    public bool IsSection { get; set; }

    /// <summary>是否参与编号（用于 `mode=numbered` 过滤）。</summary>
    public bool IsNumbered { get; set; }

    /// <summary>
    /// 产生该目录条目的源块（section 或标题块）。
    /// 供查看器建立「目录项 → 渲染元素」的映射以实现跳转，渲染器无需依赖顺序约定。
    /// </summary>
    public PrtBlock? Source { get; set; }
}

/// <summary>文档结构解析结果：目录、脚注等派生信息。</summary>
public sealed class DocumentStructure
{
    public List<TocEntry> Toc { get; } = new();

    public List<FootnoteEntry> Footnotes { get; } = new();

    /// <summary>按脚注内容索引，用于复用同一编号（标准 7.2）。</summary>
    private readonly Dictionary<string, FootnoteEntry> _footnoteByContent = new(StringComparer.Ordinal);

    /// <summary>登记脚注并返回其编号；同一内容复用同一编号。</summary>
    public int RegisterFootnote(string content)
    {
        if (_footnoteByContent.TryGetValue(content, out var existing))
        {
            existing.ReferenceCount++;
            return existing.Number;
        }
        var entry = new FootnoteEntry(Footnotes.Count + 1, content);
        Footnotes.Add(entry);
        _footnoteByContent[content] = entry;
        return entry.Number;
    }
}
