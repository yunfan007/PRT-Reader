using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Prt.App.Models;
using Prt.Core.Syntax;

namespace Prt.App.Views;

// 编辑器与预览的联动、滚动同步与高亮（DocumentView 的 partial；D-06）
// M2a：预览换成只读虚拟阅读器，联动从「TextPointer / GetCharacterRect」改为「块 → 列表项」导航；
// 高亮打在已实现容器的整行底色上（未滚到的块没有元素，待它被实例化后由再次联动着色）。
public partial class DocumentView : UserControl
{
    // ─────────────────────────────── 编辑器 ↔ 预览联动 ───────────────────────────────

    /// <summary>
    /// 预览重建后复位联动状态：高亮容器已随列表重建失效需重新落位；但**保留**当前联动的源块，
    /// 以免每次重新解析（打字触发）都把预览重新滚到该块——长块会因此反复跳位、只看到一块的局部。
    /// </summary>
    private void ResetCaretSync()
    {
        _highlightedContainer = null;
        _highlightOriginalBrush = null;
        if (_caretSyncBlock is { } block && !_anchorIndex.ContainsKey(block))
        {
            _caretSyncBlock = null;
        }
    }

    /// <summary>
    /// 编辑器光标 → 虚拟预览：光标所在源块变化时，列表滚动到对应项并给容器着高亮。
    /// </summary>
    private void SyncPreviewToCaret()
    {
        // 就地编辑进行中：联动会改动编辑容器的底色并尝试滚动（已锁定），全部停掉。
        if (_editBlock is not null || Mode == ViewMode.EditorOnly || _anchorIndex.Count == 0 || Document is null)
        {
            return;
        }

        var (line, _) = CaretPosition;
        PrtBlock? best = null;
        foreach (var candidate in _anchorIndex.Keys)
        {
            if (candidate.Line <= line && (best is null || candidate.Line > best.Line))
            {
                best = candidate;
            }
        }

        if (best is null || ReferenceEquals(best, _caretSyncBlock))
        {
            return;
        }

        _caretSyncBlock = best;
        ApplyPreviewHighlight(best);

        if (Mode == ViewMode.Split)
        {
            ScrollVirtualToBlock(best);
        }
    }

    /// <summary>把预览高亮迁移到指定源块的已实现容器（还原上一个容器的原始背景）。</summary>
    private void ApplyPreviewHighlight(PrtBlock block)
    {
        if (_highlightedContainer is not null)
        {
            _highlightedContainer.Background = _highlightOriginalBrush;
            _highlightedContainer = null;
            _highlightOriginalBrush = null;
        }

        // 目标项尚未被虚拟化实例化时跳过着色：等用户滚动到附近、容器实现后，
        // 下一次光标进入该块（或点击）会再次联动补上。虚拟化下这是预期行为而非缺陷。
        if (Preview.TryGetRealizedContainer(block, out var container))
        {
            _highlightOriginalBrush = container.Background;
            container.Background = SyncHighlightBrush;
            _highlightedContainer = container;
        }
    }

    /// <summary>
    /// 只读虚拟预览中点击任意部分 → 源码栏滑动到对应源块所在行并获得焦点：
    /// 无选区概念（只读），点击处所在块整行高亮，源码栏整行选中。
    /// </summary>
    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        // 就地编辑进行中：点在编辑框上不应触发「定位源码」。
        if (_editBlock is not null || _anchorIndex.Count == 0 || e.OriginalSource is System.Windows.Controls.CheckBox)
        {
            return;
        }

        if (!Preview.TryGetBlockAt(e.OriginalSource as DependencyObject ?? Preview, out var block))
        {
            return;
        }

        _lastPreviewBlock = block;
        ApplyPreviewHighlight(block);
        JumpEditorToBlock(block);
    }

    /// <summary>源码栏滑动到指定源块行、整行选中高亮并取得焦点。</summary>
    private void JumpEditorToBlock(PrtBlock block)
    {
        if (Mode == ViewMode.PreviewOnly)
        {
            ApplyViewMode(ViewMode.Split);
        }

        var line = Math.Max(1, block.Line);
        var (start, length) = GetLineSpan(Editor.Text, line);
        if (start < 0)
        {
            return;
        }

        Editor.Focus();
        Editor.Select(start, length);
        ScrollEditorGuarded(line);
    }

    /// <summary>某块源码行范围的结束字符下标（用于在块内定位文字）。</summary>
    private (int End, int StartLine) BlockSourceEnd(PrtBlock block)
    {
        var ordered = _anchorIndex.Keys.Where(b => b.Line > 0).OrderBy(b => b.Line).ToList();
        var endLine = int.MaxValue;
        foreach (var candidate in ordered)
        {
            if (candidate.Line > block.Line && candidate.Line < endLine)
            {
                endLine = candidate.Line;
            }
        }

        return endLine == int.MaxValue
            ? (Editor.Text.Length, block.Line)
            : (GetLineSpan(Editor.Text, endLine).Start, block.Line);
    }

    private void ScrollEditorGuarded(int line)
    {
        _programmaticScroll = true;
        try
        {
            Editor.ScrollToLine(Math.Max(0, line - 1));
        }
        finally
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _programmaticScroll = false));
        }
    }

    /// <summary>视觉化编辑高亮画刷（半透明主题蓝，浅深色主题下均可辨识）。</summary>
    private static readonly Brush SyncHighlightBrush = CreateSyncHighlightBrush();

    private static Brush CreateSyncHighlightBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(64, 0x09, 0x69, 0xDA));
        brush.Freeze();
        return brush;
    }

    public void FocusEditor() => Editor.Focus();

    // ─────────────────────────────── 滚动同步 ───────────────────────────────

    private void AttachScrollSync()
    {
        _editorScroll = FindDescendant<ScrollViewer>(Editor);
        _previewScroll = FindDescendant<ScrollViewer>(Preview);

        if (_editorScroll is not null)
        {
            _editorScroll.ScrollChanged += OnEditorScrollChanged;
        }
        if (_previewScroll is not null)
        {
            _previewScroll.ScrollChanged += OnPreviewScrollChanged;
        }
    }

    private void OnEditorScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_syncingScroll || _programmaticScroll || Mode != ViewMode.Split || _previewScroll is null)
        {
            return;
        }
        if (Math.Abs(e.VerticalChange) < 0.01 && Math.Abs(e.ExtentHeightChange) < 0.01)
        {
            return;
        }

        _syncingScroll = true;
        try
        {
            SyncScrollRatio(_editorScroll, _previewScroll);
        }
        finally
        {
            _syncingScroll = false;
        }
    }

    private void OnPreviewScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_syncingScroll || _programmaticScroll || Mode != ViewMode.Split || _editorScroll is null)
        {
            return;
        }
        if (Math.Abs(e.VerticalChange) < 0.01)
        {
            return;
        }

        _syncingScroll = true;
        try
        {
            SyncScrollRatio(_previewScroll, _editorScroll);
        }
        finally
        {
            _syncingScroll = false;
        }
    }

    private static void SyncScrollRatio(ScrollViewer? source, ScrollViewer? target)
    {
        if (source is null || target is null || source.ScrollableHeight <= 0 || target.ScrollableHeight <= 0)
        {
            return;
        }

        var ratio = source.VerticalOffset / source.ScrollableHeight;
        target.ScrollToVerticalOffset(ratio * target.ScrollableHeight);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
            {
                return typed;
            }
            var nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }
        return null;
    }
}
