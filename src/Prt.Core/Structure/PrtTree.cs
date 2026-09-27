using Prt.Core.Syntax;

namespace Prt.Core.Structure;

/// <summary>
/// 语法树遍历工具（内聚的单一职责：只负责枚举子节点与行内列表）。
/// 结构解析与渲染均复用本工具，避免各模块各写一套遍历逻辑。
/// </summary>
internal static class PrtTree
{
    /// <summary>按文档物理顺序深度优先枚举全部块。</summary>
    public static IEnumerable<PrtBlock> WalkBlocks(PrtBlock root)
    {
        yield return root;
        foreach (var child in GetChildren(root))
        {
            foreach (var descendant in WalkBlocks(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>取某块的直接子块（按块类型分派）。</summary>
    public static IEnumerable<PrtBlock> GetChildren(PrtBlock block)
    {
        switch (block)
        {
            case DocumentBlock d:
                foreach (var c in d.Children) yield return c;
                break;
            case QuoteBlock q:
                foreach (var c in q.Children) yield return c;
                break;
            case SemanticBlock s:
                foreach (var c in s.Children) yield return c;
                break;
            case SummaryBlock sm:
                foreach (var c in sm.Children) yield return c;
                break;
            case SectionBlock sec:
                foreach (var c in sec.Children) yield return c;
                break;
            case TextStyleBlock t:
                foreach (var c in t.Children) yield return c;
                break;
            case AlignBlock a:
                foreach (var c in a.Children) yield return c;
                break;
            case FigureBlock f:
                foreach (var c in f.Children) yield return c;
                break;
            case LayoutBlock l:
                foreach (var c in l.Children) yield return c;
                foreach (var col in l.ColumnItems)
                {
                    yield return col;
                }
                foreach (var tab in l.TabItems)
                {
                    yield return tab;
                }
                break;
            case ColumnBlock col2:
                foreach (var c in col2.Children) yield return c;
                break;
            case TabBlock tab2:
                foreach (var c in tab2.Children) yield return c;
                break;
            case ListBlock list:
                foreach (var item in list.Items) yield return item;
                break;
            case ListItemBlock item2:
                foreach (var c in item2.Children) yield return c;
                break;
        }
    }

    /// <summary>枚举树中全部「行内列表」（段落、标题、列表项、表格单元格等）。</summary>
    public static IEnumerable<List<PrtInline>> EnumerateInlineLists(PrtBlock root)
    {
        foreach (var block in WalkBlocks(root))
        {
            switch (block)
            {
                case HeadingBlock h:
                    yield return h.Inlines;
                    break;
                case ParagraphBlock p:
                    yield return p.Inlines;
                    break;
                case ListItemBlock li:
                    yield return li.Inlines;
                    break;
                case TableBlock t:
                    foreach (var cell in EnumerateCells(t.Table))
                    {
                        yield return cell.Inlines;
                    }
                    break;
                case PipeTableBlock pt:
                    foreach (var cell in EnumerateCells(pt.Table))
                    {
                        yield return cell.Inlines;
                    }
                    break;
            }
        }
    }

    /// <summary>枚举表格全部单元格。</summary>
    public static IEnumerable<TableCellData> EnumerateCells(TableModel table)
    {
        foreach (var row in EnumerateRows(table))
        {
            foreach (var cell in row.Cells)
            {
                yield return cell;
            }
        }
    }

    /// <summary>按「表头 → 表体 → 表尾」顺序枚举表格全部行。</summary>
    public static IEnumerable<TableRowData> EnumerateRows(TableModel table)
    {
        foreach (var row in table.HeaderRows) yield return row;
        foreach (var row in table.BodyRows) yield return row;
        foreach (var row in table.FooterRows) yield return row;
    }

    /// <summary>递归访问行内节点（含嵌套子节点）。</summary>
    public static void VisitInlines(IEnumerable<PrtInline> inlines, Action<PrtInline> visit)
    {
        foreach (var inline in inlines)
        {
            visit(inline);
            foreach (var child in GetInlineChildren(inline))
            {
                VisitInlines(child, visit);
            }
        }
    }

    private static IEnumerable<List<PrtInline>> GetInlineChildren(PrtInline inline)
    {
        switch (inline)
        {
            case EmphasisInline e:
                yield return e.Children;
                break;
            case StrikeInline s:
                yield return s.Children;
                break;
            case HighlightInline h:
                yield return h.Children;
                break;
            case UnderlineInline u:
                yield return u.Children;
                break;
            case SuperscriptInline sup:
                yield return sup.Children;
                break;
            case SubscriptInline sub:
                yield return sub.Children;
                break;
            case ColorInline c:
                yield return c.Children;
                break;
            case BackgroundColorInline bg:
                yield return bg.Children;
                break;
            case SizeInline size:
                yield return size.Children;
                break;
            case LinkInline link when !link.IsImage:
                yield return link.Children;
                break;
            case FootnoteInline fn:
                // 脚注内容以纯文本保存，不参与行内标记递归。
                _ = fn;
                break;
        }
    }
}
