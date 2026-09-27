using System.Text;
using Prt.Core.Rendering;
using Prt.Core.Syntax;

namespace Prt.Core.Export;

/// <summary>
/// 纯文本渲染器（《PRT 标准》第 13 章降级协议：去样式、保留文字、不丢内容）。
/// </summary>
public sealed class PlainTextRenderer
{
    /// <summary>渲染为纯文本。</summary>
    public static string Render(PrtDocument document)
    {
        var builder = new StringBuilder();
        var renderer = new PlainTextRenderer(builder) { CurrentStructure = document.Structure };
        renderer.WriteBlocks(document.Root.Children, 0);
        renderer.WriteFootnoteArea(document);
        return builder.ToString().TrimEnd() + "\n";
    }

    private readonly StringBuilder _sb;

    private PlainTextRenderer(StringBuilder sb)
    {
        _sb = sb;
    }

    private void WriteBlocks(IEnumerable<PrtBlock> blocks, int indent)
    {
        foreach (var block in blocks)
        {
            WriteBlock(block, indent);
        }
    }

    private void WriteBlock(PrtBlock block, int indent)
    {
        var pad = new string(' ', indent * 2);

        switch (block)
        {
            case HeadingBlock heading:
                _sb.Append(pad).Append(JoinNumber(heading.AssignedNumber, heading.PlainText)).Append('\n');
                break;

            case ParagraphBlock paragraph:
                _sb.Append(pad);
                AppendInlines(paragraph.Inlines);
                _sb.Append('\n');
                break;

            case ListBlock list:
                WriteList(list, indent);
                break;

            case QuoteBlock quote:
                WriteQuote(quote, indent);
                break;

            case CodeBlock code:
                foreach (var line in code.Code.Split('\n'))
                {
                    _sb.Append(pad).Append("    ").Append(line).Append('\n');
                }
                break;

            case ThematicBreakBlock:
                _sb.Append(pad).Append("--------").Append('\n');
                break;

            case PipeTableBlock pipe:
                WriteTable(pipe.Table, indent);
                break;

            case TableBlock table:
                if (!string.IsNullOrEmpty(table.Caption))
                {
                    _sb.Append(pad).Append('表').Append(table.AssignedNumber).Append(": ")
                        .Append(table.Caption).Append('\n');
                }
                WriteTable(table.Table, indent);
                break;

            case SemanticBlock semantic:
                _sb.Append(pad).Append('[').Append(SemanticNaming.Label(semantic.Kind)).Append(']');
                if (!string.IsNullOrEmpty(semantic.Title))
                {
                    _sb.Append(semantic.Title);
                }
                _sb.Append('\n');
                WriteBlocks(semantic.Children, indent + 1);
                break;

            case LayoutBlock layout:
                WriteLayout(layout, indent);
                break;

            case SectionBlock section:
                _sb.Append(pad).Append(JoinNumber(section.AssignedNumber, section.Title)).Append('\n');
                WriteBlocks(section.Children, indent);
                break;

            case SummaryBlock summary:
                _sb.Append(pad).Append("【摘要】").Append('\n');
                WriteBlocks(summary.Children, indent + 1);
                break;

            case TextStyleBlock text:
                WriteBlocks(text.Children, indent);
                break;

            case AlignBlock align:
                WriteBlocks(align.Children, indent);
                break;

            case FigureBlock figure:
                _sb.Append(pad).Append('图').Append(figure.AssignedNumber).Append(": ")
                    .Append(figure.Caption ?? figure.Alt ?? string.Empty).Append('\n');
                if (!string.IsNullOrEmpty(figure.Source))
                {
                    _sb.Append(pad).Append("    ").Append(figure.Source).Append('\n');
                }
                WriteBlocks(figure.Children, indent + 1);
                break;

            case EquationBlock equation:
                _sb.Append(pad).Append("式(").Append(equation.AssignedNumber).Append("): ")
                    .Append(equation.Source).Append('\n');
                break;

            // 【COMP 不支持】计算块按第 13 章降级为源码文本，保证不丢内容。
            case ComputationBlock computation:
                foreach (var lineText in computation.Source.Split('\n'))
                {
                    _sb.Append(pad).Append("    ").Append(lineText).Append('\n');
                }
                break;

            case UnknownBlock unknown:
                foreach (var lineText in unknown.Body.Split('\n'))
                {
                    _sb.Append(pad).Append(lineText).Append('\n');
                }
                break;

            // meta / refs / theme 不进入正文（标准 9.1 / 9.4 / 12.2）。
            case MetaBlock:
            case RefsBlock:
            case ThemeBlock:
                break;
        }
    }

    private void WriteList(ListBlock list, int indent)
    {
        var pad = new string(' ', indent * 2);
        var index = list.StartNumber;
        foreach (var item in list.Items)
        {
            _sb.Append(pad);
            if (item.IsTask)
            {
                _sb.Append(item.IsChecked ? "[x] " : "[ ] ");
            }
            else if (list.Ordered)
            {
                _sb.Append(index).Append(". ");
            }
            else
            {
                _sb.Append("- ");
            }
            AppendInlines(item.Inlines);
            _sb.Append('\n');
            WriteBlocks(item.Children, indent + 1);
            index++;
        }
    }

