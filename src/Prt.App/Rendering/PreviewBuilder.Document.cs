using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Prt.Core;
using Prt.Core.Rendering;
using Prt.Core.Structure;
using Prt.Core.Syntax;
using PrtTextAlignment = Prt.Core.TextAlignment;
using WpfTextAlignment = System.Windows.TextAlignment;

namespace Prt.App.Rendering;

// 文档与块（PreviewBuilder 的 partial；拆分依据见《工程改进方案》§4.8 / D-06）。
internal sealed partial class PreviewBuilder
{
    // ─────────────────────────────── 文档与块 ───────────────────────────────

    private FlowDocument CreateFlowDocument() => new()
    {
        Background = _style.PageBackground,
        Foreground = _style.Text,
        FontFamily = _style.SansFamily,
        FontSize = _style.BodyFontSize,
        LineHeight = _style.BodyFontSize * 1.65,
        PagePadding = new Thickness(28, 22, 28, 48),
        // 单列布局：避免窗口较宽时自动分栏（与所见即所得编辑的直觉一致）。
        ColumnWidth = double.PositiveInfinity,
        TextAlignment = WpfTextAlignment.Left,
    };

    private void AppendDocumentHeader(BlockCollection target, PrtMetadata metadata)
    {
        var hasTitle = !string.IsNullOrWhiteSpace(metadata.Title);
        var hasSubtitle = !string.IsNullOrWhiteSpace(metadata.Subtitle);
        var metaLine = BuildMetaLine(metadata);
        if (!hasTitle && !hasSubtitle && metaLine.Length == 0)
        {
            return;
        }

        if (hasTitle)
        {
            target.Add(new Paragraph(new Run(metadata.Title))
            {
                FontSize = _style.HeadingSize(1) + 4,
                FontWeight = FontWeights.Bold,
                Foreground = _style.Heading,
                Margin = new Thickness(0, 0, 0, 4),
            });
        }

        if (hasSubtitle)
        {
            target.Add(new Paragraph(new Run(metadata.Subtitle))
            {
                FontSize = _style.HeadingSize(3),
                Foreground = _style.SubtleText,
                Margin = new Thickness(0, 0, 0, 6),
            });
        }

        if (metaLine.Length > 0)
        {
            target.Add(new Paragraph(new Run(metaLine))
            {
                FontSize = 12.5,
                Foreground = _style.MutedText,
                Margin = new Thickness(0, 0, 0, 14),
            });
        }
    }

    private static string BuildMetaLine(PrtMetadata metadata)
    {
        var parts = new List<string>();
        if (metadata.Authors.Count > 0)
        {
            parts.Add(string.Join("、", metadata.Authors));
        }
        if (!string.IsNullOrWhiteSpace(metadata.Date))
        {
            parts.Add(metadata.Date!);
        }
        if (!string.IsNullOrWhiteSpace(metadata.Version))
        {
            parts.Add("版本 " + metadata.Version);
        }
        if (metadata.Tags.Count > 0)
        {
            parts.Add("标签：" + string.Join(" / ", metadata.Tags));
        }
        return string.Join(" · ", parts);
    }

    private void AppendBlocks(BlockCollection target, IEnumerable<PrtBlock> blocks)
    {
        foreach (var block in blocks)
        {
            // 锚点判定用 LastBlock 引用比较（O(1)）：TextTree 集合的 Count 是 O(n)，
            // 逐块查询会退化为 O(n²)——这是大文档构建卡顿的主因（6000 块实测约 6 秒，
            // 其中块构造本身仅约 0.5 秒）。
            var lastBefore = target.LastBlock;
            try
            {
                AppendBlock(target, block);
            }
            catch (Exception ex)
            {
                // 单个块渲染异常不应导致整篇预览失效：就地降级并显式标注，便于定位。
                target.Add(new Paragraph(new Run($"[渲染异常] {block.GetType().Name}：{ex.Message}"))
                {
                    Foreground = _style.MutedText,
                    FontStyle = FontStyles.Italic,
                    FontSize = 12,
                    Margin = new Thickness(0, 2, 0, 8),
                });
            }

            // 通用锚点：每个顶层块的「源块 → 预览元素」映射，供视觉化编辑的双向定位使用
            // （目录 / 交叉引用等场景已在各 Append* 内注册更精确的锚点，此处仅补齐其余块）。
            if (target.LastBlock is FrameworkContentElement element && !ReferenceEquals(element, lastBefore))
            {
                RegisterAnchor(block, element);
            }
        }
    }

