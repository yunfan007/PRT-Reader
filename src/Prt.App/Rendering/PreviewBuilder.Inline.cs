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

// 行内（PreviewBuilder 的 partial；拆分依据见《工程改进方案》§4.8 / D-06）。
internal sealed partial class PreviewBuilder
{
    // ─────────────────────────────── 行内 ───────────────────────────────

    // internal：VirtualPreviewBuilder（虚拟预览的逐块渲染器）复用同一套行内渲染规则，
    // 保证「打印/导出的 FlowDocument」与「虚拟预览的块 FE」在行内格式上单一实现（D-06 单一职责）。
    internal void AppendInlines(InlineCollection target, IEnumerable<PrtInline> inlines)
    {
        foreach (var inline in inlines)
        {
            AppendInline(target, inline);
        }
    }

    private void AppendInline(InlineCollection target, PrtInline inline)
    {
        switch (inline)
        {
            case TextInline text:
                target.Add(new Run(text.Text));
                break;

            case LiteralInline literal:
                target.Add(new Run(literal.Text));
                break;

            case CodeInline code:
                target.Add(new Run(code.Text)
                {
                    FontFamily = _style.MonoFamily,
                    FontSize = _style.BodyFontSize - 1.5,
                    Background = _style.CodeBackground,
                    Foreground = _style.CodeText,
                });
                break;

            case EmphasisInline emphasis:
            {
                var span = new Span();
                if (emphasis.Bold)
                {
                    span.FontWeight = FontWeights.Bold;
                }
                if (emphasis.Italic)
                {
                    span.FontStyle = FontStyles.Italic;
                }
                AppendInlines(span.Inlines, emphasis.Children);
                target.Add(span);
                break;
            }

            case StrikeInline strike:
            {
                var span = new Span { TextDecorations = TextDecorations.Strikethrough };
                AppendInlines(span.Inlines, strike.Children);
                target.Add(span);
                break;
            }

            case HighlightInline highlight:
            {
                var span = new Span { Background = _style.HighlightBackground };
                AppendInlines(span.Inlines, highlight.Children);
                target.Add(span);
                break;
            }

            case UnderlineInline underline:
            {
                var span = new Span { TextDecorations = TextDecorations.Underline };
                AppendInlines(span.Inlines, underline.Children);
                target.Add(span);
                break;
            }

            case SuperscriptInline superscript:
            {
                var span = new Span
                {
                    BaselineAlignment = BaselineAlignment.Superscript,
                    FontSize = _style.BodyFontSize - 3,
                };
                AppendInlines(span.Inlines, superscript.Children);
                target.Add(span);
                break;
            }

            case SubscriptInline subscript:
            {
                var span = new Span
                {
                    BaselineAlignment = BaselineAlignment.Subscript,
                    FontSize = _style.BodyFontSize - 3,
                };
                AppendInlines(span.Inlines, subscript.Children);
                target.Add(span);
                break;
            }

            case ColorInline color:
            {
                var span = new Span { Foreground = PreviewStyle.NamedBrush(color.Color) };
                AppendInlines(span.Inlines, color.Children);
                target.Add(span);
                break;
            }

            case BackgroundColorInline background:
            {
                var span = new Span { Background = PreviewStyle.NamedBrush(background.Color) };
                AppendInlines(span.Inlines, background.Children);
                target.Add(span);
                break;
            }

            case SizeInline size:
            {
                var span = new Span { FontSize = ColorPalette.FontSizePixel(size.Size) };
                AppendInlines(span.Inlines, size.Children);
                target.Add(span);
                break;
            }

            case KbdInline kbd:
                target.Add(new Run(kbd.Keys)
                {
                    FontFamily = _style.MonoFamily,
                    FontSize = _style.BodyFontSize - 2,
                    Background = _style.KbdBackground,
                    Foreground = _style.KbdText,
                });
                break;

            case FootnoteInline footnote:
                target.Add(new Run("[" + footnote.Number + "]")
                {
                    BaselineAlignment = BaselineAlignment.Superscript,
                    FontSize = _style.BodyFontSize - 3,
                    Foreground = _style.Accent,
                });
                break;

            case RefInline reference:
                AppendAnchorLink(target, reference.ResolvedText ?? reference.DisplayText ?? reference.TargetId, reference.ResolvedTarget, null);
                break;

            case TermLinkInline term:
                AppendAnchorLink(target, term.ResolvedText ?? term.DisplayText ?? term.Key, null, term.Definition);
                break;

            case LinkInline link:
                AppendLink(target, link);
                break;

            case ContentsInline contents:
                AppendContentsInline(target, contents);
                break;

            // 【COMP 不支持】插值按原文输出（标准第 13 章降级）。
            case InterpolationInline interpolation:
                target.Add(new Run(interpolation.LiteralText)
                {
                    FontFamily = _style.MonoFamily,
                    Foreground = _style.MutedText,
                    ToolTip = "COMP 受限计算不受支持，已按标准第 13 章降级为原文",
                });
                break;

            case DateLiteralInline date:
                target.Add(new Run(date.Text));
                break;

            case LineBreakInline:
                target.Add(new LineBreak());
                break;

            default:
                target.Add(new Run(inline.RawText ?? string.Empty));
                break;
        }
    }

