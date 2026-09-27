using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Prt.Core.Syntax;
using Prt.App.Rendering;

namespace Prt.App.Views;

/// <summary>
/// 虚拟预览容器（《虚拟化改造方案》M2a）：只读的「块列表」阅读器。
/// <para>
/// 承载与整篇文档等长的项列表（文档头 / 各顶层块 / 目录项 / 脚注区），由
/// <see cref="VirtualizingStackPanel"/>（Recycling + 像素滚动单位）
/// 只实例化视口内若干项——滚出视口的块元素被回收，大文档滚动不再整篇参与布局。
/// 选 ListBox 而非裸 ItemsControl 的原因：虚拟化下目标项往往尚未实例化，
/// <see cref="ListBox.ScrollIntoView"/> 是框架提供的「先实现后滚动」的现成通道。
/// </para>
/// <para>
/// 项到元素的映射：模型项（<see cref="PrtBlock"/> / 头部 / 目录 / 脚注）在容器**实现时**
/// 才经 <see cref="ItemRenderer"/> 渲染成 FE；「块 ↔ 已实现容器」两张映射随之维护，
/// 容器回收或复用时同步更新——点击定位、光标联动、高亮都以「容器是否被实现」为准。
/// </para>
/// </summary>
internal sealed class VirtualPreview : ListBox
{
    /// <summary>项渲染工厂：把模型项渲染为独立 FE（由宿主提供 <see cref="VirtualPreviewBuilder"/> 的委托）。</summary>
    public Func<object, FrameworkElement>? ItemRenderer { get; set; }

    /// <summary>已实现容器 → 源块（点击预览定位源码用）。</summary>
    private readonly Dictionary<DependencyObject, PrtBlock> _containerToBlock = new();

    /// <summary>源块 → 已实现容器（联动高亮用；随容器回收增减）。容器是 ListBoxItem（Control，带 Background）。</summary>
    private readonly Dictionary<PrtBlock, ContentControl> _realizedContainers = new();

    public VirtualPreview()
    {
        Focusable = false;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        Background = Brushes.Transparent;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        SetValue(ScrollViewer.CanContentScrollProperty, true);   // 虚拟化前提：按项滚动（滚动单位在面板上另配为像素）
        SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);

        // 虚拟化面板：回收模式 + 像素滚动单位（大块按项滚动会跳，像素单位顺滑）。
        SetCurrentValue(VirtualizingPanel.IsVirtualizingProperty, true);
        SetCurrentValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
        SetCurrentValue(VirtualizingPanel.ScrollUnitProperty, ScrollUnit.Pixel);
        SetCurrentValue(VirtualizingPanel.CacheLengthProperty, new VirtualizationCacheLength(1));
        SetCurrentValue(VirtualizingPanel.CacheLengthUnitProperty, VirtualizationCacheLengthUnit.Page);

