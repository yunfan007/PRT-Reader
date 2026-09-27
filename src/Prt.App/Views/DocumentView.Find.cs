using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Prt.App.Services;

namespace Prt.App.Views;

// 查找 / 替换（DocumentView 的 partial；D-06）
// 拆分依据：《工程改进方案》§4.8 / CRS 9.6.4 D-06；拆分**只做位置迁移**，未改行为。
public partial class DocumentView : UserControl
{
    // ─────────────────────────────── 查找 / 替换 ───────────────────────────────

    public void ShowFind(bool replace)
    {
        FindBar.Visibility = Visibility.Visible;

        var replaceVisibility = replace ? Visibility.Visible : Visibility.Collapsed;
        ReplaceLabel.Visibility = replaceVisibility;
        ReplaceBox.Visibility = replaceVisibility;
        ReplaceButton.Visibility = replaceVisibility;
        ReplaceAllButton.Visibility = replaceVisibility;

        if (!string.IsNullOrEmpty(Editor.SelectedText) && !Editor.SelectedText.Contains('\n', StringComparison.Ordinal))
        {
            FindBox.Text = Editor.SelectedText;
        }

        FindBox.Focus();
        FindBox.SelectAll();
    }

    public void HideFind() => FindBar.Visibility = Visibility.Collapsed;

    private StringComparison FindComparison
        => MatchCaseBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private void OnFindNextClick(object sender, RoutedEventArgs e) => FindNext();

    private void OnCloseFindClick(object sender, RoutedEventArgs e)
    {
        HideFind();
        Editor.Focus();
    }

    private bool FindNext()
    {
        var needle = FindBox.Text;
        if (needle.Length == 0)
        {
            return false;
        }

        var text = Editor.Text;
        var comparison = FindComparison;
        var start = Math.Min(Math.Clamp(Editor.CaretIndex + Editor.SelectionLength, 0, text.Length), text.Length);

        var index = start < text.Length ? text.IndexOf(needle, start, comparison) : -1;
        if (index < 0 && start > 0)
        {
            // 回绕到文档开头继续查找。
            index = text.IndexOf(needle, 0, start, comparison);
        }

        if (index < 0)
        {
            StatusMessage?.Invoke(this, "未找到：" + needle);
            return false;
        }

        Editor.Focus();
        Editor.Select(index, needle.Length);
        var (line, _) = TextNavigation.ToLineColumn(text, index);
        Editor.ScrollToLine(Math.Max(0, line - 1));
        return true;
    }

    private void OnReplaceClick(object sender, RoutedEventArgs e)
    {
        var needle = FindBox.Text;
        if (needle.Length == 0)
        {
            return;
        }

        if (Editor.SelectionLength == needle.Length
            && string.Equals(Editor.SelectedText, needle, FindComparison))
        {
            var caret = Editor.CaretIndex;
            var replacement = ReplaceBox.Text ?? string.Empty;
            Editor.SelectedText = replacement;
            Editor.CaretIndex = caret + replacement.Length;
        }

        FindNext();
    }

    private void OnReplaceAllClick(object sender, RoutedEventArgs e)
    {
        var needle = FindBox.Text;
        if (needle.Length == 0)
        {
            return;
        }

        var replacement = ReplaceBox.Text ?? string.Empty;
        var text = Editor.Text;
        var comparison = FindComparison;
        var builder = new StringBuilder(text.Length);
        var count = 0;
        var position = 0;

        while (position <= text.Length)
        {
            var index = text.IndexOf(needle, position, comparison);
            if (index < 0)
            {
                break;
            }
            builder.Append(text, position, index - position).Append(replacement);
            position = index + needle.Length;
            count++;
        }

        if (count == 0)
        {
            StatusMessage?.Invoke(this, "未找到可替换的内容");
            return;
        }

        builder.Append(text, position, text.Length - position);
        Editor.Text = builder.ToString();
        StatusMessage?.Invoke(this, $"已替换 {count} 处");
    }

    private void OnEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+F / Ctrl+H / Ctrl+L 由主窗口统一分发（这里只处理 F3 继续查找）。
        if (e.KeyboardDevice.Modifiers == ModifierKeys.None && e.Key == Key.F3)
        {
            FindNext();
            e.Handled = true;
        }
    }
}
