using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Perisc.Safety;
using Prt.App.Models;
using Prt.App.Rendering;
using Prt.App.Services;
using Prt.Core;
using Prt.Core.Diagnostics;
using Prt.Core.Export;
using Prt.Core.Rendering;
using Prt.Core.Structure;
using Prt.Core.Syntax;

namespace Prt.App.Views;

/// <summary>
/// 单个文档的编辑视图：左侧 PRT 源码编辑，右侧原生 <see cref="FlowDocument"/> 实时预览。
/// <para>
/// 界面全部为 WPF 原生控件，不含任何 WebView / 浏览器内核。
/// 本类只负责「一个文档」的编辑与呈现；跨文档的标签页、菜单、导出等由 <see cref="MainWindow"/> 统筹。
/// </para>
/// </summary>
public partial class DocumentView : UserControl
{
    private const double BaseEditorFontSize = 14d;

    /// <summary>常规文档的编辑防抖间隔（毫秒）。</summary>
    private const int BaseDebounceMilliseconds = 280;

    /// <summary>文本超过该字符数视为大文档：解析与渲染开销显著，防抖间隔自适应拉长，避免连续输入时反复全量重排。</summary>
    private const int LargeTextCharacterThreshold = 60_000;

    /// <summary>大文档的编辑防抖间隔（毫秒）。</summary>
    private const int LargeTextDebounceMilliseconds = 900;

    /// <summary>
    /// 历史阈值：曾用于区分「同步构建 / 渐进管线」两条路径（按工作量：块数＋目录条目＋脚注）。
    /// 渲染路径统一为「游离全量构建＋一次性挂载」后仅保留作参考值，供自检构造超大文档时对照。
    /// </summary>
    internal const int ProgressiveBlockThreshold = 300;

    /// <summary>
    /// 惰性渲染的顶层块数阈值：达到即走「首屏惰性 + 后台分批补载」，否则一次成型。
    /// 取 150：既有参考阈值（<see cref="ProgressiveBlockThreshold"/> = 300）偏保守，
    /// 中等偏大的文档（每块渲染开销可观）用一次整篇布局仍会卡；降到 150 后此类文档也受惰性保护。
    /// </summary>
    private const int LazyRenderTopLevelThreshold = 150;

    private readonly DispatcherTimer _debounce;
    private readonly PrtOptions _options = new();

    /// <summary>
    /// 锚点映射（M2a 虚拟预览）：可锚定块 → 所在列表项索引。
    /// 虚拟化下块元素按需实例化，锚点表因此落在**模型侧**（与视口无关、一次构建全量就绪）；
    /// 「块 → 已实现元素」的查询走 <see cref="Preview"/> 的容器映射。
    /// </summary>
    private IReadOnlyDictionary<PrtBlock, int> _anchorIndex = new Dictionary<PrtBlock, int>();

    /// <summary>上次随编辑器光标联动到的源块（避免无谓滚动）。</summary>
    private PrtBlock? _caretSyncBlock;

    /// <summary>程序化滚动进行中（ScrollIntoView / ScrollToLine 会触发 ScrollChanged，需抑制比例同步）。</summary>
    private bool _programmaticScroll;

    /// <summary>联动高亮的容器及其原始背景（迁移高亮前先还原）。</summary>
    private System.Windows.Controls.ContentControl? _highlightedContainer;
    private Brush? _highlightOriginalBrush;

    /// <summary>预览中最后一次点击的源块（右键「插入段落 / 删除块」以此为基准）。</summary>
    private PrtBlock? _lastPreviewBlock;

    /// <summary>是否已完成过一次渲染：首次渲染提前到 AddTab（加载完成再显示标签），Loaded 时不再重复。</summary>
    private bool _renderedOnce;

    /// <summary>编辑器正在分帧加载大文本（此间 Editor 内容可能尚不完整，源码真源以 <see cref="Tab.Text"/> 为准）。</summary>
    private bool _editorFilling;

    /// <summary>分帧加载进行中令牌；再次载入时取消上一轮未完成的分帧。</summary>
    private CancellationTokenSource? _fillCts;

    private ScrollViewer? _editorScroll;
    private ScrollViewer? _previewScroll;
    private double _zoom = 1d;
    private bool _suppressChanged;
    private bool _syncingScroll;

    // 编辑器右键菜单（构造一次，弹出时按选区状态刷新可用性）
    private ContextMenu? _contextMenu;
    private MenuItem? _insertMenu;
    private MenuItem? _wrapHintItem;
    private MenuItem? _cutItem;
    private MenuItem? _copyItem;
    private MenuItem? _pasteItem;

