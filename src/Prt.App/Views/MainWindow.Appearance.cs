using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Prt.App.Services;
using StructLayoutKind = System.Runtime.InteropServices.LayoutKind;

namespace Prt.App.Views;

// 自绘标题栏与插入菜单（MainWindow 的 partial；D-06）
// 拆分依据：《工程改进方案》§4.8 / CRS 9.6.4 D-06；拆分**只做位置迁移**，未改行为。
public partial class MainWindow : Window
{
    // ─────────────────────────────── 自绘标题栏 ───────────────────────────────

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 窗口句柄只有到这里才存在；消息钩子与 DWM 圆角都交给独立的互操作类型（见 WindowInterop.cs）。
        WindowInterop.Attach(new WindowInteropHelper(this).Handle);
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCaptionCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowStateChanged(object sender, EventArgs e) => UpdateMaximizeVisual();

    private void UpdateMaximizeVisual()
    {
        var maximized = WindowState == WindowState.Maximized;

        // 最大化时窗口贴合工作区，不再需要外描边。
        RootFrame.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "向下还原" : "最大化";
    }

    // ─────────────────────────────── 插入菜单 ───────────────────────────────

    /// <summary>按片段目录填充「插入」菜单的三个分组（与编辑器右键菜单同源）。</summary>
    private void BuildInsertMenus()
    {
        FillSnippetGroup(InsertSemanticMenuItem, PrtSnippets.GroupSemantic);
        FillSnippetGroup(InsertStructureMenuItem, PrtSnippets.GroupStructure);
        FillSnippetGroup(InsertBasicMenuItem, PrtSnippets.GroupBasic);
    }

    private void FillSnippetGroup(MenuItem root, string group)
    {
        root.Items.Clear();
        foreach (var snippet in PrtSnippets.InGroup(group))
        {
            var item = new MenuItem
            {
                Header = snippet.Label,
                InputGestureText = snippet.Gesture,
            };

            var id = snippet.Id;
            item.Click += (_, _) => ActiveView?.ApplySnippet(id);
            root.Items.Add(item);
        }
    }
}
