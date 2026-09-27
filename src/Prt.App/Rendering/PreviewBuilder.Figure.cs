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

// 图 / 公式 / COMP / 未知块（PreviewBuilder 的 partial；拆分依据见《工程改进方案》§4.8 / D-06）。
internal sealed partial class PreviewBuilder
{
    // ─────────────────────────────── 图 / 公式 / COMP / 未知块 ───────────────────────────────

    private void AppendFigure(BlockCollection target, FigureBlock figure)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var source = TryLoadImage(figure.Source);
        if (source is not null)
        {
            stack.Children.Add(new Image { Source = source, Stretch = Stretch.Uniform, MaxHeight = 460 });
        }
        else
        {
            stack.Children.Add(new Border
            {
                Background = _style.Surface,
                BorderBrush = _style.Border,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(20, 14, 20, 14),
                Child = new TextBlock
                {
                    Text = "[图片] " + (string.IsNullOrWhiteSpace(figure.Alt) ? figure.Source ?? string.Empty : figure.Alt),
                    Foreground = _style.MutedText,
                    TextWrapping = TextWrapping.Wrap,
                },
            });
        }

        if (!string.IsNullOrEmpty(figure.Caption))
        {
            stack.Children.Add(new TextBlock
            {
                Text = "图" + figure.AssignedNumber + ": " + figure.Caption,
                FontSize = 12.5,
                Foreground = _style.SubtleText,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            });
        }

        var host = new BlockUIContainer(stack) { Margin = new Thickness(0, 6, 0, 14) };
        RegisterAnchor(figure, host);
        target.Add(host);

        // 块体内承载图片语法的那个段落已在上方呈现，避免图片重复出现。
        foreach (var child in figure.Children)
        {
            if (IsImageOnlyParagraph(child))
            {
                continue;
            }
            AppendBlock(target, child);
        }
    }

    /// <summary>判断段落是否仅承载图片语法（用于插图块去重）。</summary>
    private static bool IsImageOnlyParagraph(PrtBlock block)
    {
        if (block is not ParagraphBlock paragraph)
        {
            return false;
        }

        var hasImage = false;
        foreach (var inline in paragraph.Inlines)
        {
            switch (inline)
            {
                case LinkInline { IsImage: true }:
                    hasImage = true;
                    break;
                case TextInline text when string.IsNullOrWhiteSpace(text.Text):
                    break;
                case LineBreakInline:
                    break;
                default:
                    return false;
            }
        }

        return hasImage;
    }

    private void AppendEquation(BlockCollection target, EquationBlock equation)
    {
        var paragraph = new Paragraph(new Run(equation.Source))
        {
            FontFamily = _style.MonoFamily,
            FontSize = _style.BodyFontSize,
            TextAlignment = WpfTextAlignment.Center,
            Background = _style.Surface,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 6, 0, 6),
            Foreground = _style.Text,
        };
        RegisterAnchor(equation, paragraph);
        target.Add(paragraph);

        if (!string.IsNullOrEmpty(equation.Caption))
        {
            target.Add(new Paragraph(new Run("式(" + equation.AssignedNumber + "): " + equation.Caption))
            {
                TextAlignment = WpfTextAlignment.Center,
                FontSize = 12.5,
                Foreground = _style.SubtleText,
                Margin = new Thickness(0, 0, 0, 14),
            });
        }
    }

    private void AppendComputation(BlockCollection target, ComputationBlock block)
    {
        var section = new Section
        {
            Background = _style.Surface,
            BorderBrush = _style.Border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 4, 0, 14),
        };

        section.Blocks.Add(new Paragraph(new Run("[COMP 不受支持] 该计算块已按标准第 13 章降级为源码，未执行任何求值。"))
        {
            FontSize = 11.5,
            Foreground = _style.MutedText,
            Margin = new Thickness(0, 0, 0, 4),
        });

        section.Blocks.Add(new Paragraph(new Run(block.Source))
        {
            FontFamily = _style.MonoFamily,
            FontSize = 12.5,
            Foreground = _style.CodeText,
            Margin = new Thickness(0),
        });

        target.Add(section);
    }

    private void AppendUnknown(BlockCollection target, UnknownBlock block)
    {
        target.Add(new Paragraph(new Run(string.IsNullOrEmpty(block.Body) ? "[[未知块 " + block.TypeName + "]]" : block.Body))
        {
            FontFamily = _style.MonoFamily,
            FontSize = 12.5,
            Foreground = _style.MutedText,
            Background = _style.Surface,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 4, 0, 12),
        });
    }
}
