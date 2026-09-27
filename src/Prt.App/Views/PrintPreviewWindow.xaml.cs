using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Prt.App.Rendering;
using Prt.App.Services;
using Prt.App.Theming;

namespace Prt.App.Views;

/// <summary>
/// 打印预览与打印：分页预览（可翻页）、页眉页脚、纸张 / 方向 / 边距，确认后交系统打印对话框。
/// <para>
/// 打印用**独立构建**的流文档（见 <see cref="DocumentView.BuildPrintableDocument"/>），
/// 因此改纸张、切黑白都不会影响屏幕上正在编辑的预览。默认按 print 主题黑白输出（省墨）。
/// </para>
/// </summary>
public partial class PrintPreviewWindow : Window
{
    /// <summary>A4 / A5 / Letter 的纸张尺寸（设备无关单位，1/96 英寸）。</summary>
    private static readonly Size[] PaperSizes =
    [
        new(793.70, 1122.52),  // A4
        new(559.37, 793.70),   // A5
        new(816.00, 1056.00),  // Letter
    ];

    /// <summary>页边距三档（设备无关单位）。</summary>
    private static readonly Thickness[] Margins =
    [
        new(38),   // 窄
        new(62),   // 常规
        new(90),   // 宽
    ];

    private readonly string _documentTitle;

    private readonly Func<bool, FlowDocument?> _buildDocument;

    private DocumentPaginator? _paginator;

    private int _pageIndex;

    /// <summary>初始化期间抑制重建（避免控件初始值触发一轮无意义的分页）。</summary>
    private bool _initializing;

    public PrintPreviewWindow(string documentTitle, Func<bool, FlowDocument?> buildDocument)
    {
        InitializeComponent();

        _documentTitle = string.IsNullOrWhiteSpace(documentTitle) ? Localizer.T("app.name") : documentTitle;
        _buildDocument = buildDocument;

        _initializing = true;
        PaperBox.SelectedIndex = 0;
        OrientationBox.SelectedIndex = 0;
        MarginBox.SelectedIndex = 1;
        _initializing = false;

        Loaded += (_, _) => Rebuild();
    }

    /// <summary>是否真的完成了打印（供调用方给出状态栏提示）。</summary>
    public bool Printed { get; private set; }

    /// <summary>页数（自检用）。</summary>
    public int PageCount => _paginator?.PageCount ?? 0;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
    }

    // ─────────────────────────────── 分页 ───────────────────────────────

    /// <summary>按当前纸张 / 边距 / 黑白设置重建分页结果。</summary>
    public void Rebuild()
    {
        if (_initializing)
        {
            return;
        }

        var flow = _buildDocument(MonochromeBox.IsChecked == true);
        if (flow is null)
        {
            MessageText.Text = Localizer.T("print.missing");
            PageView.DocumentPaginator = null;
            _paginator = null;
            return;
        }

        var paper = PaperSizes[Math.Clamp(PaperBox.SelectedIndex, 0, PaperSizes.Length - 1)];
        var landscape = OrientationBox.SelectedIndex == 1;
        var pageSize = landscape ? new Size(paper.Height, paper.Width) : paper;
        var margin = Margins[Math.Clamp(MarginBox.SelectedIndex, 0, Margins.Length - 1)];

        // 纸张与边距交给 FlowDocument 自己排版：单列铺满、按页高折行。
        flow.PageWidth = pageSize.Width;
        flow.PageHeight = pageSize.Height;
        flow.PagePadding = margin;
        flow.ColumnWidth = double.PositiveInfinity;

        var inner = ((IDocumentPaginatorSource)flow).DocumentPaginator;
        inner.PageSize = pageSize;
        inner.ComputePageCount();

        var brush = (TryFindResource(AppPalette.ResourceKeys.Text) as Brush) ?? Brushes.Black;
        _paginator = new HeaderFooterPaginator(
            inner,
            _documentTitle,
            HeaderBox.IsChecked == true,
            FooterBox.IsChecked == true,
            brush,
            9d);

        _paginator.PageSize = pageSize;
        _paginator.ComputePageCount();

        PageView.DocumentPaginator = _paginator;
        _pageIndex = Math.Clamp(_pageIndex, 0, Math.Max(0, _paginator.PageCount - 1));
        UpdatePageIndicators();
        MessageText.Text = string.Empty;
    }

    private void UpdatePageIndicators()
    {
        var count = Math.Max(1, _paginator?.PageCount ?? 0);
        PageView.PageNumber = _pageIndex;
        PageText.Text = Localizer.T("print.page", _pageIndex + 1, count);
        PrevButton.IsEnabled = _paginator is not null && _pageIndex > 0;
        NextButton.IsEnabled = _paginator is not null && _pageIndex < count - 1;
    }

    // ─────────────────────────────── 事件 ───────────────────────────────

    // 与设置窗口同理：XAML 装载期可能先抛一次控件事件，那时其它控件还没连上，
    // 重建分页会踩到空字段——装载完成前一律不响应。
    private void OnOptionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized)
        {
            Rebuild();
        }
    }

    private void OnOptionClick(object sender, RoutedEventArgs e)
    {
        if (IsInitialized)
        {
            Rebuild();
        }
    }

    private void OnPrevPageClick(object sender, RoutedEventArgs e)
    {
        if (_paginator is null || _pageIndex <= 0)
        {
            return;
        }

        _pageIndex--;
        UpdatePageIndicators();
    }

    private void OnNextPageClick(object sender, RoutedEventArgs e)
    {
        if (_paginator is null || _pageIndex >= _paginator.PageCount - 1)
        {
            return;
        }

        _pageIndex++;
        UpdatePageIndicators();
    }

    private void OnPrintClick(object sender, RoutedEventArgs e)
    {
        if (_paginator is null)
        {
            MessageText.Text = Localizer.T("print.missing");
            return;
        }

        var paper = PaperSizes[Math.Clamp(PaperBox.SelectedIndex, 0, PaperSizes.Length - 1)];
        var landscape = OrientationBox.SelectedIndex == 1;

        var dialog = new PrintDialog();
        try
        {
            dialog.PrintTicket.PageMediaSize = new System.Printing.PageMediaSize(paper.Width, paper.Height);
            dialog.PrintTicket.PageOrientation = landscape
                ? System.Printing.PageOrientation.Landscape
                : System.Printing.PageOrientation.Portrait;
        }
        catch
        {
            // 打印机不支持显式纸张时交给系统对话框自己决定。
        }

        // 注：WPF 的 PrintDialog 没有 owner 重载，系统对话框会挂在当前活动窗口上。
        if (dialog.ShowDialog() != true)
        {
            MessageText.Text = Localizer.T("status.print.cancelled");
            return;
        }

        try
        {
            dialog.PrintDocument(_paginator, _documentTitle);
            Printed = true;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageText.Text = ex.Message;
        }
    }
}
