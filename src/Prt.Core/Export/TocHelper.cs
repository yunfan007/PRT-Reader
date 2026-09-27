using Prt.Core.Structure;
using Prt.Core.Syntax;

namespace Prt.Core.Export;

/// <summary>目录过滤（标准 9.3：`depth` 限深、`mode=numbered` 仅列出参与编号的章节）。</summary>
internal static class TocHelper
{
    public static List<TocEntry> Filter(DocumentStructure structure, int? depth, string mode)
    {
        var result = new List<TocEntry>();
        foreach (var entry in structure.Toc)
        {
            if (depth.HasValue && entry.Level > depth.Value)
            {
                continue;
            }
            if (string.Equals(mode, "numbered", StringComparison.OrdinalIgnoreCase) && !entry.IsNumbered)
            {
                continue;
            }
            result.Add(entry);
        }
        return result;
    }

    /// <summary>取行内列表中所有脚注节点（按出现顺序）。</summary>
    public static IEnumerable<FootnoteInline> Footnotes(IEnumerable<PrtInline> inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is FootnoteInline fn)
            {
                yield return fn;
            }
            foreach (var list in InlineChildLists(inline))
            {
                foreach (var nested in Footnotes(list))
                {
                    yield return nested;
                }
            }
        }
    }

    private static IEnumerable<List<PrtInline>> InlineChildLists(PrtInline inline)
    {
        switch (inline)
        {
            case EmphasisInline e: yield return e.Children; break;
            case StrikeInline s: yield return s.Children; break;
            case HighlightInline h: yield return h.Children; break;
            case UnderlineInline u: yield return u.Children; break;
            case SuperscriptInline sup: yield return sup.Children; break;
            case SubscriptInline sub: yield return sub.Children; break;
            case ColorInline c: yield return c.Children; break;
            case BackgroundColorInline bg: yield return bg.Children; break;
            case SizeInline size: yield return size.Children; break;
            case LinkInline link when !link.IsImage: yield return link.Children; break;
        }
    }
}
