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

// 语义 / 样式 / 布局（PreviewBuilder 的 partial；拆分依据见《工程改进方案》§4.8 / D-06）。
internal sealed partial class PreviewBuilder
{
    // ─────────────────────────────── 语义 / 样式 / 布局 ───────────────────────────────

    private void AppendSemantic(BlockCollection target, SemanticBlock block)
    {
        var border = _style.CalloutBorder(block.Kind);
        var section = new Section
        {
            Background = _style.CalloutBackground(block.Kind),
            BorderBrush = border,
            BorderThickness = new Thickness(4, 1, 1, 1),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 4, 0, 14),
        };

        var title = new Paragraph
        {
            FontWeight = FontWeights.SemiBold,
            Foreground = border,
            Margin = new Thickness(0, 0, 0, 4),
        };
        title.Inlines.Add(new Run(SemanticNaming.Label(block.Kind)));
        if (!string.IsNullOrWhiteSpace(block.Title))
        {
            title.Inlines.Add(new Run("：" + block.Title));
        }
        section.Blocks.Add(title);

        AppendBlocks(section.Blocks, block.Children);
        RegisterAnchor(block, section);
        target.Add(section);
    }

    private void AppendTextStyle(BlockCollection target, TextStyleBlock block)
    {
        var section = new Section { Margin = new Thickness(0, 2, 0, 10) };
        if (block.Color is { } color)
        {
            section.Foreground = PreviewStyle.NamedBrush(color);
        }
        if (block.Size is { } size)
        {
            section.FontSize = ColorPalette.FontSizePixel(size);
        }
        if (block.Align is { } align)
        {
            section.TextAlignment = ToWpfAlignment(align);
        }
        if (block.Bold)
        {
            section.FontWeight = FontWeights.Bold;
        }
        if (block.Italic)
        {
            section.FontStyle = FontStyles.Italic;
        }

        AppendBlocks(section.Blocks, block.Children);

        // FlowDocument 的块级元素（Block）不提供 TextDecorations，
        // 因此删除线在追加完成后包裹到子段落的行内层（仅改外观，内容不变）。
        if (block.Strike)
        {
            ApplyStrikeThrough(section.Blocks);
        }

        target.Add(section);
    }

    /// <summary>把删除线装饰下沉到块内的段落（含嵌套结构），仅影响呈现，不改变内容。</summary>
    private static void ApplyStrikeThrough(IEnumerable<Block> blocks)
    {
        foreach (var child in blocks)
        {
            switch (child)
            {
                case Paragraph paragraph:
                {
                    var inlines = paragraph.Inlines.ToList();
                    paragraph.Inlines.Clear();
                    var span = new Span { TextDecorations = TextDecorations.Strikethrough };
                    foreach (var inline in inlines)
                    {
                        span.Inlines.Add(inline);
                    }
                    paragraph.Inlines.Add(span);
                    break;
                }

                case Section nested:
                    ApplyStrikeThrough(nested.Blocks);
                    break;

                case List list:
                    foreach (var item in list.ListItems)
                    {
                        ApplyStrikeThrough(item.Blocks);
                    }
                    break;
            }
        }
    }

    private void AppendAlign(BlockCollection target, AlignBlock block)
    {
        var section = new Section
        {
            TextAlignment = ToWpfAlignment(block.Align),
            Margin = new Thickness(0, 2, 0, 10),
        };
        AppendBlocks(section.Blocks, block.Children);
        target.Add(section);
    }

    private static WpfTextAlignment ToWpfAlignment(PrtTextAlignment alignment) => alignment switch
    {
        PrtTextAlignment.Center => WpfTextAlignment.Center,
        PrtTextAlignment.Right => WpfTextAlignment.Right,
        PrtTextAlignment.Justify => WpfTextAlignment.Justify,
        _ => WpfTextAlignment.Left,
    };

    private void AppendLayout(BlockCollection target, LayoutBlock layout)
    {
        UIElement host = layout.Kind switch
        {
            LayoutKind.Columns => BuildColumnsHost(layout),
            LayoutKind.Collapse => BuildExpanderHost(layout),
            LayoutKind.Tabs => BuildTabsHost(layout),
            _ => new StackPanel(),
        };

        target.Add(new BlockUIContainer(host) { Margin = new Thickness(0, 6, 0, 14) });
    }

    private UIElement BuildColumnsHost(LayoutBlock layout)
    {
        var items = layout.ColumnItems.Cast<PrtBlock>().ToList();
        var count = Math.Max(1, items.Count > 0 ? items.Count : layout.Columns);

        var grid = new Grid();
        for (var i = 0; i < count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var i = 0; i < count; i++)
        {
            var content = i < items.Count ? BlocksOf(items[i]) : Enumerable.Empty<PrtBlock>();
            var column = new Border
            {
                BorderBrush = _style.Border,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(i == 0 ? 0 : 4, 0, i == count - 1 ? 0 : 4, 0),
                Child = BuildEmbeddedHost(content),
            };
            Grid.SetColumn(column, i);
            grid.Children.Add(column);
        }

        return grid;
    }

    private UIElement BuildExpanderHost(LayoutBlock layout)
    {
        var header = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(layout.Title) ? "折叠内容" : layout.Title,
            FontWeight = FontWeights.SemiBold,
            Foreground = _style.Text,
        };

        return new Expander
        {
            Header = header,
            IsExpanded = layout.DefaultOpen,
            Content = BuildEmbeddedHost(layout.Children),
            Background = _style.Surface,
            BorderBrush = _style.Border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6, 10, 6),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
    }

    private UIElement BuildTabsHost(LayoutBlock layout)
    {
        var tabControl = new TabControl
        {
            Background = _style.PageBackground,
            BorderBrush = _style.Border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
        };

        foreach (var tab in layout.TabItems)
        {
            tabControl.Items.Add(new TabItem
            {
                Header = string.IsNullOrWhiteSpace(tab.Name) ? "标签" : tab.Name,
                Content = BuildEmbeddedHost(tab.Children),
            });
        }

        return tabControl;
    }

    private UIElement BuildEmbeddedHost(IEnumerable<PrtBlock> blocks)
    {
        var flow = CreateFlowDocument();
        flow.PagePadding = new Thickness(0);
        flow.LineHeight = _style.BodyFontSize * 1.5;
        AppendBlocks(flow.Blocks, blocks);

        return new FlowDocumentScrollViewer
        {
            Document = flow,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsToolBarVisible = false,
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
        };
    }

    private static IEnumerable<PrtBlock> BlocksOf(PrtBlock block) => block switch
    {
        ColumnBlock column => column.Children,
        TabBlock tab => tab.Children,
        _ => new[] { block },
    };
}
