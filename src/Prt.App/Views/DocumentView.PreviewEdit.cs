using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Prt.App.Services;
using Prt.Core.Syntax;

namespace Prt.App.Views;

// 预览的块级操作与就地编辑（DocumentView 的 partial；D-06）。
// M2a 曾按《虚拟化改造方案》移除就地编辑；方案增补（2026-09-26，用户定案）把它请回来——
// 核心机制＝「编辑时暂时禁用滚动」：编辑态锁定预览滚动，被编辑的容器钉在视口内不被回收，
// 虚拟化与就地编辑因此可以并存。编辑内容是该块的**源码**（所见即所改，写回无损）；
// 提交走编辑器选区替换（保留撤销栈），Esc 取消，两条路都会立即重新解析渲染。
public partial class DocumentView : UserControl
{
    /// <summary>编辑中的源块（null = 不在编辑态）。</summary>
    private PrtBlock? _editBlock;

    /// <summary>编辑框（寄居在被编辑容器的 Content 上）。</summary>
    private TextBox? _editBox;

    /// <summary>被编辑的容器（提交 / 取消后由重新渲染重建内容）。</summary>
    private System.Windows.Controls.ContentControl? _editContainer;

    /// <summary>当前是否处于就地编辑态（供联动 / 渲染 / 自检判断）。</summary>
    internal bool IsInPlaceEditing => _editBlock is not null;

    /// <summary>编辑框当前文本（自检驱动用；正常交互直接在编辑框里打字）。</summary>
    internal string InPlaceEditText
    {
        get => _editBox?.Text ?? string.Empty;
        set
        {
            if (_editBox is not null)
            {
                _editBox.Text = value;
            }
        }
    }

    /// <summary>预览滚动是否已锁定（就地编辑期间应为 true）。</summary>
    internal bool IsPreviewScrollLocked => Preview.IsScrollLocked;

    /// <summary>
    /// 进入一个源块的就地编辑态：锁定预览滚动，把该块的源码放进原地编辑框。
    /// <para>
    /// 容器尚未被虚拟化实例化时先滚动过去再试；仍取不到（无布局 / 无效块）则返回 false。
    /// 解析在途时同样拒绝——渲染马上会重建列表，编辑态撑不过去。
    /// </para>
    /// </summary>
    internal bool BeginInPlaceEdit(PrtBlock block)
    {
        if (_editBlock is not null || Document is null || IsParseInProgress || block.Line <= 0)
        {
            return false;
        }

        if (!Preview.TryGetRealizedContainer(block, out var container))
        {
            if (_anchorIndex.TryGetValue(block, out var index))
            {
                Preview.ScrollToIndex(index);
                Preview.UpdateLayout();
            }

            if (!Preview.TryGetRealizedContainer(block, out container))
            {
                return false;
            }
        }

        var source = ReadBlockSource(block);
        var viewportHeight = _previewScroll?.ViewportHeight ?? 600;
        var box = new TextBox
        {
            Text = source,
            AcceptsReturn = true,
            AcceptsTab = true,
            FontFamily = Editor.FontFamily,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(200, viewportHeight - 48),
            Background = TryFindResource("App.PanelBg") as Brush ?? Brushes.White,
            BorderBrush = SyncHighlightBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6, 8, 6),
        };
        box.LostKeyboardFocus += (_, _) => CommitInPlaceEdit();
        box.KeyDown += OnEditBoxKeyDown;

        _editBlock = block;
        _editBox = box;
        _editContainer = container;
        container.Content = box;

