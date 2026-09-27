using System.Text;
using Prt.Core.Rendering;
using Prt.Core.Syntax;

namespace Prt.Core.Export;

/// <summary>
/// HTML 导出渲染器（《PRT 标准》13：HTML 为唯一全程支持全部特性的目标）。
/// 样式全部来自主题（第 12 章 / 附录 C），不出现任何文档内联样式声明。
/// </summary>
public sealed class HtmlRenderer
{
    /// <summary>渲染为完整 HTML 文档。</summary>
    public static string Render(PrtDocument document)
    {
        var themeBlocks = PrtTreeThemeBlocks(document.Root);
        var theme = PrtTheme.Resolve(document.Metadata.Theme, themeBlocks, out var resolvedThemeName);

        var sb = new StringBuilder();
        var renderer = new HtmlRenderer(sb, document, theme);

        sb.Append("<!DOCTYPE html>\n<html lang=\"")
            .Append(Escape(document.Metadata.Language ?? "zh-CN"))
            .Append("\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<title>").Append(Escape(document.Metadata.Title ?? "PRT 文档")).Append("</title>\n")
            .Append("<meta name=\"generator\" content=\"PRT EXT renderer / spec ")
            .Append(Escape(PrtCapabilities.SpecificationVersion)).Append("\">\n")
            .Append("<meta name=\"prt-theme\" content=\"").Append(Escape(resolvedThemeName)).Append("\">\n")
            .Append("<style>\n").Append(BuildStyle(theme)).Append("</style>\n</head>\n<body>\n")
            .Append("<article class=\"prt-document prt-theme-").Append(Escape(resolvedThemeName)).Append("\">\n");

        renderer.WriteBlocks(document.Root.Children);
        renderer.WriteFootnoteArea(document);

        sb.Append("</article>\n</body>\n</html>\n");
        return sb.ToString();
    }

    private static IEnumerable<ThemeBlock> PrtTreeThemeBlocks(PrtBlock root)
    {
        foreach (var block in Structure.PrtTree.WalkBlocks(root))
        {
            if (block is ThemeBlock theme)
            {
                yield return theme;
            }
        }
    }

    private readonly StringBuilder _sb;
    private readonly PrtDocument _document;
    private readonly PrtTheme _theme;

    private HtmlRenderer(StringBuilder sb, PrtDocument document, PrtTheme theme)
    {
        _sb = sb;
        _document = document;
        _theme = theme;
    }

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
                _sb.Append("<h").Append(heading.Level).Append(" class=\"prt-heading\">")
                    .Append(NumberPrefix(heading.AssignedNumber))
                    .Append(Escape(heading.PlainText))
                    .Append("</h").Append(heading.Level).Append(">\n");
                break;

            case ParagraphBlock paragraph:
                _sb.Append("<p>");
                AppendInlines(paragraph.Inlines);
                _sb.Append("</p>\n");
                break;

            case ListBlock list:
                WriteList(list);
                break;

            case QuoteBlock quote:
                _sb.Append("<blockquote>\n");
                WriteBlocks(quote.Children);
                _sb.Append("</blockquote>\n");
                break;

            case CodeBlock code:
                _sb.Append("<pre class=\"prt-code\"><code");
                if (!string.IsNullOrEmpty(code.Language))
                {
                    _sb.Append(" class=\"language-").Append(Escape(code.Language)).Append('"');
                }
                _sb.Append('>').Append(Escape(code.Code)).Append("</code></pre>\n");
                break;

            case ThematicBreakBlock:
                _sb.Append("<hr>\n");
                break;

            case PipeTableBlock pipe:
                WriteTable(pipe.Table, null, null);
                break;

            case TableBlock table:
                WriteTable(table.Table, table.Caption, table.AssignedNumber);
                break;

            case SemanticBlock semantic:
                WriteSemantic(semantic);
                break;

            case LayoutBlock layout:
                WriteLayout(layout);
                break;

            case SectionBlock section:
                WriteSection(section);
                break;

            case SummaryBlock summary:
                _sb.Append("<div class=\"prt-summary\">\n");
                WriteBlocks(summary.Children);
                _sb.Append("</div>\n");
                break;

