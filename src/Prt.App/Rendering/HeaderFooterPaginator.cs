using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Prt.App.Rendering;

/// <summary>
/// 分页装饰器：给每一页加上页眉（文档标题）与页脚（页码）。
/// <para>
/// WPF 的 <see cref="FlowDocument"/> 自身没有"每页重复的页眉 / 页脚"概念，通行做法是在
/// <see cref="DocumentPaginator"/> 外面套一层：取到某页的 Visual 之后，重新排进
/// 「页眉 / 正文 / 页脚」三行再交出去。
/// </para>
/// <para>
/// 装饰过程整体包了异常兜底——任何一步失败都退回原始页，绝不因为"想加页码"而打不出文档。
/// </para>
/// </summary>
internal sealed class HeaderFooterPaginator : DocumentPaginator
{
    private readonly DocumentPaginator _inner;

    private readonly string _header;

    private readonly bool _withHeader;

    private readonly bool _withFooter;

    private readonly Brush _foreground;

    private readonly double _fontSize;

    public HeaderFooterPaginator(
        DocumentPaginator inner,
        string header,
        bool withHeader,
        bool withFooter,
        Brush foreground,
        double fontSize)
    {
        _inner = inner;
        _header = header;
        _withHeader = withHeader;
        _withFooter = withFooter;
        _foreground = foreground;
        _fontSize = fontSize;
    }

    public override bool IsPageCountValid => _inner.IsPageCountValid;

    public override int PageCount => _inner.PageCount;

    public override Size PageSize
    {
        get => _inner.PageSize;
        set => _inner.PageSize = value;
    }

    public override IDocumentPaginatorSource? Source => _inner.Source;

    public override DocumentPage GetPage(int pageNumber)
    {
        var page = _inner.GetPage(pageNumber);
        if (ReferenceEquals(page, DocumentPage.Missing) || (!_withHeader && !_withFooter))
        {
            return page;
        }

        try
        {
            var size = _inner.PageSize;

            var root = new Grid
            {
                Width = size.Width,
                Height = size.Height,
                Background = Brushes.Transparent,
            };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            if (_withHeader && _header.Length > 0)
            {
                var header = new TextBlock
                {
                    Text = _header,
                    FontSize = _fontSize,
                    Foreground = _foreground,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 0, 0, 6),
                };
                Grid.SetRow(header, 0);
                root.Children.Add(header);
            }

            // 正文：把该页的 Visual 画进中间一行。
            var body = new Rectangle
            {
                Fill = new VisualBrush(page.Visual)
                {
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top,
                },
            };
            Grid.SetRow(body, 1);
            root.Children.Add(body);

            if (_withFooter)
            {
                var footer = new TextBlock
                {
                    Text = $"{pageNumber + 1} / {Math.Max(1, _inner.PageCount)}",
                    FontSize = _fontSize,
                    Foreground = _foreground,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 6, 0, 0),
                };
                Grid.SetRow(footer, 2);
                root.Children.Add(footer);
            }

            root.Measure(size);
            root.Arrange(new Rect(size));
            root.UpdateLayout();

            return new DocumentPage(root, size, new Rect(size), new Rect(size));
        }
        catch
        {
            // 装饰失败就用原始页，宁可没有页码也不能打不出来。
            return page;
        }
    }
}