    private void AppendBlock(BlockCollection target, PrtBlock block)
    {
        switch (block)
        {
            case ParagraphBlock paragraph:
                if (paragraph.Inlines.Count == 1 && paragraph.Inlines[0] is ContentsInline contents)
                {
                    AppendTocList(target, contents);
                }
                else
                {
                    target.Add(CreateParagraph(paragraph.Inlines));
                }
                break;

            case HeadingBlock heading:
                RegisterAnchor(heading, AppendHeading(target, heading.Level, heading.AssignedNumber, heading.Inlines));
                break;

            case SectionBlock section:
                RegisterAnchor(section, AppendSection(target, section));
                break;

            case SummaryBlock summary:
                AppendSummary(target, summary);
                break;

            case ListBlock list:
                target.Add(CreateList(list));
                break;

            case QuoteBlock quote:
                AppendQuote(target, quote);
                break;

            case CodeBlock code:
                target.Add(CreateCodeBlock(code.Code, code.Language));
                break;

            case ThematicBreakBlock:
                target.Add(CreateDivider());
                break;

            case PipeTableBlock pipe:
                AppendTable(target, pipe.Table, null, null, null);
                break;

            case TableBlock table:
                AppendTable(target, table.Table, table.Caption, table.AssignedNumber, table);
                break;

            case SemanticBlock semantic:
                AppendSemantic(target, semantic);
                break;

            case LayoutBlock layout:
                AppendLayout(target, layout);
                break;

            case TextStyleBlock textStyle:
                AppendTextStyle(target, textStyle);
                break;

            case AlignBlock align:
                AppendAlign(target, align);
                break;

            case FigureBlock figure:
                AppendFigure(target, figure);
                break;

            case EquationBlock equation:
                AppendEquation(target, equation);
                break;

            // 【COMP 不支持】按第 13 章降级呈现源码。
            case ComputationBlock computation:
                AppendComputation(target, computation);
                break;

            case UnknownBlock unknown:
                AppendUnknown(target, unknown);
                break;

            // meta / refs / theme 属于「元信息块」，本身不产生正文内容。
            case MetaBlock:
            case RefsBlock:
            case ThemeBlock:
                break;
        }
    }