            case TextStyleBlock text:
                _sb.Append("<div class=\"prt-text\" style=\"")
                    .Append(text.Color.HasValue ? $"color:{ColorPalette.ToHex(text.Color.Value)};" : string.Empty)
                    .Append(text.Size.HasValue ? $"font-size:{ColorPalette.FontSizeEm(text.Size.Value)};" : string.Empty)
                    .Append(text.Align.HasValue ? $"text-align:{AlignmentName(text.Align.Value)};" : string.Empty)
                    .Append(text.Bold ? "font-weight:bold;" : string.Empty)
                    .Append(text.Italic ? "font-style:italic;" : string.Empty)
                    .Append(text.Strike ? "text-decoration:line-through;" : string.Empty)
                    .Append("\">\n");
                WriteBlocks(text.Children);
                _sb.Append("</div>\n");
                break;

            case AlignBlock align:
                _sb.Append("<div class=\"prt-align\" style=\"text-align:")
                    .Append(AlignmentName(align.Align)).Append(";\">\n");
                WriteBlocks(align.Children);
                _sb.Append("</div>\n");
                break;

            case FigureBlock figure:
                _sb.Append("<figure class=\"prt-figure\"");
                if (!string.IsNullOrEmpty(figure.Id))
                {
                    _sb.Append(" id=\"").Append(Escape(figure.Id)).Append('"');
                }
                _sb.Append(">\n");
                _sb.Append("<img src=\"").Append(Escape(figure.Source ?? string.Empty)).Append("\" alt=\"")
                    .Append(Escape(figure.Alt ?? figure.Caption ?? string.Empty)).Append("\">\n");
                _sb.Append("<figcaption>图").Append(Escape(figure.AssignedNumber ?? string.Empty));
                if (!string.IsNullOrEmpty(figure.Caption))
                {
                    _sb.Append(": ").Append(Escape(figure.Caption));
                }
                _sb.Append("</figcaption>\n</figure>\n");
                WriteBlocks(figure.Children);
                break;

            // 公式：无 LaTeX 渲染环境时按第 13 章降级为代码块（标准 9.4）。
            case EquationBlock equation:
                _sb.Append("<div class=\"prt-equation\"");
                if (!string.IsNullOrEmpty(equation.Id))
                {
                    _sb.Append(" id=\"").Append(Escape(equation.Id)).Append('"');
                }
                _sb.Append(">\n<span class=\"prt-equation-number\">式(")
                    .Append(Escape(equation.AssignedNumber ?? string.Empty)).Append(")</span>\n")
                    .Append("<pre class=\"prt-code\"><code>").Append(Escape(equation.Source))
                    .Append("</code></pre>\n</div>\n");
                break;

            // 【COMP 不支持】计算块按第 13 章降级：输出源码并标注说明，不执行任何求值。
            case ComputationBlock computation:
                _sb.Append("<div class=\"prt-computation\">\n")
                    .Append("<p class=\"prt-computation-note\">受限计算（COMP）不受本渲染器支持，以下为源文件原文。</p>\n")
                    .Append("<pre class=\"prt-code\"><code>").Append(Escape(computation.Source))
                    .Append("</code></pre>\n</div>\n");
                break;

            case UnknownBlock unknown:
                _sb.Append("<pre class=\"prt-unknown\">").Append(Escape(unknown.Body)).Append("</pre>\n");
                break;

