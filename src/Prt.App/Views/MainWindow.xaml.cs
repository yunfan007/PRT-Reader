using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Prt.App.Models;
using Prt.App.Services;
using Prt.App.Theming;
using Prt.Core;
using Prt.Core.Import;
using Prt.Core.Structure;
using Prt.Core.Syntax;
using Microsoft.Win32;

// Prt.Core 里也有一个同名的 LayoutKind（布局块类型），此处显式指向互操作用的枚举。
using StructLayoutKind = System.Runtime.InteropServices.LayoutKind;

namespace Prt.App.Views;

/// <summary>
/// 主窗口：负责标签页管理、菜单与工具栏命令、目录面板、诊断面板、状态栏与文件读写。
/// <para>
/// 单一职责边界：文档本身的解析与呈现全部在 <see cref="DocumentView"/> 内完成，
/// 本类只做「跨文档的编排」，不接触语法树细节（除目录跳转所需的源块引用外）。
/// </para>
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>标签页与其关联视图的绑定记录。</summary>
    internal sealed class TabEntry
    {
        public required DocumentTab Model { get; init; }

        public required DocumentView View { get; init; }

        public required TabItem Item { get; init; }

        public required TextBlock HeaderText { get; init; }
    }

    private const double OutlineWidth = 246d;
    private const double ProblemsWidth = 340d;

    /// <summary>免费版（未激活）允许同时打开的标签页上限。</summary>
    private const int FreeTabLimit = 2;

    private readonly List<TabEntry> _entries = new();

    private ViewMode _viewMode = ViewMode.Split;

    /// <summary>预览主题；null 表示跟随文档 <c>meta.theme</c>。</summary>
    private string? _previewTheme;

    private int _untitledCounter;

    public MainWindow()
    {
        InitializeComponent();

        StatusConformanceText.Text = Localizer.T("status.conformance", PrtCapabilities.SpecificationVersion);

        // 先按已存设置同步菜单勾选与各视图默认值，再建文档标签——
        // ApplyOutlineVisibility / SetViewMode 等一律以菜单勾选状态为准，顺序不能反。
        ApplyPersistedSettings();

        BuildInsertMenus();
        UpdateMaximizeVisual();
        // 启动恢复上次会话：构造期只做"有没有可恢复内容"的**纯内存**判断，
        // 真正的打开动作推到窗口 Loaded 之后。
        // 理由是硬性的：打开文档要过安全申报，而申报不能阻塞调用线程——在构造期同步等，
        // 等于在 UI 线程上等一个需要 UI 线程的答复（4.3 第 3 条 × D-14），必然自锁。
        // 无可恢复内容时同步开一个完全空白的标签（不填任何示例内容——空白就是空白，
        // 不显示「# 未命名文档」）；示例文档仍由「帮助 → 打开示例文档…」按需打开。
        if (!SessionService.TryLoad(out var sessionPaths, out var sessionActiveIndex))
        {
            NewDocument(withWelcome: false);
        }
        else
        {
            _restorePaths = sessionPaths;
            _restoreActiveIndex = sessionActiveIndex;
        }

        Loaded += OnMainWindowLoaded;
    }

    // ─────────────────────────────── 启动恢复 ───────────────────────────────

    private IReadOnlyList<string>? _restorePaths;

    private int _restoreActiveIndex;

    /// <summary>
    /// 恢复上次会话：按退出时的标签顺序重新打开已保存的文档，并选中当时的活动标签。
    /// 文件已不存在者自动跳过；一个也恢复不了时开一个空白标签。
    /// </summary>
    private async Task CompleteStartupAsync()
    {
        if (_restorePaths is null)
        {
            return; // 构造期已经开好空白标签
        }

        var restored = 0;
        foreach (var path in _restorePaths)
        {
            // quiet：恢复的是"上次退出时开着的文档"，早被删掉属正常情况，
            // 不该为它在启动时弹一串错误框。文件在不在由读取本身回答（不再先探一次）。
            if (await OpenPathAsync(path, quiet: true))
            {
                restored++;
            }
        }

        if (restored == 0)
        {
            NewDocument(withWelcome: false);
            return;
        }

        // 选中会话记录中的活动标签（按「已保存文档」在当前标签列表中的位置对位）。
        if (SessionService.TryLoad(out var saved, out _)
            && _restoreActiveIndex < saved.Count
            && saved[_restoreActiveIndex] is { } activePath)
        {
            var index = 0;
            foreach (var entry in _entries)
            {
                if (entry.Model.FilePath is not null
                    && string.Equals(entry.Model.FilePath, activePath, StringComparison.OrdinalIgnoreCase))
                {
                    Tabs.SelectedIndex = index;
                    break;
                }

                index++;
            }
        }
    }

    /// <summary>把当前标签布局写入会话记忆（仅记录已保存文档；未命名文档不入记录）。</summary>
    private void SaveSession()
    {
        var paths = new List<string>();
        var activeIndex = 0;
        foreach (var entry in _entries)
        {
            if (entry.Model.FilePath is not null)
            {
                if (entry.Item == Tabs.SelectedItem)
                {
                    activeIndex = paths.Count;
                }

                paths.Add(entry.Model.FilePath);
            }
            else if (entry.Item == Tabs.SelectedItem)
            {
                // 活动标签是未命名文档：仍记录其位置，恢复时落到首个已恢复文档。
                activeIndex = paths.Count;
            }
        }

        // 会话只落程序目录（《设计取舍》第 13 条）。会话不是关键数据：写不进去不打断退出流程，
        // 也不弹提示——下一个文档恢复不了，用户看到的是"空标签"而不是错误。
        SessionService.TrySave(paths, Math.Clamp(activeIndex, 0, Math.Max(0, paths.Count - 1)), out _);
    }

    private void OnMainWindowLoaded(object sender, RoutedEventArgs e) =>
        RunGuarded(nameof(OnMainWindowLoaded), async () =>
        {
            // 自定义 MenuItem 模板固定使用 Placement=Bottom 以确保顶层菜单竖直向下弹出；
            // 子菜单需要按 Role 改为 Placement=Right，但 Placement 不是依赖属性，无法在 XAML 中
            // 通过触发器按 Role 切换 —— 这里在模板被实例化后再统一调整一次。
            AdjustSubmenuPlacements();

            // 会话恢复在这里做（不在构造期）：打开文档要过安全申报，申报必须能回到 UI 线程，
            // 因而只能在消息循环已经跑起来之后进行。本处理器需要 await，载体是 RunGuarded
            // （异常总兜底见 MainWindow.Guard.cs）；但"一个都恢复不了"是**可预期的局部故障**，
            // 不该退化成一行的状态栏提示，所以就地再兜一层：保证至少留一个可用标签。
            try
            {
                await CompleteStartupAsync();
            }
            catch (Exception ex)
            {
                // 会话恢复失败不影响程序可用：开一个空白标签继续。
                SetStatus("恢复上次会话失败：" + ex.Message);
                if (_entries.Count == 0)
                {
                    NewDocument(withWelcome: false);
                }
            }
        });

    /// <summary>启动收尾的测试入口：窗口未被 Show 时不会触发 Loaded，自检需要显式跑一次。</summary>
    internal Task CompleteStartupForTestAsync() => CompleteStartupAsync();

    /// <summary>
    /// 沿 Menu 树向下，把所有 <c>Role=SubmenuHeader/SubmenuItem</c> 的 PART_Popup
    /// 切换为 Placement=Right，使子菜单从父菜单项侧向弹出（与 WinUI 3 行为一致）。
    /// </summary>
    private void AdjustSubmenuPlacements()
    {
        foreach (var descendant in EnumerateMenuItems(this))
        {
            if (descendant is not MenuItem item)
            {
                continue;
            }

            if (item.Role != MenuItemRole.SubmenuHeader && item.Role != MenuItemRole.SubmenuItem)
            {
                continue;
            }

            item.ApplyTemplate();
            if (item.Template?.FindName("PART_Popup", item) is Popup popup)
            {
                popup.Placement = PlacementMode.Right;
                popup.PlacementTarget = item;
            }
        }
    }

    private static IEnumerable<object> EnumerateMenuItems(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in EnumerateMenuItems(child))
            {
                yield return nested;
            }
        }
    }

    private TabEntry? ActiveEntry
        => Tabs.SelectedItem is TabItem item ? _entries.FirstOrDefault(entry => entry.Item == item) : null;

    private DocumentView? ActiveView => ActiveEntry?.View;
}