    public DocumentView(DocumentTab tab)
    {
        Tab = tab;
        InitializeComponent();

        _debounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(280),
        };
        _debounce.Tick += OnDebounceTick;

        // 注意：编辑器文本不在构造时同步赋值（见 LoadText——大文本改分帧填充）。
        // 构造完成、标签加入视觉树前由 MainWindow 调 view.LoadText(model.Text) 填内容，
        // 避免把超大源码一次性塞进 TextBox 让打开瞬间卡死。
        Editor.TextChanged += OnEditorTextChanged;
        Editor.SelectionChanged += OnEditorSelectionChanged;
        Editor.PreviewKeyDown += OnEditorPreviewKeyDown;

        // 虚拟预览：单击 → 定位源码；双击 → 就地编辑该块（滚动锁定式，见 DocumentView.PreviewEdit.cs）。
        Preview.MouseLeftButtonUp += OnPreviewMouseUp;
        Preview.MouseDoubleClick += OnPreviewDoubleClick;

        BuildPreviewContextMenu();

        BuildEditorContextMenu();

        Loaded += OnLoaded;
    }

    /// <summary>
    /// 载入源码并填充编辑器。小文档同步赋值；<b>大文档分帧填充</b>——不在构造时把超大字符串
    /// 一次性塞进 <see cref="Editor"/>（否则打开瞬间 UI 线程被「塞大文本 + 全量布局」卡死），
    /// 而是先让界面立即可用、源码在后台逐段呈现。解析始终基于 <see cref="Tab.Text"/>（源码真源），
    /// 与编辑器填充进度无关。
    /// </summary>
    public void LoadText(string text)
    {
        Tab.Text = text;
        Tab.IsDirty = false;
        _fillCts?.Cancel();
        if (IsInPlaceEditing)
        {
            CancelInPlaceEdit();   // 换文档 / 重载：编辑态作废，源码即新文档，不回写
        }

        var small = text.Length < LargeTextCharacterThreshold;
        _suppressChanged = true;
        try
        {
            Editor.Clear();
            if (small)
            {
                Editor.Text = text;
            }
        }
        finally
        {
            _suppressChanged = false;
        }

        _editorFilling = !small;
        if (small)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _fillCts = cts;
        _ = FillEditorChunkedAsync(text, cts.Token);
    }

    private async Task FillEditorChunkedAsync(string text, CancellationToken cancellation)
    {
        const int chunk = 200_000;
        for (var i = 0; i < text.Length; i += chunk)
        {
            if (cancellation.IsCancellationRequested)
            {
                break;
            }

            var segment = text.Substring(i, Math.Min(chunk, text.Length - i));
            _suppressChanged = true;
            try
            {
                Editor.AppendText(segment);
            }
            finally
            {
                _suppressChanged = false;
            }

            // 让出 UI，使打开时的首帧与交互不被大文本加载吞没。
            await Dispatcher.Yield(DispatcherPriority.Background);
        }

        _editorFilling = false;
        _fillCts = null;
    }

    /// <summary>该视图承载的文档数据。</summary>
    public DocumentTab Tab { get; }

    /// <summary>解析结果（可能为 null，表示尚未解析）。</summary>
    public PrtDocument? Document { get; private set; }

    /// <summary>结构级状态变化（解析完成、脏标记变化、主题/缩放变化）。</summary>
    public event EventHandler? StateChanged;

    /// <summary>新一轮解析开始时触发（主窗口据此把侧边栏大纲切到「计算中」占位）。</summary>
    public event EventHandler? ParseStarted;

    /// <summary>是否有解析在途（结构/标题结果尚未就绪）；解析完成或失败后复位。</summary>
    public bool IsParseInProgress { get; private set; }

    /// <summary>光标或选区变化。</summary>
    public event EventHandler? CaretChanged;

    /// <summary>需要显示在状态栏的瞬时消息。</summary>
    public event EventHandler<string>? StatusMessage;

    public ViewMode Mode { get; private set; } = ViewMode.Split;

    public bool WordWrap => Editor.TextWrapping == TextWrapping.Wrap;

    public double Zoom => _zoom;

    /// <summary>预览主题名；null 表示跟随文档 <c>meta.theme</c>。</summary>
    public string? PreviewThemeName { get; private set; }

    /// <summary>本次预览实际生效的主题名（含回退结果）。</summary>
    public string ResolvedPreviewTheme { get; private set; } = "default";

    /// <summary>严格模式显式覆盖；null 表示交由 <c>meta.strict</c> 决定。</summary>
    public bool? StrictOverride { get; private set; }

    public IReadOnlyList<Diagnostic> Diagnostics
    {
        get
        {
            var document = Document;
            return document is null ? Array.Empty<Diagnostic>() : document.SortedDiagnostics;
        }
    }

    public IReadOnlyList<TocEntry> Toc
    {
        get
        {
            var document = Document;
            return document is null ? Array.Empty<TocEntry>() : document.Structure.Toc;
        }
    }

    public bool HasErrors => Document?.HasErrors ?? false;

    public int ErrorCount => Document?.Diagnostics.ErrorCount ?? 0;

    public int WarningCount => Document?.Diagnostics.WarningCount ?? 0;

    public bool HasUnsupportedComputation => Document?.HasUnsupportedComputation ?? false;

    public string Text => _editorFilling ? Tab.Text : Editor.Text;

    public int CharacterCount => Tab.Text.Length;

    public (int Line, int Column) CaretPosition => TextNavigation.ToLineColumn(Editor.Text, Editor.CaretIndex);

    // ─────────────────────────────── 生命周期 ───────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachScrollSync();
        // 首次渲染已由 AddTab 同步完成（标签出现即内容完整，避免「先分栏/空预览再跳变」的中间帧）；
        // 这里仅在确实未渲染时补一次（例如视图被重新挂入视觉树）。
        if (!_renderedOnce)
        {
            Reparse();
        }

        ApplyViewMode(Mode);
        Editor.Focus();
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounce.Stop();
        Reparse();
    }

    private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressChanged)
        {
            return;
        }

        Tab.Text = Editor.Text;
        if (!Tab.IsDirty)
        {
            Tab.IsDirty = true;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        RestartDebounce();
    }

    private void OnEditorSelectionChanged(object sender, RoutedEventArgs e)
    {
        SyncPreviewToCaret();
        CaretChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RestartDebounce()
    {
        _debounce.Stop();
        // 大文档防抖自适应拉长：输入越密集、文档越大，越不值得为每次击键启动一轮全量重排。
        _debounce.Interval = Editor.Text.Length >= LargeTextCharacterThreshold
            ? TimeSpan.FromMilliseconds(LargeTextDebounceMilliseconds)
            : TimeSpan.FromMilliseconds(BaseDebounceMilliseconds);
        _debounce.Start();
    }

    // ─────────────────────────────── 编辑命令（供菜单转发） ───────────────────────────────

    public void Undo()
    {
        if (Editor.CanUndo)
        {
            Editor.Undo();
        }
    }

    public void Redo()
    {
        if (Editor.CanRedo)
        {
            Editor.Redo();
        }
    }

    // ───────────────────────── 剪贴板（经 SRT，3.8）─────────────────────────
    //
    // 原先这里是 Editor.Cut() / Copy() / Paste() 三个直通调用。它们是 TextBox 的控件命令，
    // 但内部读写的仍是系统剪贴板——按 3.8 的门槛口径，程序不得绕开 SRT 去碰平台能力。
    // 改成先由 SRT 申报并执行剪贴板读写，再自己改编辑器内容：
    // Editor 是 TextBox（不是 RichTextBox），其 Copy/Paste 本来就只走纯文本，
    // 因此换成 SRT + SelectedText 赋值不丢任何格式语义。

    /// <summary>剪切：先把选中文本写进剪贴板（DEV-05），成功后才删掉选中的部分。</summary>
    public void Cut()
    {
        var selection = Editor.SelectedText;
        if (string.IsNullOrEmpty(selection))
        {
            return;
        }
        if (!SafeRuntime.Device.SetClipboardText(selection, "剪切所选内容到剪贴板").Executed)
        {
            // 未获许可就什么都不做：不得"剪掉了但没进剪贴板"，那是不可逆的数据丢失（3.8 ③）。
            return;
        }
        Editor.SelectedText = string.Empty;
    }

    /// <summary>复制：把选中文本写进剪贴板（DEV-05）。</summary>
    public void Copy()
    {
        var selection = Editor.SelectedText;
        if (string.IsNullOrEmpty(selection))
        {
            return;
        }
        SafeRuntime.Device.SetClipboardText(selection, "复制所选内容到剪贴板");
    }

    /// <summary>
    /// 粘贴：先从剪贴板读文本（DEV-04），成功且确有文本时才写进编辑器。
    /// <para>
    /// <c>null</c> 表示剪贴板里没有文本格式——此时直接返回，不能拿空值去覆盖选区，
    /// 否则「粘贴一段图片/文件」会变成「删掉你选中的文字」。
    /// </para>
    /// </summary>
    public void Paste()
    {
        var read = SafeRuntime.Device.GetClipboardText("粘贴剪贴板文本");
        if (!read.Executed || read.Value is null)
        {
            return;
        }
        Editor.SelectedText = read.Value;
    }

    public void SelectAllText()
    {
        Editor.Focus();
        Editor.SelectAll();
    }

}