    private void AppendAnchorLink(InlineCollection target, string text, IReferenceable? referenceTarget, string? tooltip)
    {
        var hyperlink = new Hyperlink(new Run(text))
        {
            Foreground = _style.Link,
            ToolTip = tooltip,
        };

        if (referenceTarget is PrtBlock block)
        {
            hyperlink.Tag = block;
            hyperlink.Click += OnAnchorLinkClick;
        }
        target.Add(hyperlink);
    }

    private void AppendLink(InlineCollection target, LinkInline link)
    {
        if (link.IsImage)
        {
            AppendImageInline(target, link);
            return;
        }

        var hyperlink = new Hyperlink { Foreground = _style.Link };
        AppendInlines(hyperlink.Inlines, link.Children);
        if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri))
        {
            hyperlink.NavigateUri = uri;
        }
        else
        {
            hyperlink.ToolTip = link.Url;
        }
        target.Add(hyperlink);
    }

    private void AppendImageInline(InlineCollection target, LinkInline link)
    {
        var image = TryLoadImage(link.Url);
        if (image is not null)
        {
            target.Add(new InlineUIContainer(new Image
            {
                Source = image,
                Stretch = Stretch.Uniform,
                MaxHeight = 320,
            }));
            return;
        }

        target.Add(new Run("[图片: " + (string.IsNullOrWhiteSpace(link.Alt) ? link.Url : link.Alt) + "]")
        {
            Foreground = _style.MutedText,
            Background = _style.Surface,
            ToolTip = link.Url,
        });
    }

    private void AppendContentsInline(InlineCollection target, ContentsInline contents)
    {
        foreach (var entry in FilterToc(contents.Depth, contents.Mode))
        {
            target.Add(new LineBreak());
            target.Add(new Run(new string(' ', Math.Max(0, entry.Level - 1) * 3) + "· " + JoinNumber(entry.Number, entry.Title))
            {
                Foreground = _style.SubtleText,
            });
        }
    }

    private void AppendTocList(BlockCollection target, ContentsInline contents)
    {
        var entries = FilterToc(contents.Depth, contents.Mode);
        if (entries.Count == 0)
        {
            target.Add(new Paragraph(new Run("（目录：当前无已编号章节）"))
            {
                FontStyle = FontStyles.Italic,
                Foreground = _style.MutedText,
                Margin = new Thickness(0, 0, 0, 12),
            });
            return;
        }

        var list = new List
        {
            MarkerStyle = TextMarkerStyle.None,
            Margin = new Thickness(12, 0, 0, 14),
            Padding = new Thickness(0),
        };

        foreach (var entry in entries)
        {
            list.ListItems.Add(CreateTocListItem(entry));
        }

        target.Add(list);
    }

    private List<TocEntry> FilterToc(int? depth, string mode)
    {
        var result = new List<TocEntry>();
        if (Structure is null)
        {
            return result;
        }

        foreach (var entry in Structure.Toc)
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

    private static string JoinNumber(string? number, string? title)
        => string.IsNullOrWhiteSpace(number) ? title ?? string.Empty : number + " " + title;
}