            case MetaBlock:
            case RefsBlock:
            case ThemeBlock:
                break;
        }
    }

    private void WriteSemantic(SemanticBlock semantic)
    {
        var name = SemanticNaming.CssName(semantic.Kind);
        _sb.Append("<div class=\"prt-callout prt-callout-").Append(name).Append("\">\n");
        _sb.Append("<p class=\"prt-callout-title\">").Append(Escape(SemanticNaming.Label(semantic.Kind)));
        if (!string.IsNullOrEmpty(semantic.Title))
        {
            _sb.Append(" · ").Append(Escape(semantic.Title));
        }
        _sb.Append("</p>\n");
        WriteBlocks(semantic.Children);
        _sb.Append("</div>\n");
    }

    private void WriteSection(SectionBlock section)
    {
        var level = Math.Clamp(section.NumberLevel, 1, 6);
        _sb.Append("<section class=\"prt-section\"");
        if (!string.IsNullOrEmpty(section.Id))
        {
            _sb.Append(" id=\"").Append(Escape(section.Id)).Append('"');
        }
        _sb.Append(">\n<h").Append(level).Append(" class=\"prt-section-title\">")
            .Append(NumberPrefix(section.AssignedNumber))
            .Append(Escape(section.Title ?? string.Empty))
            .Append("</h").Append(level).Append(">\n");
        WriteBlocks(section.Children);
        _sb.Append("</section>\n");
    }

    private void WriteLayout(LayoutBlock layout)
    {
        switch (layout.Kind)
        {
            case LayoutKind.Columns:
                _sb.Append("<div class=\"prt-columns\" style=\"--prt-cols:")
                    .Append(Math.Max(1, layout.Columns)).Append(";\">\n");
                foreach (var column in layout.ColumnItems)
                {
                    _sb.Append("<div class=\"prt-column\">\n");
                    WriteBlocks(column.Children);
                    _sb.Append("</div>\n");
                }
                _sb.Append("</div>\n");
                break;

            case LayoutKind.Collapse:
                _sb.Append("<details class=\"prt-collapse\"")
                    .Append(layout.DefaultOpen ? " open" : string.Empty).Append(">\n<summary>")
                    .Append(Escape(layout.Title ?? "详情")).Append("</summary>\n");
                WriteBlocks(layout.Children);
                _sb.Append("</details>\n");
                break;

            case LayoutKind.Tabs:
            {
                var group = Guid.NewGuid().ToString("N")[..8];
                _sb.Append("<div class=\"prt-tabs\">\n");
                for (var i = 0; i < layout.TabItems.Count; i++)
                {
                    _sb.Append("<input type=\"radio\" name=\"prt-tab-").Append(group).Append("\" id=\"prt-tab-")
                        .Append(group).Append('-').Append(i).Append('"')
                        .Append(i == 0 ? " checked" : string.Empty).Append(">\n")
                        .Append("<label for=\"prt-tab-").Append(group).Append('-').Append(i).Append("\">")
                        .Append(Escape(layout.TabItems[i].Name ?? $"标签 {i + 1}")).Append("</label>\n");
                }
                for (var i = 0; i < layout.TabItems.Count; i++)
                {
                    _sb.Append("<div class=\"prt-tab-panel\">\n");
                    WriteBlocks(layout.TabItems[i].Children);
                    _sb.Append("</div>\n");
                }
                _sb.Append("</div>\n");
                break;
            }
        }
    }

    private void WriteList(ListBlock list)
    {
        var tag = list.Ordered ? "ol" : "ul";
        _sb.Append('<').Append(tag);
        if (list.Ordered && list.StartNumber > 1)
        {
            _sb.Append(" start=\"").Append(list.StartNumber).Append('"');
        }
        _sb.Append(">\n");
        foreach (var item in list.Items)
        {
            _sb.Append("<li");
            if (item.IsTask)
            {
                _sb.Append(" class=\"prt-task\"");
            }
            _sb.Append('>');
            if (item.IsTask)
            {
                _sb.Append("<input type=\"checkbox\" disabled")
                    .Append(item.IsChecked ? " checked" : string.Empty).Append("> ");
            }
            AppendInlines(item.Inlines);
            WriteBlocks(item.Children);
            _sb.Append("</li>\n");
        }
        _sb.Append("</").Append(tag).Append(">\n");
    }

    private void WriteTable(TableModel table, string? caption, string? number)
    {
        _sb.Append("<table class=\"prt-table\">\n");
        if (!string.IsNullOrEmpty(caption))
        {
            _sb.Append("<caption>表").Append(Escape(number ?? string.Empty)).Append(": ")
                .Append(Escape(caption)).Append("</caption>\n");
        }

        if (table.HeaderRows.Count > 0)
        {
            _sb.Append("<thead>\n");
            foreach (var row in table.HeaderRows)
            {
                WriteTableRow(row, table, "th");
            }
            _sb.Append("</thead>\n");
        }

        if (table.BodyRows.Count > 0)
        {
            _sb.Append("<tbody>\n");
            foreach (var row in table.BodyRows)
            {
                WriteTableRow(row, table, "td");
            }
            _sb.Append("</tbody>\n");
        }

        if (table.FooterRows.Count > 0)
        {
            _sb.Append("<tfoot>\n");
            foreach (var row in table.FooterRows)
            {
                WriteTableRow(row, table, "td");
            }
            _sb.Append("</tfoot>\n");
        }

        _sb.Append("</table>\n");
    }

    private void WriteTableRow(TableRowData row, TableModel table, string tag)
    {
        _sb.Append("<tr>");
        for (var i = 0; i < row.Cells.Count; i++)
        {
            var cell = row.Cells[i];
            if (cell.IsCovered)
            {
                // 被合并覆盖的位置不输出（由主格的 colspan/rowspan 占位）。
                continue;
            }
            _sb.Append('<').Append(tag);
            if (cell.ColumnSpan > 1)
            {
                _sb.Append(" colspan=\"").Append(cell.ColumnSpan).Append('"');
            }
            if (cell.RowSpan > 1)
            {
                _sb.Append(" rowspan=\"").Append(cell.RowSpan).Append('"');
            }
            var alignment = i < table.Alignments.Count ? table.Alignments[i] : null;
            if (alignment.HasValue)
            {
                _sb.Append(" style=\"text-align:").Append(alignment.Value switch
                {
                    TableAlignment.Left => "left",
                    TableAlignment.Right => "right",
                    _ => "center",
                }).Append('"');
            }
            _sb.Append('>');
            AppendInlines(cell.Inlines);
            _sb.Append("</").Append(tag).Append('>');
        }
        _sb.Append("</tr>\n");
    }

    private void WriteFootnoteArea(PrtDocument document)
    {
        if (document.Structure.Footnotes.Count == 0)
        {
            return;
        }
        _sb.Append("<section class=\"prt-footnotes\">\n<hr>\n<ol>\n");
        foreach (var footnote in document.Structure.Footnotes)
        {
            _sb.Append("<li id=\"prt-fn-").Append(footnote.Number).Append("\">")
                .Append(Escape(footnote.Content)).Append("</li>\n");
        }
        _sb.Append("</ol>\n</section>\n");
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
            case TextInline t: _sb.Append(Escape(t.Text)); break;
            case LiteralInline l: _sb.Append(Escape(l.Text)); break;
            case CodeInline c: _sb.Append("<code class=\"prt-inline-code\">").Append(Escape(c.Text)).Append("</code>"); break;

            case EmphasisInline e:
                var open = new StringBuilder();
                var close = new StringBuilder();
                if (e.Italic) { open.Append("<em>"); close.Insert(0, "</em>"); }
                if (e.Bold) { open.Append("<strong>"); close.Insert(0, "</strong>"); }
                _sb.Append(open);
                AppendInlines(e.Children);
                _sb.Append(close);
                break;

            case StrikeInline s:
                _sb.Append("<del>");
                AppendInlines(s.Children);
                _sb.Append("</del>");
                break;

            // 高亮仅加背景色，不得改变文字颜色或以加粗代替（标准 7.2）。
            case HighlightInline h:
                _sb.Append("<mark class=\"prt-highlight\">");
                AppendInlines(h.Children);
                _sb.Append("</mark>");
                break;

            case UnderlineInline u:
                _sb.Append("<u>");
                AppendInlines(u.Children);
                _sb.Append("</u>");
                break;

            case SuperscriptInline sup:
                _sb.Append("<sup>");
                AppendInlines(sup.Children);
                _sb.Append("</sup>");
                break;

            case SubscriptInline sub:
                _sb.Append("<sub>");
                AppendInlines(sub.Children);
                _sb.Append("</sub>");
                break;

            case ColorInline col:
                _sb.Append("<span style=\"color:").Append(ColorPalette.ToHex(col.Color)).Append("\">");
                AppendInlines(col.Children);
                _sb.Append("</span>");
                break;

            case BackgroundColorInline bg:
                _sb.Append("<span style=\"background-color:").Append(ColorPalette.ToHex(bg.Color)).Append("\">");
                AppendInlines(bg.Children);
                _sb.Append("</span>");
                break;

            case SizeInline size:
                _sb.Append("<span style=\"font-size:").Append(ColorPalette.FontSizeEm(size.Size)).Append("\">");
                AppendInlines(size.Children);
                _sb.Append("</span>");
                break;

            case LinkInline link:
                if (link.IsImage)
                {
                    _sb.Append("<img class=\"prt-inline-image\" src=\"").Append(Escape(link.Url))
                        .Append("\" alt=\"").Append(Escape(link.Alt)).Append("\">");
                }
                else
                {
                    _sb.Append("<a href=\"").Append(Escape(link.Url)).Append("\">");
                    AppendInlines(link.Children);
                    _sb.Append("</a>");
                }
                break;

            case KbdInline kbd:
                _sb.Append("<kbd class=\"prt-kbd\">").Append(Escape(kbd.Keys)).Append("</kbd>");
                break;

            case RefInline reference:
            {
                var text = reference.ResolvedText ?? reference.DisplayText ?? reference.TargetId;
                var anchor = ReferenceAnchor(reference);
                _sb.Append("<a class=\"prt-ref\" href=\"#").Append(Escape(anchor)).Append("\">")
                    .Append(Escape(text)).Append("</a>");
                break;
            }

            case TermLinkInline term:
                _sb.Append("<span class=\"prt-term\"");
                if (!string.IsNullOrEmpty(term.Definition))
                {
                    _sb.Append(" title=\"").Append(Escape(term.Definition)).Append('"');
                }
                _sb.Append('>').Append(Escape(term.ResolvedText ?? term.DisplayText ?? term.Key)).Append("</span>");
                break;

            case ContentsInline contents:
                WriteToc(contents);
                break;

            // 【COMP 不支持】插值按第 13 章降级为原文。
            case InterpolationInline interpolation:
                _sb.Append("<code class=\"prt-interpolation\">").Append(Escape(interpolation.LiteralText)).Append("</code>");
                break;

            case DateLiteralInline date:
                _sb.Append(Escape(date.Text));
                break;

            case LineBreakInline:
                _sb.Append('\n');
                break;

            case FootnoteInline footnote:
                _sb.Append("<sup class=\"prt-footnote-ref\"><a href=\"#prt-fn-")
                    .Append(footnote.Number).Append("\">").Append(footnote.Number).Append("</a></sup>");
                break;
        }
    }

    private void WriteToc(ContentsInline contents)
    {
        var entries = TocHelper.Filter(_document.Structure, contents.Depth, contents.Mode);
        if (entries.Count == 0)
        {
            return;
        }
        _sb.Append("<nav class=\"prt-toc\">\n<ul>\n");
        var currentLevel = entries[0].Level;
        var opened = 1;
        var first = true;
        foreach (var entry in entries)
        {
            if (!first)
            {
                while (entry.Level > currentLevel)
                {
                    _sb.Append("<ul>\n");
                    opened++;
                    currentLevel++;
                }
                while (entry.Level < currentLevel)
                {
                    _sb.Append("</li>\n</ul>\n");
                    opened--;
                    currentLevel--;
                }
                if (entry.Level == currentLevel)
                {
                    _sb.Append("</li>\n");
                }
            }
            first = false;
            _sb.Append("<li>");
            if (!string.IsNullOrEmpty(entry.AnchorId))
            {
                _sb.Append("<a href=\"#").Append(Escape(entry.AnchorId)).Append("\">");
            }
            _sb.Append(Escape(JoinNumber(entry.Number, entry.Title)));
            if (!string.IsNullOrEmpty(entry.AnchorId))
            {
                _sb.Append("</a>");
            }
        }
        for (var i = 0; i < opened; i++)
        {
            _sb.Append("</li>\n</ul>\n");
        }
        _sb.Append("</nav>\n");
    }

    private static string ReferenceAnchor(RefInline reference)
    {
        if (reference.ResolvedTarget is SectionBlock section && !string.IsNullOrEmpty(section.Id))
        {
            return section.Id!;
        }
        if (reference.ResolvedTarget is TableBlock table && !string.IsNullOrEmpty(table.Id))
        {
            return table.Id!;
        }
        if (reference.ResolvedTarget is FigureBlock figure && !string.IsNullOrEmpty(figure.Id))
        {
            return figure.Id!;
        }
        if (reference.ResolvedTarget is EquationBlock equation && !string.IsNullOrEmpty(equation.Id))
        {
            return equation.Id!;
        }
        return reference.TargetId;
    }

    private static string NumberPrefix(string? number)
        => string.IsNullOrEmpty(number)
            ? string.Empty
            : $"<span class=\"prt-number\">{Escape(number)}</span> ";

    private static string AlignmentName(TextAlignment alignment) => alignment switch
    {
        TextAlignment.Center => "center",
        TextAlignment.Right => "right",
        TextAlignment.Justify => "justify",
        _ => "left",
    };

    private static string JoinNumber(string? number, string? title)
        => string.IsNullOrEmpty(number) ? title ?? string.Empty : $"{number} {title}".TrimEnd();

    private static string Escape(string text)
        => text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

    /// <summary>由主题变量拼装 CSS（附录 C：主题只覆盖预定义变量，不允许任意 CSS）。</summary>
    private static string BuildStyle(PrtTheme theme)
    {
        var sb = new StringBuilder();
        sb.Append(":root {\n");
        foreach (var pair in theme.Variables.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.Append("  ").Append(pair.Key).Append(": ").Append(pair.Value).Append(";\n");
        }
        sb.Append("}\n");
        sb.Append(BaseCss);
        return sb.ToString();
    }

    private const string BaseCss = """
        * { box-sizing: border-box; }
        html, body { margin: 0; padding: 0; }
        body {
          background: var(--page-bg);
          color: var(--page-text);
          font-family: var(--font-sans-serif);
          line-height: 1.75;
        }
        .prt-document { max-width: 860px; margin: 0 auto; padding: 48px 32px 96px; }
        h1, h2, h3, h4, h5, h6 { color: var(--heading-color); line-height: 1.35; margin: 1.6em 0 .6em; }
        .prt-number { color: var(--page-text-muted); font-weight: 500; margin-right: .35em; }
        p { margin: .7em 0; }
        a { color: var(--link-color); }
        hr { border: none; border-top: 1px solid var(--page-border); margin: 1.6em 0; }
        blockquote {
          margin: .8em 0; padding: .2em 1em; color: var(--page-text-secondary);
          border-left: 3px solid var(--page-border);
        }
        code, pre, kbd { font-family: var(--font-mono); }
        pre.prt-code {
          background: var(--code-bg); color: var(--code-text);
          border: 1px solid var(--page-border); border-radius: var(--radius-medium);
          padding: 12px 14px; overflow: auto;
        }
        .prt-inline-code {
          background: var(--code-bg); color: var(--code-text);
          padding: .1em .35em; border-radius: var(--radius-small);
        }
        .prt-highlight { background: var(--highlight-bg); color: inherit; }
        .prt-kbd {
          background: var(--kbd-bg); color: var(--kbd-text);
          border: 1px solid var(--kbd-border); border-radius: var(--radius-small);
          padding: 0 .4em; font-size: .9em;
        }
        .prt-term {
          border-bottom: 1px dashed var(--page-text-muted); cursor: help;
        }
        .prt-ref { text-decoration: none; }
        .prt-footnote-ref { font-size: .75em; }
        .prt-footnotes { color: var(--page-text-secondary); font-size: .9em; }
        .prt-summary {
          background: var(--page-surface); border: 1px solid var(--page-border);
          border-radius: var(--radius-medium); padding: 12px 18px;
          color: var(--page-text-secondary); font-size: .96em;
        }
        .prt-callout {
          border: 1px solid var(--page-border);
          border-left-width: 4px;
          border-radius: var(--radius-medium);
          padding: 12px 16px; margin: 1em 0;
        }
        .prt-callout-title { font-weight: 600; margin: 0 0 .35em; }
        .prt-callout-note { background: var(--callout-note-bg); border-left-color: var(--callout-note-border); }
        .prt-callout-warning { background: var(--callout-warning-bg); border-left-color: var(--callout-warning-border); }
        .prt-callout-danger { background: var(--callout-danger-bg); border-left-color: var(--callout-danger-border); }
        .prt-callout-attention { background: var(--callout-attention-bg); border-left-color: var(--callout-attention-border); }
        .prt-callout-example { background: var(--callout-example-bg); border-left-color: var(--callout-example-border); }
        .prt-callout-definition { background: var(--callout-definition-bg); border-left-color: var(--callout-definition-border); }
        .prt-callout-quote { background: var(--callout-quote-bg); border-left-color: var(--callout-quote-border); }
        .prt-callout-comment { background: var(--callout-comment-bg); border-left-color: var(--callout-comment-border); color: var(--page-text-muted); }
        .prt-callout-info { background: var(--callout-info-bg); border-left-color: var(--callout-info-border); }
        .prt-callout-tip { background: var(--callout-tip-bg); border-left-color: var(--callout-tip-border); }
        .prt-callout-result { background: var(--callout-result-bg); border-left-color: var(--callout-result-border); }
        .prt-callout-task { background: var(--callout-task-bg); border-left-color: var(--callout-task-border); }
        table.prt-table { border-collapse: collapse; width: 100%; margin: 1em 0; }
        table.prt-table caption { caption-side: top; text-align: left; color: var(--page-text-secondary); margin-bottom: .4em; }
        table.prt-table th, table.prt-table td {
          border: 1px solid var(--page-border); padding: 6px 10px; vertical-align: top;
        }
        table.prt-table thead th { background: var(--page-surface); font-weight: 600; }
        table.prt-table tfoot td { background: var(--page-surface-muted); font-weight: 600; }
        .prt-columns { display: flex; gap: 16px; flex-wrap: wrap; }
        .prt-column { flex: 1 1 0; min-width: 180px; }
        .prt-collapse { border: 1px solid var(--page-border); border-radius: var(--radius-medium); padding: .4em .8em; margin: 1em 0; }
        .prt-collapse > summary { cursor: pointer; font-weight: 600; }
        .prt-tabs { border: 1px solid var(--page-border); border-radius: var(--radius-medium); padding: .6em .8em; margin: 1em 0; }
        .prt-tabs > input { display: none; }
        .prt-tabs > label {
          display: inline-block; padding: .25em .9em; margin-right: .4em; cursor: pointer;
          border: 1px solid var(--page-border); border-radius: var(--radius-small);
        }
        .prt-tabs > div.prt-tab-panel { display: none; border-top: 1px solid var(--page-border); margin-top: .6em; padding-top: .6em; }
        .prt-tabs > input:nth-of-type(1):checked ~ div.prt-tab-panel:nth-of-type(1) { display: block; }
        .prt-tabs > input:nth-of-type(2):checked ~ div.prt-tab-panel:nth-of-type(2) { display: block; }
        .prt-tabs > input:nth-of-type(3):checked ~ div.prt-tab-panel:nth-of-type(3) { display: block; }
        .prt-tabs > input:nth-of-type(4):checked ~ div.prt-tab-panel:nth-of-type(4) { display: block; }
        .prt-figure { margin: 1.2em 0; text-align: center; }
        .prt-figure img { max-width: 100%; }
        .prt-figure figcaption { color: var(--page-text-secondary); font-size: .9em; margin-top: .4em; }
        .prt-equation { margin: 1em 0; position: relative; }
        .prt-equation-number { float: right; color: var(--page-text-muted); }
        .prt-computation { border: 1px dashed var(--page-border); border-radius: var(--radius-medium); padding: .6em .8em; }
        .prt-computation-note { color: var(--page-text-muted); font-size: .9em; margin: 0 0 .4em; }
        .prt-toc { background: var(--page-surface); border: 1px solid var(--page-border); border-radius: var(--radius-medium); padding: .6em 1.2em; }
        .prt-toc ul { list-style: none; padding-left: 1.1em; margin: .2em 0; }
        .prt-toc a { text-decoration: none; }
        .prt-task { list-style: none; margin-left: -1.2em; }
        """;
}
