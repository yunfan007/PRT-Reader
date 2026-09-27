using System.Text;
using Prt.Core.Rendering;
using Prt.Core.Syntax;

namespace Prt.Core.Export;

/// <summary>
/// Markdown 导出渲染器，实现《PRT 标准》13.1（兼容子集无损导出）与
/// 13.2（超出子集时的显式降级协议）。
/// </summary>
public sealed class MarkdownRenderer
{
    /// <summary>渲染为 Markdown。</summary>
    public static string Render(PrtDocument document)
    {
        var builder = new StringBuilder();
        var renderer = new MarkdownRenderer(builder) { Structure = document.Structure };
        renderer.WriteBlocks(document.Root.Children);
        renderer.WriteFootnoteArea(document);
        return builder.ToString().TrimEnd() + "\n";
    }

    private readonly StringBuilder _sb;

    private MarkdownRenderer(StringBuilder sb)
    {
        _sb = sb;
    }

    private Structure.DocumentStructure? Structure { get; set; }

    private void WriteBlocks(IEnumerable<PrtBlock> blocks)
    {
        foreach (var block in blocks)
        {
            WriteBlock(block);
        }
    }

    private void WriteBlock(PrtBlock block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                // 自动编号是结构解析阶段派生的结果，不再写回 Markdown，
                // 否则再次解析会发生二次编号，破坏可逆性（标准 13.1）。
                _sb.Append(new string('#', heading.Level)).Append(' ')
                    .Append(heading.PlainText).Append('\n').Append('\n');
                break;

            case ParagraphBlock paragraph:
                AppendInlines(paragraph.Inlines);
                _sb.Append('\n').Append('\n');
                break;

            case ListBlock list:
                WriteList(list, 0);
                _sb.Append('\n');
                break;

            case QuoteBlock quote:
                WriteQuote(quote);
                break;

            case CodeBlock code:
                _sb.Append("```").Append(code.Language ?? string.Empty).Append('\n')
                    .Append(code.Code).Append('\n').Append("```").Append('\n').Append('\n');
                break;

            case ThematicBreakBlock:
                _sb.Append("---").Append('\n').Append('\n');
                break;

            case PipeTableBlock pipe:
                WritePipeTable(pipe.Table);
                break;

            case TableBlock table:
                if (!string.IsNullOrEmpty(table.Caption))
                {
                    _sb.Append("**表").Append(table.AssignedNumber).Append(": ").Append(table.Caption).Append("**")
                        .Append('\n').Append('\n');
                }
                WritePipeTable(table.Table);
                break;

            // 语义块 / 布局块降级为「前缀名称 + 折行文本」（标准 13.2）。
            case SemanticBlock semantic:
                _sb.Append('[').Append(SemanticNaming.Label(semantic.Kind)).Append(']');
                if (!string.IsNullOrEmpty(semantic.Title))
                {
                    _sb.Append(' ').Append(semantic.Title);
                }
                _sb.Append('\n').Append('\n');
                WriteBlocks(semantic.Children);
                break;

            case LayoutBlock layout:
                WriteLayout(layout);
                break;

            case SectionBlock section:
                // 同 HeadingBlock：编号为派生结果，导出时省略以保持可逆。
                _sb.Append(new string('#', Math.Clamp(section.NumberLevel, 1, 6))).Append(' ')
                    .Append(section.Title ?? string.Empty).Append('\n').Append('\n');
                WriteBlocks(section.Children);
                break;

            case SummaryBlock summary:
                _sb.Append("*摘要*").Append('\n').Append('\n');
                WriteBlocks(summary.Children);
                break;

            case TextStyleBlock text:
                WriteBlocks(text.Children);
                break;

            case AlignBlock align:
                WriteBlocks(align.Children);
                break;

            case FigureBlock figure:
                _sb.Append("![")
                    .Append(string.IsNullOrEmpty(figure.Alt) ? figure.Caption ?? string.Empty : figure.Alt)
                    .Append("](").Append(figure.Source ?? string.Empty).Append(')').Append('\n').Append('\n');
                _sb.Append('图').Append(figure.AssignedNumber).Append(": ")
                    .Append(figure.Caption ?? string.Empty).Append('\n').Append('\n');
                WriteBlocks(figure.Children);
                break;

            case EquationBlock equation:
                _sb.Append("```").Append('\n').Append(equation.Source).Append('\n').Append("```")
                    .Append('\n').Append('\n');
                break;

            // 【COMP 不支持】计算块降级为代码块文本（标准 13.2）。
            case ComputationBlock computation:
                _sb.Append("```").Append('\n').Append(computation.Source).Append('\n').Append("```")
                    .Append('\n').Append('\n');
                break;

            case UnknownBlock unknown:
                _sb.Append(unknown.Body).Append('\n').Append('\n');
                break;

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
                _sb.Append(item.IsChecked ? "- [x] " : "- [ ] ");
            }
            else
            {
                _sb.Append(list.Ordered ? $"{index}. " : "- ");
            }
            AppendInlines(item.Inlines);
            _sb.Append('\n');
            if (item.Children.Count > 0)
            {
                var nested = new MarkdownRenderer(new StringBuilder());
                nested.WriteBlocks(item.Children);
                foreach (var line in nested._sb.ToString().TrimEnd('\n').Split('\n'))
                {
                    _sb.Append(pad).Append("  ").Append(line).Append('\n');
                }
            }
            index++;
        }
    }

    private void WriteQuote(QuoteBlock quote)
    {
        var nested = new MarkdownRenderer(new StringBuilder());
        nested.WriteBlocks(quote.Children);
        foreach (var line in nested._sb.ToString().TrimEnd('\n').Split('\n'))
        {
            _sb.Append("> ").Append(line).Append('\n');
        }
        _sb.Append('\n');
    }

    private void WriteLayout(LayoutBlock layout)
    {
        switch (layout.Kind)
        {
            case LayoutKind.Columns:
                foreach (var column in layout.ColumnItems)
                {
                    WriteBlocks(column.Children);
                }
                break;

            case LayoutKind.Collapse:
                _sb.Append("**[").Append(layout.Title ?? "折叠").Append("]**").Append('\n').Append('\n');
                WriteBlocks(layout.Children);
                break;

            case LayoutKind.Tabs:
                foreach (var tab in layout.TabItems)
                {
                    _sb.Append("**[").Append(tab.Name ?? "标签").Append("]**").Append('\n').Append('\n');
                    WriteBlocks(tab.Children);
                }
                break;
        }
    }

    /// <summary>
    /// 输出标准 Markdown 管道表。完整表格的合并处以 `(合并)` 占位（标准 13.2）。
    /// </summary>
    private void WritePipeTable(TableModel table)
    {
        var columnCount = Math.Max(1, table.ColumnCount);

        var headerRows = table.HeaderRows.Count > 0 ? table.HeaderRows : table.BodyRows.Count > 0
            ? new List<TableRowData> { table.BodyRows[0] }
            : new List<TableRowData>();

        foreach (var row in headerRows)
        {
            WriteTableRow(row, columnCount);
        }

        // 分隔行。
        _sb.Append('|');
        for (var i = 0; i < columnCount; i++)
        {
            var alignment = i < table.Alignments.Count ? table.Alignments[i] : null;
            _sb.Append(alignment switch
            {
                TableAlignment.Left => " :--- |",
                TableAlignment.Right => " ---: |",
                TableAlignment.Center => " :---: |",
                _ => " --- |",
            });
        }
        _sb.Append('\n');

        var body = table.HeaderRows.Count > 0 ? table.BodyRows : table.BodyRows.Skip(1).ToList();
        foreach (var row in body)
        {
            WriteTableRow(row, columnCount);
        }
        foreach (var row in table.FooterRows)
        {
            WriteTableRow(row, columnCount);
        }

        _sb.Append('\n');
    }

    private void WriteTableRow(TableRowData row, int columnCount)
    {
        _sb.Append('|');
        for (var i = 0; i < columnCount && i < row.Cells.Count; i++)
        {
            var cell = row.Cells[i];
            _sb.Append(' ');
            if (cell.IsCovered)
            {
                _sb.Append("(合并)");
            }
            else
            {
                _sb.Append(InlineMarkdown(cell.Inlines).Replace("|", "\\|", StringComparison.Ordinal).Replace('\n', ' '));
            }
            _sb.Append(" |");
        }
        _sb.Append('\n');
    }

    private void WriteFootnoteArea(PrtDocument document)
    {
        if (document.Structure.Footnotes.Count == 0)
        {
            return;
        }
        _sb.Append('\n');
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
            case TextInline t:
                _sb.Append(EscapeText(t.Text));
                break;

            case LiteralInline l:
                _sb.Append(EscapeText(l.Text));
                break;

            case CodeInline c:
                _sb.Append('`').Append(c.Text).Append('`');
                break;

            case EmphasisInline e:
                if (e.Bold && e.Italic)
                {
                    _sb.Append("***");
                    AppendInlines(e.Children);
                    _sb.Append("***");
                }
                else if (e.Bold)
                {
                    _sb.Append("**");
                    AppendInlines(e.Children);
                    _sb.Append("**");
                }
                else
                {
                    _sb.Append('*');
                    AppendInlines(e.Children);
                    _sb.Append('*');
                }
                break;

            case StrikeInline s:
                _sb.Append("~~");
                AppendInlines(s.Children);
                _sb.Append("~~");
                break;

            // 高亮 / 下划线 / 上下标：去标记的普通文本（标准 13.2）。
            case HighlightInline h:
                AppendInlines(h.Children);
                break;
            case UnderlineInline u:
                AppendInlines(u.Children);
                break;
            case SuperscriptInline sup:
                AppendInlines(sup.Children);
                break;
            case SubscriptInline sub:
                AppendInlines(sub.Children);
                break;
            case ColorInline col:
                AppendInlines(col.Children);
                break;
            case BackgroundColorInline bg:
                AppendInlines(bg.Children);
                break;
            case SizeInline size:
                AppendInlines(size.Children);
                break;

            case LinkInline link:
                if (link.IsImage)
                {
                    _sb.Append("![").Append(link.Alt).Append("](").Append(link.Url).Append(')');
                }
                else
                {
                    _sb.Append('[');
                    AppendInlines(link.Children);
                    _sb.Append("](").Append(link.Url).Append(')');
                }
                break;

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
                var entries = TocHelper.Filter(Structure!, contents.Depth, contents.Mode);
                foreach (var entry in entries)
                {
                    _sb.Append('\n').Append(new string(' ', Math.Max(0, entry.Level - 1) * 2))
                        .Append("- ").Append(JoinNumber(entry.Number, entry.Title));
                }
                _sb.Append('\n');
                break;
            }

            case InterpolationInline interpolation:
                _sb.Append(interpolation.LiteralText);
                break;

            case DateLiteralInline date:
                _sb.Append(date.Text);
                break;

            case LineBreakInline:
                _sb.Append('\n');
                break;

            case FootnoteInline footnote:
                _sb.Append('[').Append(footnote.Number).Append(']');
                break;
        }
    }

    private static string InlineMarkdown(IEnumerable<PrtInline> inlines)
    {
        var renderer = new MarkdownRenderer(new StringBuilder());
        renderer.AppendInlines(inlines);
        return renderer._sb.ToString();
    }

    /// <summary>转义 Markdown 特殊字符，保证内容不被误解析。</summary>
    private static string EscapeText(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\':
                case '`':
                case '*':
                case '_':
                case '[':
                case ']':
                case '<':
                case '>':
                    sb.Append('\\').Append(c);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string JoinNumber(string? number, string? title)
        => string.IsNullOrEmpty(number) ? title ?? string.Empty : $"{number} {title}".TrimEnd();
}