    private void WriteQuote(QuoteBlock quote, int indent)
    {
        var nested = PlainTextRenderer.RenderInternal(quote.Children);
        var pad = new string(' ', indent * 2);
        foreach (var line in nested.Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }
            _sb.Append(pad).Append("> ").Append(line).Append('\n');
        }
    }

    private static string RenderInternal(IEnumerable<PrtBlock> blocks)
    {
        var sb = new StringBuilder();
        var renderer = new PlainTextRenderer(sb);
        renderer.WriteBlocks(blocks, 0);
        return sb.ToString();
    }

    private void WriteLayout(LayoutBlock layout, int indent)
    {
        switch (layout.Kind)
        {
            case LayoutKind.Columns:
                foreach (var column in layout.ColumnItems)
                {
                    WriteBlocks(column.Children, indent);
                }
                break;

            case LayoutKind.Collapse:
                _sb.Append(new string(' ', indent * 2))
                    .Append("【折叠：").Append(layout.Title ?? string.Empty).Append('】').Append('\n');
                WriteBlocks(layout.Children, indent + 1);
                break;

            case LayoutKind.Tabs:
                foreach (var tab in layout.TabItems)
                {
                    _sb.Append(new string(' ', indent * 2))
                        .Append("【标签：").Append(tab.Name ?? string.Empty).Append('】').Append('\n');
                    WriteBlocks(tab.Children, indent + 1);
                }
                break;
        }
    }

    private void WriteTable(TableModel table, int indent)
    {
        var pad = new string(' ', indent * 2);
        foreach (var row in PrtTreeRows(table))
        {
            _sb.Append(pad);
            foreach (var cell in row.Cells)
            {
                if (cell.IsCovered)
                {
                    _sb.Append("(合并) ");
                    continue;
                }
                _sb.Append(PlainInline(cell.Inlines).Replace('\n', ' ')).Append('\t');
            }
            _sb.Append('\n');
        }
    }

    private static IEnumerable<TableRowData> PrtTreeRows(TableModel table)
    {
        foreach (var row in table.HeaderRows) yield return row;
        foreach (var row in table.BodyRows) yield return row;
        foreach (var row in table.FooterRows) yield return row;
    }

    private void WriteFootnoteArea(PrtDocument document)
    {
        if (document.Structure.Footnotes.Count == 0)
        {
            return;
        }
        _sb.Append('\n').Append("脚注:").Append('\n');
        foreach (var footnote in document.Structure.Footnotes)
        {
            _sb.Append(footnote.Number).Append(". ").Append(footnote.Content).Append('\n');
        }
    }

    private void AppendInlines(IEnumerable<PrtInline> inlines)
    {
        foreach (var inline in inlines)
        {
            AppendInline(inline);
        }
    }

    private void AppendInline(PrtInline inline)
    {
        switch (inline)
        {
            case TextInline t: _sb.Append(t.Text); break;
            case LiteralInline l: _sb.Append(l.Text); break;
            case CodeInline c: _sb.Append(c.Text); break;
            case EmphasisInline e: AppendInlines(e.Children); break;
            case StrikeInline s: AppendInlines(s.Children); break;
            case HighlightInline h: AppendInlines(h.Children); break;
            case UnderlineInline u: AppendInlines(u.Children); break;
            case SuperscriptInline sup: AppendInlines(sup.Children); break;
            case SubscriptInline sub: AppendInlines(sub.Children); break;
            case ColorInline col: AppendInlines(col.Children); break;
            case BackgroundColorInline bg: AppendInlines(bg.Children); break;
            case SizeInline size: AppendInlines(size.Children); break;
            case LinkInline link:
                if (link.IsImage)
                {
                    _sb.Append('[').Append(link.Alt).Append(']');
                }
                else
                {
                    AppendInlines(link.Children);
                    _sb.Append(" (").Append(link.Url).Append(')');
                }
                break;

            // 键盘按键降级为 `[Ctrl]` / `[Ctrl+K]`（标准 13）。
            case KbdInline kbd:
                _sb.Append('[').Append(kbd.Keys).Append(']');
                break;

            case RefInline reference:
                _sb.Append(reference.ResolvedText ?? reference.DisplayText ?? reference.TargetId);
                break;

            case TermLinkInline term:
                _sb.Append(term.ResolvedText ?? term.DisplayText ?? term.Key);
                break;

            case ContentsInline contents:
            {
                var entries = TocHelper.Filter(CurrentStructure!, contents.Depth, contents.Mode);
                foreach (var entry in entries)
                {
                    _sb.Append('\n').Append(new string(' ', (entry.Level - 1) * 2))
                        .Append(JoinNumber(entry.Number, entry.Title));
                }
                break;
            }

            // 【COMP 不支持】插值输出原文（标准 13）。
            case InterpolationInline interpolation:
                _sb.Append(interpolation.LiteralText);
                break;

            case DateLiteralInline date:
                _sb.Append(date.Text);
                break;

            case LineBreakInline:
                _sb.Append('\n');
                break;

            // 脚注在正文保留上标序号（以 [N] 形式），内容集中列于文末（标准 13）。
            case FootnoteInline footnote:
                _sb.Append('[').Append(footnote.Number).Append(']');
                break;
        }
    }

    /// <summary>目录指令渲染时使用的文档结构（由 <see cref="Render"/> 设置）。</summary>
    private Structure.DocumentStructure? CurrentStructure { get; set; }

    private static string PlainInline(IEnumerable<PrtInline> inlines)
    {
        var sb = new StringBuilder();
        var renderer = new PlainTextRenderer(sb);
        renderer.AppendInlines(inlines);
        return sb.ToString();
    }

    private static string JoinNumber(string? number, string? title)
        => string.IsNullOrEmpty(number) ? title ?? string.Empty : $"{number} {title}".TrimEnd();
}
