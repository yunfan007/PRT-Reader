using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Prt.App.Models;
using Prt.App.Services;
using Prt.Core.Structure;
using Prt.Core.Syntax;
using StructLayoutKind = System.Runtime.InteropServices.LayoutKind;

namespace Prt.App.Views;

// 目录面板与诊断面板（MainWindow 的 partial；D-06）
// 拆分依据：《工程改进方案》§4.8 / CRS 9.6.4 D-06；拆分**只做位置迁移**，未改行为。
public partial class MainWindow : Window
{
    // ─────────────────────────────── 面板 ───────────────────────────────

    private void ApplyOutlineVisibility()
    {
        var show = ShowOutlineMenuItem.IsChecked == true;
        OutlinePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        OutlineSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        OutlineColumn.Width = show ? new GridLength(OutlineWidth) : new GridLength(0);
        OutlineSplitterColumn.Width = show ? GridLength.Auto : new GridLength(0);
    }

    private void ApplyProblemsVisibility()
    {
        var show = ShowProblemsMenuItem.IsChecked == true;
        ProblemsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ProblemsSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ProblemsColumn.Width = show ? new GridLength(ProblemsWidth) : new GridLength(0);
        ProblemsSplitterColumn.Width = show ? GridLength.Auto : new GridLength(0);
    }

    private void UpdateOutline() => _ = RebuildOutlineAsync();

    /// <summary>
    /// 后台重新构建侧边栏大纲（先显示占位、再计算后填入）。<c>UpdateOutline</c> 被普通方法调用、
    /// 不是事件处理器，故不能是 <c>async void</c>——按锚点 D-22，非事件处理器的异步方法必须是
    /// <c>async Task</c>，这里由同名的同步入口 fire-and-forget 触发。
    /// </summary>
    private async Task RebuildOutlineAsync()
    {
        var entry = ActiveEntry;
        if (entry is null)
        {
            OutlineTree.ItemsSource = null;
            return;
        }

        // 解析仍在途（结构/标题结果未就绪）：切到「计算中」占位，待完成后再填真实大纲。
        // 这保证打开大文档时侧边栏先显示占位、后台计算完成后再更新，而非卡住等待。
        if (entry.View.IsParseInProgress)
        {
            OutlineTree.ItemsSource = new[] { OutlineComputingNode() };
            return;
        }

        var toc = entry.View.Toc;
        if (toc.Count == 0)
        {
            OutlineTree.ItemsSource = new[]
            {
                new OutlineNode { Header = "(无标题层级)", IsPlaceholder = true },
            };
            return;
        }

        try
        {
            // 先给占位（避免旧大纲残留到新结果就绪的空白），再在后台构建节点。
            OutlineTree.ItemsSource = new[] { OutlineComputingNode() };
            var snapshotToc = toc;
            var nodes = await Task.Run(() => BuildOutlineNodes(snapshotToc));

            // 计算期间可能切换了标签 / 又发起了新的解析（结果已被替换）：本次结果不再适用就丢弃。
            var latest = ActiveEntry;
            if (latest is null || !ReferenceEquals(latest, entry) || !ReferenceEquals(latest.View.Toc, toc))
            {
                return;
            }

            OutlineTree.ItemsSource = nodes;
        }
        catch
        {
            // fire-and-forget 防火墙：后台构建失败时不崩界面，占位转成「目录不可用」的静态提示。
            OutlineTree.ItemsSource = new[] { new OutlineNode { Header = "(目录构建失败)", IsPlaceholder = true } };
        }
    }

    /// <summary>解析开始时立即把大纲切到「计算中」占位，提供先显示、后计算的即时反馈。</summary>
    private void OnViewParseStarted(TabEntry entry)
    {
        if (ReferenceEquals(entry, ActiveEntry))
        {
            OutlineTree.ItemsSource = new[] { OutlineComputingNode() };
        }
    }

    /// <summary>「正在计算目录…」占位节点：渲染为禁用，示意大纲数据在后台准备中。</summary>
    private static OutlineNode OutlineComputingNode()
        => new() { Header = "正在计算目录…", IsPlaceholder = true };

    /// <summary>大纲条目数不超过该值时默认全部展开；超过则只展开前两级，保持超大文档的大纲可用。</summary>
    internal const int OutlineExpandAllLimit = 300;