        ItemsPanel = BuildItemsPanel();
        ItemContainerStyle = BuildItemContainerStyle();
    }

    private static ItemsPanelTemplate BuildItemsPanel()
    {
        var factory = new FrameworkElementFactory(typeof(VirtualizingStackPanel));
        factory.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
        factory.SetValue(VirtualizingStackPanel.OrientationProperty, Orientation.Vertical);
        return new ItemsPanelTemplate { VisualTree = factory };
    }

    /// <summary>
    /// 容器样式：剥掉 ListBoxItem 的选中 / 悬停高亮与内边距——预览是阅读面，不是可选列表；
    /// 视觉状态全部由块自身承载。
    /// </summary>
    private static Style BuildItemContainerStyle()
    {
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.MarginProperty, new Thickness(0));

        var template = new ControlTemplate(typeof(ListBoxItem))
        {
            VisualTree = presenter,
        };

        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(FocusableProperty, false));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(MarginProperty, new Thickness(0)));
        style.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(TemplateProperty, template));
        return style;
    }

    // ─────────────────────────────── 容器实现 / 回收 ───────────────────────────────

    /// <summary>
    /// 容器实现时把模型项渲染为 FE 并登记「块 ↔ 容器」映射。
    /// Recycling 模式下同一容器会换绑新项：先清掉它上一轮的映射与视觉残留再登记。
    /// </summary>
    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);

        // 换绑清理：这个容器上一轮可能属于别的块（回收池不保证先走 CleanUp 路径）。
        PrtBlock? previousBlock = null;
        foreach (var pair in _containerToBlock)
        {
            if (ReferenceEquals(pair.Key, element))
            {
                previousBlock = pair.Value;
                break;
            }
        }
        if (previousBlock is not null)
        {
            _containerToBlock.Remove(element);
            if (_realizedContainers.Remove(previousBlock))
            {
                // 旧块的容器已换绑：其联动高亮随视觉残留一起作废。
            }
        }

        var renderer = ItemRenderer;
        FrameworkElement? content;
        try
        {
            content = renderer?.Invoke(item);
        }
        catch
        {
            // 渲染工厂内部已就地降级；此处再兜一层，绝不让单个坏项炸掉整份列表。
            content = new TextBlock { Text = "（该项渲染失败）" };
        }

        if (element is ContentControl container)
        {
            // 高亮画刷由宿主在容器上设置；换绑时恢复为透明底，避免上一项的高亮「跟」到新项。
            container.SetCurrentValue(BackgroundProperty, Brushes.Transparent);
            container.Content = content;
        }

        if (item is PrtBlock block && element is ContentControl containerElement)
        {
            // 联动高亮打在容器上（整行底色），故映射存容器而非内容元素。
            _containerToBlock[element] = block;
            _realizedContainers[block] = containerElement;
        }
    }

    /// <summary>容器回收或项被移除时解除映射，防止引用已失效的元素。</summary>
    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is ContentControl container)
        {
            container.Content = null;
        }

        if (item is PrtBlock block)
        {
            _containerToBlock.Remove(element);
            _realizedContainers.Remove(block);
        }

        base.ClearContainerForItemOverride(element, item);
    }

    // ─────────────────────────────── 查询与导航 ───────────────────────────────

    /// <summary>判断一次点击落在哪个源块的容器内（沿视觉树向上找列表容器）。</summary>
    public bool TryGetBlockAt(DependencyObject source, out PrtBlock block)
    {
        var current = source;
        while (current is not null)
        {
            if (_containerToBlock.TryGetValue(current, out var found))
            {
                block = found;
                return true;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        block = null!;
        return false;
    }

    /// <summary>取某个源块当前已实现的容器（联动高亮用；未实现返回 false）。</summary>
    public bool TryGetRealizedContainer(PrtBlock block, out ContentControl container)
        => _realizedContainers.TryGetValue(block, out container!);

    /// <summary>把指定索引的项滚动到视口（虚拟化下未实例化的项会先被实现再滚动）。</summary>
    public void ScrollToIndex(int index)
    {
        if (index < 0 || index >= Items.Count)
        {
            return;
        }

        ScrollIntoView(Items.GetItemAt(index));
    }

    /// <summary>清空项并解除全部映射（换文档 / 重新解析时调用，避免容器复用与新文档交错）。</summary>
    public void ResetItems()
    {
        ItemsSource = null;
        _containerToBlock.Clear();
        _realizedContainers.Clear();
    }

    // ─────────────────────────────── 就地编辑的滚动锁定 ───────────────────────────────

    /// <summary>滚动是否被锁定（就地编辑期间锁定：被编辑的容器必须钉在视口内不被回收）。</summary>
    public bool IsScrollLocked => _lockScroll;

    private bool _lockScroll;
    private ScrollViewer? _lockedScroll;

    /// <summary>
    /// 锁定 / 解锁预览滚动（就地编辑用，方案增补：编辑时暂时禁用滚动）。
    /// <para>
    /// 锁定 = 垂直滚动条禁用 + 滚轮事件就地吞掉。虚拟化面板只回收「滚出视口」的容器，
    /// 滚动被锁死后视口纹丝不动，被编辑的容器因此绝不会在编辑中途被回收——
    /// 这是「虚拟化」与「就地编辑」能并存的关键。
    /// </para>
    /// </summary>
    public void SetScrollLocked(bool locked)
    {
        _lockScroll = locked;
        if (_lockedScroll is null)
        {
            _lockedScroll = FindScrollViewer(this);
        }

        if (_lockedScroll is not null)
        {
            _lockedScroll.VerticalScrollBarVisibility = locked
                ? ScrollBarVisibility.Hidden
                : ScrollBarVisibility.Auto;
        }
    }

    /// <summary>锁定期间吞掉滚轮：既不滚自己，也不冒泡（编辑框自身的内部滚动不受影响——它在更内层先处理掉）。</summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_lockScroll)
        {
            e.Handled = true;
            return;
        }

        base.OnMouseWheel(e);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer found)
            {
                return found;
            }

            var nested = FindScrollViewer(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
