using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Prt.App.Models;
using Prt.App.Services;
using Prt.Core.Syntax;

namespace Prt.App.Views;

// 视图模式 / 缩放 / 折行与文档内导航（DocumentView 的 partial；D-06）
// 拆分依据：《工程改进方案》§4.8 / CRS 9.6.4 D-06；拆分**只做位置迁移**，未改行为。
public partial class DocumentView : UserControl
{
    // ─────────────────────────────── 视图模式 / 缩放 ───────────────────────────────

    public void ApplyViewMode(ViewMode mode)
    {
        Mode = mode;

        switch (mode)
        {
            case ViewMode.EditorOnly:
                EditorColumn.Width = new GridLength(1, GridUnitType.Star);
                SplitterColumn.Width = new GridLength(0);
                PreviewColumn.Width = new GridLength(0);
                Preview.Visibility = Visibility.Collapsed;
                Splitter.Visibility = Visibility.Collapsed;
                break;

            case ViewMode.PreviewOnly:
                EditorColumn.Width = new GridLength(0);
                SplitterColumn.Width = new GridLength(0);
                PreviewColumn.Width = new GridLength(1, GridUnitType.Star);
                Preview.Visibility = Visibility.Visible;
                Splitter.Visibility = Visibility.Collapsed;
                break;

            default:
                EditorColumn.Width = new GridLength(1, GridUnitType.Star);
                SplitterColumn.Width = GridLength.Auto;
                PreviewColumn.Width = new GridLength(1, GridUnitType.Star);
                Preview.Visibility = Visibility.Visible;
                Splitter.Visibility = Visibility.Visible;
                break;
        }
    }

    public void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.5d, 3d);
        Editor.FontSize = BaseEditorFontSize * _zoom;
        Preview.LayoutTransform = Math.Abs(_zoom - 1d) < 0.001
            ? Transform.Identity
            : new ScaleTransform(_zoom, _zoom);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleWordWrap()
    {
        var wrap = Editor.TextWrapping != TextWrapping.Wrap;
        Editor.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        Editor.HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ─────────────────────────────── 导航 ───────────────────────────────

    /// <summary>把编辑器光标定位到指定逻辑行列（用于诊断面板跳转）。</summary>
    public void NavigateTo(int line, int column)
    {
        if (Mode == ViewMode.PreviewOnly)
        {
            ApplyViewMode(ViewMode.Split);
        }

        var index = Math.Clamp(TextNavigation.ToIndex(Editor.Text, line, column), 0, Editor.Text.Length);
        Editor.Focus();
        Editor.CaretIndex = index;
        ScrollEditorGuarded(line);
        Editor.Select(index, 0);
    }

    /// <summary>在预览中滚动到指定源块的位置（用于目录与交叉引用跳转）。</summary>
    public void ScrollToBlock(PrtBlock? block)
    {
        if (block is null)
        {
            return;
        }

        if (Mode == ViewMode.EditorOnly)
        {
            ApplyViewMode(ViewMode.Split);
        }

        // 虚拟预览（M2a）：块 → 列表项索引，ScrollIntoView 负责先实现后滚动。
        if (_anchorIndex.TryGetValue(block, out var index))
        {
            _programmaticScroll = true;
            try
            {
                Preview.ScrollToIndex(index);
            }
            finally
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => _programmaticScroll = false));
            }
        }
    }
}