    private Paragraph CreateParagraph(IEnumerable<PrtInline> inlines)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
        AppendInlines(paragraph.Inlines, inlines);
        return paragraph;
    }

    private Paragraph AppendHeading(BlockCollection target, int level, string? number, IEnumerable<PrtInline> inlines)
    {
        var paragraph = new Paragraph
        {
            FontSize = _style.HeadingSize(level),
            FontWeight = FontWeights.SemiBold,
            Foreground = _style.Heading,
            Margin = new Thickness(0, 18, 0, 8),
        };

        if (!string.IsNullOrEmpty(number))
        {
            paragraph.Inlines.Add(new Run(number + " ") { Foreground = _style.SubtleText });
        }
        AppendInlines(paragraph.Inlines, inlines);

        if (level <= 1)
        {
            paragraph.BorderBrush = _style.Border;
            paragraph.BorderThickness = new Thickness(0, 0, 0, 1);
            paragraph.Padding = new Thickness(0, 0, 0, 6);
        }

        target.Add(paragraph);
        return paragraph;
    }

    private Paragraph AppendSection(BlockCollection target, SectionBlock section)
    {
        var level = Math.Clamp(section.NumberLevel, 1, 7);
        var heading = new Paragraph
        {
            FontSize = _style.HeadingSize(level),
            FontWeight = FontWeights.SemiBold,
            Foreground = _style.Heading,
            Margin = new Thickness(0, level <= 1 ? 20 : 14, 0, 8),
        };

        if (!string.IsNullOrEmpty(section.AssignedNumber))
        {
            heading.Inlines.Add(new Run(section.AssignedNumber + " ") { Foreground = _style.SubtleText });
        }
        if (!string.IsNullOrEmpty(section.Title))
        {
            heading.Inlines.Add(new Run(section.Title));
        }
        if (level <= 1)
        {
            heading.BorderBrush = _style.Border;
            heading.BorderThickness = new Thickness(0, 0, 0, 1);
            heading.Padding = new Thickness(0, 0, 0, 6);
        }
        target.Add(heading);

        if (section.Children.Count > 0)
        {
            var body = new Section { Margin = new Thickness(level > 1 ? 14 : 0, 0, 0, 0) };
            AppendBlocks(body.Blocks, section.Children);
            target.Add(body);
        }

        return heading;
    }

    private void AppendSummary(BlockCollection target, SummaryBlock summary)
    {
        var section = new Section
        {
            Background = _style.Surface,
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 4, 0, 14),
            FontStyle = FontStyles.Italic,
            FontSize = _style.BodyFontSize - 1,
        };
        AppendBlocks(section.Blocks, summary.Children);
        target.Add(section);
    }

    private List CreateList(ListBlock block)
    {
        var list = new List
        {
            MarkerStyle = block.Ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            MarkerOffset = 12,
            Margin = new Thickness(18, 0, 0, 10),
            Padding = new Thickness(0),
        };

        foreach (var item in block.Items)
        {
            var listItem = new ListItem();
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 3) };
            if (item.IsTask)
            {
                // 任务清单（EXT）：渲染为可交互的真实复选框；点击经回调写回源文本。
                var checkBox = new CheckBox
                {
                    IsChecked = item.IsChecked,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                };

                var taskLine = item.Line;
                checkBox.Checked += (_, _) => TaskToggleCallback?.Invoke(taskLine, true);
                checkBox.Unchecked += (_, _) => TaskToggleCallback?.Invoke(taskLine, false);

                paragraph.Inlines.Add(new InlineUIContainer(checkBox));
            }
            AppendInlines(paragraph.Inlines, item.Inlines);
            listItem.Blocks.Add(paragraph);
            AppendBlocks(listItem.Blocks, item.Children);
            list.ListItems.Add(listItem);
        }

        return list;
    }

    private void AppendQuote(BlockCollection target, QuoteBlock quote)
    {
        var section = new Section
        {
            BorderBrush = _style.Border,
            BorderThickness = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(14, 4, 0, 4),
            Margin = new Thickness(0, 4, 0, 12),
            Foreground = _style.SubtleText,
        };
        AppendBlocks(section.Blocks, quote.Children);
        target.Add(section);
    }

    private Section CreateCodeBlock(string code, string? language)
    {
        var section = new Section
        {
            Background = _style.CodeBackground,
            BorderBrush = _style.Border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 4, 0, 12),
        };

        if (!string.IsNullOrWhiteSpace(language))
        {
            section.Blocks.Add(new Paragraph(new Run(language))
            {
                FontFamily = _style.MonoFamily,
                FontSize = 11,
                Foreground = _style.MutedText,
                Margin = new Thickness(0, 0, 0, 4),
            });
        }

        section.Blocks.Add(new Paragraph(new Run(code))
        {
            FontFamily = _style.MonoFamily,
            FontSize = 13,
            Foreground = _style.CodeText,
            Margin = new Thickness(0),
        });

        return section;
    }

    private Block CreateDivider() => new BlockUIContainer(new Border
    {
        Height = 1,
        Background = _style.Border,
    })
    {
        Margin = new Thickness(0, 14, 0, 14),
    };

    private void AppendFootnoteArea(BlockCollection target, PrtDocument document)
    {
        var footnotes = document.Structure.Footnotes;
        if (footnotes.Count == 0)
        {
            return;
        }

        target.Add(new BlockUIContainer(new Border { Height = 1, Background = _style.Border })
        {
            Margin = new Thickness(0, 20, 0, 10),
        });

        target.Add(new Paragraph(new Run("脚注"))
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = _style.SubtleText,
            Margin = new Thickness(0, 0, 0, 6),
        });

        foreach (var footnote in footnotes)
        {
            var paragraph = new Paragraph
            {
                FontSize = 12.5,
                Foreground = _style.SubtleText,
                Margin = new Thickness(0, 0, 0, 4),
            };
            paragraph.Inlines.Add(new Run(footnote.Number + ". "));
            paragraph.Inlines.Add(new Run(footnote.Content));
            target.Add(paragraph);
        }
    }
}