    /// <summary>
    /// 把扁平目录还原为层级大纲节点（用于侧边栏大纲的虚拟化绑定）。
    /// </summary>
    internal static IReadOnlyList<OutlineNode> BuildOutlineNodes(IReadOnlyList<TocEntry> entries)
    {
        var expandAll = entries.Count <= OutlineExpandAllLimit;
        var roots = new List<OutlineNode>();
        var stack = new List<(int Level, OutlineNode Node)>();

        foreach (var tocEntry in entries)
        {
            var level = Math.Max(1, tocEntry.Level);
            var node = new OutlineNode
            {
                Header = string.IsNullOrWhiteSpace(tocEntry.Number)
                    ? (string.IsNullOrWhiteSpace(tocEntry.Title) ? "(无标题)" : tocEntry.Title)
                    : tocEntry.Number + " " + tocEntry.Title,
                Level = level,
                IsSection = tocEntry.IsSection,
                Source = tocEntry.Source,
                IsExpanded = expandAll || level <= 2,
                Children = new List<OutlineNode>(),
            };

            while (stack.Count > 0 && stack[^1].Level >= level)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            if (stack.Count == 0)
            {
                roots.Add(node);
            }
            else
            {
                stack[^1].Node.Children.Add(node);
            }

            stack.Add((level, node));
        }

        return roots;
    }

    private void UpdateProblems()
    {
        var entry = ActiveEntry;
        IEnumerable<DiagnosticRow> rows = entry is null
            ? Array.Empty<DiagnosticRow>()
            : entry.View.Diagnostics.Select(diagnostic => new DiagnosticRow(diagnostic)).ToList();
        ProblemsList.ItemsSource = rows;
    }

    private void UpdateStatusBar()
    {
        var entry = ActiveEntry;
        if (entry is null)
        {
            StatusPositionText.Text = "—";
            StatusLengthText.Text = "—";
            StatusModeText.Text = ModeLabel(_viewMode);
            StatusDiagnosticText.Text = Localizer.T("status.diagnostics", 0, 0);
            return;
        }

        var (line, column) = entry.View.CaretPosition;
        StatusPositionText.Text = Localizer.T("status.position", line, column);
        StatusLengthText.Text = Localizer.T("status.chars", entry.View.CharacterCount);
        StatusModeText.Text = ModeLabel(_viewMode) + " · " + entry.View.ResolvedPreviewTheme;

        var computation = entry.View.HasUnsupportedComputation ? Localizer.T("status.comp.degraded") : string.Empty;
        StatusDiagnosticText.Text =
            Localizer.T("status.diagnostics", entry.View.ErrorCount, entry.View.WarningCount) + computation;
    }

    private static string ModeLabel(ViewMode mode) => mode switch
    {
        ViewMode.EditorOnly => Localizer.T("toolbar.mode.source"),
        ViewMode.PreviewOnly => Localizer.T("toolbar.mode.preview"),
        _ => Localizer.T("toolbar.mode.split"),
    };

    private void UpdateWindowTitle()
    {
        var entry = ActiveEntry;
        var app = Localizer.T("app.name");
        Title = entry is null ? app : $"{entry.Model.DisplayTitle} — {app}";
        TitleBarText.Text = Title;
    }

    private void SetStatus(string message) => StatusMessageText.Text = message;

    private void OnOutlineMenuClick(object sender, RoutedEventArgs e)
    {
        ApplyOutlineVisibility();
        PersistUiSettings();
    }

    private void OnOutlineButtonClick(object sender, RoutedEventArgs e)
    {
        ShowOutlineMenuItem.IsChecked = ShowOutlineMenuItem.IsChecked != true;
        ApplyOutlineVisibility();
        PersistUiSettings();
    }

    private void OnProblemsMenuClick(object sender, RoutedEventArgs e)
    {
        ApplyProblemsVisibility();
        PersistUiSettings();
    }

    private void OnProblemsButtonClick(object sender, RoutedEventArgs e)
    {
        ShowProblemsMenuItem.IsChecked = ShowProblemsMenuItem.IsChecked != true;
        ApplyProblemsVisibility();
        PersistUiSettings();
    }

    private void OnOutlineSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is OutlineNode { Source: not null } node)
        {
            ActiveView?.ScrollToBlock(node.Source);
        }
    }

    private void OnProblemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProblemsList.SelectedItem is DiagnosticRow row)
        {
            ActiveView?.NavigateTo(row.Line, row.Column);
        }
    }
}

/// <summary>侧边栏大纲的绑定节点：目录条目的轻量视图模型（配合虚拟化 TreeView 使用）。</summary>
public sealed class OutlineNode
{
    /// <summary>显示文本（编号 + 标题）。</summary>
    public string Header { get; init; } = string.Empty;

    /// <summary>层级（从 1 起）。</summary>
    public int Level { get; init; }

    /// <summary>是否为 section 块（加粗显示）。</summary>
    public bool IsSection { get; init; }

    /// <summary>占位节点（无标题层级时的空态），渲染为禁用。</summary>
    public bool IsPlaceholder { get; init; }

    /// <summary>对应的源块；null 表示不可跳转（占位节点）。</summary>
    public PrtBlock? Source { get; init; }

    /// <summary>展开状态（TwoWay 绑定到 TreeViewItem.IsExpanded；虚拟化回收后仍能还原）。</summary>
    public bool IsExpanded { get; set; }

    /// <summary>子节点；占位节点为空集合（无展开箭头）。</summary>
    public List<OutlineNode> Children { get; init; } = new();
}