        Preview.SetScrollLocked(true);
        box.Focus();
        box.CaretIndex = box.Text.Length;
        StatusMessage?.Invoke(this, "已进入就地编辑（预览滚动已锁定）：Ctrl+Enter 或点击外部提交，Esc 取消");
        return true;
    }

    /// <summary>提交就地编辑：把编辑框文本写回该块的源码行范围，随后立即重新解析渲染。</summary>
    internal void CommitInPlaceEdit()
    {
        if (_editBlock is null || _editBox is null)
        {
            return;
        }

        var block = _editBlock;
        var newText = _editBox.Text;

        // 编辑框显示的是「拍掉尾随换行」的源码；写回时把换行结构补回去，
        // 否则块会与下一块黏在同一行（边界是下一块行首，必须有换行分隔）。
        var (end, _) = BlockSourceEnd(block);
        var endsAtDocumentEnd = end >= Editor.Text.Length;
        if (!endsAtDocumentEnd && !newText.EndsWith('\n'))
        {
            newText += Editor.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        }

        EndInPlaceEditUi();

        var (start, _) = GetLineSpan(Editor.Text, Math.Max(1, block.Line));
        var (endIndex, _) = BlockSourceEnd(block);
        Editor.Select(start, endIndex - start);
        Editor.SelectedText = newText;   // 选区替换：保留撤销栈

        // 立即解析刷新（不等防抖）：就地编辑是离散动作，提交就该立刻看到结果；
        // 停掉防抖，避免 280ms 后再来一轮多余的解析。
        _debounce.Stop();
        Reparse();
        StatusMessage?.Invoke(this, "就地编辑已提交");
    }

    /// <summary>取消就地编辑：源码不动，恢复该块的渲染内容。</summary>
    internal void CancelInPlaceEdit()
    {
        if (_editBlock is null)
        {
            return;
        }

        EndInPlaceEditUi();
        Reparse();
        StatusMessage?.Invoke(this, "已取消就地编辑");
    }

    /// <summary>拆除编辑态的界面痕迹（滚动解锁、容器交还渲染），字段全部复位。</summary>
    private void EndInPlaceEditUi()
    {
        Preview.SetScrollLocked(false);
        if (_editContainer is not null)
        {
            _editContainer.Content = null;
        }

        _editBox = null;
        _editContainer = null;
        _editBlock = null;
    }

    /// <summary>编辑框内按键：Ctrl+Enter 提交；Esc 取消。</summary>
    private void OnEditBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelInPlaceEdit();
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            CommitInPlaceEdit();
        }
    }

    /// <summary>双击预览中的块 → 就地编辑（单击仍是「点击定位源码」，两者不冲突）。</summary>
    private void OnPreviewDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_editBlock is not null)
        {
            return;
        }

        if (Preview.TryGetBlockAt(e.OriginalSource as DependencyObject ?? Preview, out var block)
            && BeginInPlaceEdit(block))
        {
            e.Handled = true;
        }
    }

    /// <summary>取一个块的源码文本（起始行 → 下一块行首，尾随换行拍掉以便编辑）。</summary>
    private string ReadBlockSource(PrtBlock block)
    {
        var (start, _) = GetLineSpan(Editor.Text, Math.Max(1, block.Line));
        var (end, _) = BlockSourceEnd(block);
        if (start < 0 || end <= start)
        {
            return string.Empty;
        }

        return Editor.Text.Substring(start, end - start).TrimEnd('\r', '\n');
    }

    /// <summary>
    /// 预览右键菜单：定位源码 / 插入框 / 块级编辑 / 刷新预览。
    /// 就地编辑（滚动锁定式）期间弹出的菜单仍可用：点击任何菜单项都会让编辑框失焦 → 自动提交，行为自洽。
    /// </summary>
    private void BuildPreviewContextMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(CreateCommandItem("切换到源码栏对应位置", string.Empty, () =>
        {
            if (_lastPreviewBlock is { } block)
            {
                JumpEditorToBlock(block);
            }
        }));

        menu.Items.Add(CreateCommandItem("就地编辑此块（双击同效）", string.Empty, () =>
        {
            if (_lastPreviewBlock is { } block)
            {
                BeginInPlaceEdit(block);
            }
        }));

        // 插入块：骨架写入源码栏光标处（预览随之刷新）。
        var insert = new MenuItem { Header = "插入框（插入到源码栏）" };
        insert.Items.Add(BuildSnippetGroup(PrtSnippets.GroupBasic, "基础块（标准 8.2）"));
        insert.Items.Add(BuildSnippetGroup(PrtSnippets.GroupSemantic, "语义框（标准 8.3）"));
        insert.Items.Add(BuildSnippetGroup(PrtSnippets.GroupStructure, "结构块（标准 8.5）"));
        insert.Items.Add(BuildSnippetGroup(PrtSnippets.GroupCompute, "计算与指令（标准 11 章）"));
        menu.Items.Add(insert);

        // 块级编辑：在最后点击的块下方插入段落 / 删除该块（源码级操作）。
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateCommandItem("在此块下方插入段落", string.Empty, InsertParagraphAfterCurrentBlock));
        menu.Items.Add(CreateCommandItem("删除当前块", string.Empty, DeleteCurrentBlock));

        menu.Items.Add(new Separator());
        menu.Items.Add(CreateCommandItem("刷新预览", string.Empty, Reparse));

        Preview.ContextMenu = menu;
    }

    /// <summary>在最后点击的块下方插入一个空段落（源码级插入），并把光标落在新行。</summary>
    private void InsertParagraphAfterCurrentBlock()
    {
        var block = _lastPreviewBlock;
        if (block is null || block.Line <= 0)
        {
            StatusMessage?.Invoke(this, "请先在预览中点击要操作的位置。");
            return;
        }

        var ordered = _anchorIndex.Keys.Where(b => b.Line > 0).OrderBy(b => b.Line).ToList();
        var nextLine = int.MaxValue;
        foreach (var candidate in ordered)
        {
            if (candidate.Line > block.Line && candidate.Line < nextLine)
            {
                nextLine = candidate.Line;
            }
        }

        var insertAt = nextLine == int.MaxValue
            ? Editor.Text.Length
            : GetLineSpan(Editor.Text, nextLine).Start;

        var newline = Editor.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var text = "新段落" + newline + newline;

        Editor.Select(insertAt, 0);
        Editor.SelectedText = text;
        Editor.Select(insertAt, "新段落".Length);
        Editor.Focus();
        StatusMessage?.Invoke(this, "已在下方插入段落");
    }

    /// <summary>删除最后点击的块（源码级删除整块行范围）。</summary>
    private void DeleteCurrentBlock()
    {
        var block = _lastPreviewBlock;
        if (block is null || block.Line <= 0)
        {
            StatusMessage?.Invoke(this, "请先在预览中点击要操作的位置。");
            return;
        }

        var ordered = _anchorIndex.Keys.Where(b => b.Line > 0).OrderBy(b => b.Line).ToList();
        var endLine = int.MaxValue;
        foreach (var candidate in ordered)
        {
            if (candidate.Line > block.Line && candidate.Line < endLine)
            {
                endLine = candidate.Line;
            }
        }

        var (start, _) = GetLineSpan(Editor.Text, block.Line);
        var end = endLine == int.MaxValue ? Editor.Text.Length : GetLineSpan(Editor.Text, endLine).Start;

        if (end > start)
        {
            Editor.Select(start, end - start);
            Editor.SelectedText = string.Empty;
            StatusMessage?.Invoke(this, "已删除该块");
        }
    }
}
