using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Prt.App.Models;
using Prt.App.Services;
using StructLayoutKind = System.Runtime.InteropServices.LayoutKind;

namespace Prt.App.Views;

// 标签页生命周期（MainWindow 的 partial；D-06）
// 拆分依据：《工程改进方案》§4.8 / CRS 9.6.4 D-06；拆分**只做位置迁移**，未改行为。
public partial class MainWindow : Window
{
    // ─────────────────────────────── 标签页 ───────────────────────────────

    private void NewDocument(bool withWelcome = true)
    {
        if (!EnsureTabSlot())
        {
            return;
        }

        _untitledCounter++;
        var document = new DocumentTab
        {
            // 启动时的首个空标签不带示例内容：示例文档只经「帮助 → 打开示例文档」按需显示。
            Text = withWelcome ? SampleDocument.Welcome : string.Empty,
            UntitledIndex = _untitledCounter,
        };
        AddTab(document);
    }

    /// <summary>
    /// 授权门槛：免费版限制同时打开的标签页数量（Standard / Professional 不限）。
    /// 达到上限时向用户说明并拒绝新建，返回 false。
    /// </summary>
    private bool EnsureTabSlot()
    {
        if (Activation.CurrentLevel != LicenseLevel.Free || _entries.Count < FreeTabLimit)
        {
            return true;
        }

        MessageBox.Show(
            this,
            $"免费版最多同时打开 {FreeTabLimit} 个标签页。\n\n" +
            "当前授权：" + Activation.CurrentSummary() + "\n\n" +
            "如需解除限制，请在「帮助 → 激活与授权…」中输入激活码（限时激活码即 VIP）。",
            "PRT 阅读器",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        return false;
    }

    private TabEntry AddTab(DocumentTab model)
    {
        var view = new DocumentView(model);
        view.LoadText(model.Text);
        view.ConfigureDefaults(_previewTheme, StrictMenuItem.IsChecked == true ? true : null);

        // 新标签沿用已存的显示偏好（缩放 / 折行），与设置页里的「默认显示方式」配套。
        view.SetZoom(SettingsStore.Current.Zoom);
        view.SetWordWrap(WordWrapMenuItem.IsChecked == true);
        // 先应用最终视图模式并同步完成首次渲染：标签加入时内容即完整，
        // 不会先以默认分栏布局闪现一帧、也不会出现「空白预览 → 加载完成」的跳变。
        view.ApplyViewMode(_viewMode);
        view.Reparse();

        var headerText = new TextBlock
        {
            Text = model.DisplayTitle,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var closeButton = new Button
        {
            Content = "\uE711",
            Style = (Style)FindResource("Prt.IconButton"),
            FontFamily = (FontFamily)FindResource("Prt.IconFont"),
            FontSize = 10,
            Margin = new Thickness(6, 0, 0, 0),
            Focusable = false,
            ToolTip = "关闭标签",
        };

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(headerText);
        header.Children.Add(closeButton);

        var item = new TabItem { Header = header, Content = view };
        closeButton.Click += (_, _) => CloseTab(model);

        var entry = new TabEntry
        {
            Model = model,
            View = view,
            Item = item,
            HeaderText = headerText,
        };

        view.StateChanged += (_, _) => OnViewStateChanged(entry);
        view.ParseStarted += (_, _) => OnViewParseStarted(entry);
        view.CaretChanged += (_, _) => OnViewCaretChanged(entry);
        view.StatusMessage += (_, message) => SetStatus(message);

        _entries.Add(entry);
        Tabs.Items.Add(item);
        Tabs.SelectedItem = item;
        return entry;
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var entry = ActiveEntry;
        if (entry is not null)
        {
            entry.View.ApplyViewMode(_viewMode);
        }

        UpdateOutline();
        UpdateProblems();
        UpdateStatusBar();
        UpdateWindowTitle();
    }

    private void OnViewStateChanged(TabEntry entry)
    {
        entry.HeaderText.Text = entry.Model.DisplayTitle;

        if (ReferenceEquals(entry, ActiveEntry))
        {
            UpdateOutline();
            UpdateProblems();
            UpdateStatusBar();
            UpdateWindowTitle();
        }
    }

    private void OnViewCaretChanged(TabEntry entry)
    {
        if (ReferenceEquals(entry, ActiveEntry))
        {
            UpdateStatusBar();
        }
    }

    private void CloseActiveTab()
    {
        if (ActiveEntry is { } entry)
        {
            CloseTab(entry.Model);
        }
    }

    private void CloseTab(DocumentTab model)
    {
        var entry = _entries.FirstOrDefault(candidate => candidate.Model == model);
        if (entry is null)
        {
            return;
        }

        if (!ConfirmDiscard(entry))
        {
            return;
        }

        Tabs.Items.Remove(entry.Item);
        _entries.Remove(entry);

        if (_entries.Count == 0)
        {
            NewDocument();
        }
        else
        {
            UpdateOutline();
            UpdateProblems();
            UpdateStatusBar();
            UpdateWindowTitle();
        }

        SetStatus("已关闭：" + entry.Model.DisplayTitle);
    }

    private bool ConfirmDiscard(TabEntry entry)
    {
        if (!entry.Model.IsDirty)
        {
            return true;
        }

        var result = MessageBox.Show(
            this,
            $"「{entry.Model.DisplayTitle}」有未保存的修改，确定放弃这些修改吗？",
            "PRT 阅读器",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        return result == MessageBoxResult.Yes;
    }
}
