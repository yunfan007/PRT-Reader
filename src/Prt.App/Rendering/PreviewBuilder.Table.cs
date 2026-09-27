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

// 表格（PreviewBuilder 的 partial；拆分依据见《工程改进方案》§4.8 / D-06）。
internal sealed partial class PreviewBuilder
{
    // ─────────────────────────────── 表格 ───────────────────────────────

    private void AppendTable(BlockCollection target, TableModel model, string? caption, string? number, PrtBlock? source)
    {
        var grid = BuildTableGrid(model);

        if (!string.IsNullOrEmpty(caption))
        {
            var captionParagraph = new Paragraph
            {
                TextAlignment = WpfTextAlignment.Center,
                FontSize = 12.5,
                Foreground = _style.SubtleText,
                Margin = new Thickness(0, 8, 0, 4),
            };
            captionParagraph.Inlines.Add(new Run((string.IsNullOrEmpty(number) ? string.Empty : "表" + number + ": ") + caption));
            target.Add(captionParagraph);
        }

        var host = new BlockUIContainer(grid) { Margin = new Thickness(0, 4, 0, 12) };
        if (source is not null)
        {
            RegisterAnchor(source, host);
        }
        target.Add(host);
    }

    /// <summary>
    /// 以 <see cref="Grid"/> 呈现表格：WPF 原生 Table 不支持行跨（RowSpan），
    /// 而 PRT 完整表格要求跨行与跨列并存，故统一采用 Grid + 占位矩阵实现。
    /// </summary>
    private Grid BuildTableGrid(TableModel model)
    {
        var columnCount = Math.Max(1, model.ColumnCount);
        var rows = new List<TableRowData>();
        rows.AddRange(model.HeaderRows);
        rows.AddRange(model.BodyRows);
        rows.AddRange(model.FooterRows);

        var grid = new Grid { Background = _style.PageBackground, SnapsToDevicePixels = true };
        for (var c = 0; c < columnCount; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 56 });
        }
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var occupied = new bool[rows.Count, columnCount];

        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            for (var c = 0; c < columnCount && c < row.Cells.Count; c++)
            {
                if (occupied[r, c])
                {
                    continue;
                }

                var cell = row.Cells[c];
                if (cell.IsCovered)
                {
                    continue;
                }

                var columnSpan = Math.Clamp(cell.ColumnSpan, 1, columnCount - c);
                var rowSpan = Math.Clamp(cell.RowSpan, 1, rows.Count - r);

                for (var rr = r; rr < r + rowSpan; rr++)
                {
                    for (var cc = c; cc < c + columnSpan && cc < columnCount; cc++)
                    {
                        occupied[rr, cc] = true;
                    }
                }

                var host = CreateCellHost(cell, model, c, row.Section);
                Grid.SetRow(host, r);
                Grid.SetColumn(host, c);
                Grid.SetRowSpan(host, rowSpan);
                Grid.SetColumnSpan(host, columnSpan);
                grid.Children.Add(host);
            }
        }

        return grid;
    }

    private Border CreateCellHost(TableCellData cell, TableModel model, int columnIndex, TableRowSection section)
    {
        var isHeader = section == TableRowSection.Header;
        var isFooter = section == TableRowSection.Footer;

        var host = new Border
        {
            BorderBrush = _style.Border,
            BorderThickness = new Thickness(0, 0, 1, 1),
            Padding = new Thickness(8, 5, 8, 5),
            Background = isHeader || isFooter ? _style.Surface : _style.PageBackground,
        };

        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = _style.Text,
            FontWeight = isHeader ? FontWeights.SemiBold : FontWeights.Normal,
        };

        var alignment = columnIndex < model.Alignments.Count ? model.Alignments[columnIndex] : null;
        text.TextAlignment = alignment switch
        {
            TableAlignment.Center => WpfTextAlignment.Center,
            TableAlignment.Right => WpfTextAlignment.Right,
            _ => WpfTextAlignment.Left,
        };

        AppendInlines(text.Inlines, cell.Inlines);
        host.Child = text;
        return host;
    }
}
