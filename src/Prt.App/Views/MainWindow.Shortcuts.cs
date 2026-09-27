using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Prt.App.Services;
using Microsoft.Win32;
using StructLayoutKind = System.Runtime.InteropServices.LayoutKind;

namespace Prt.App.Views;

// 窗口级快捷键与关闭（MainWindow 的 partial；D-06）
// 拆分依据：《工程改进方案》§4.8 / CRS 9.6.4 D-06；拆分**只做位置迁移**，未改行为。
public partial class MainWindow : Window
{
    // ─────────────────────────────── 窗口级快捷键与关闭 ───────────────────────────────

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e) =>
        RunGuarded(nameof(OnWindowPreviewKeyDown), async () =>
        {
            // F1：打开内置教程（不需要 Ctrl）。
            if (e.Key == Key.F1 && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                OnOpenTutorialClick(this, new RoutedEventArgs());
                return;
            }

            // Alt+↑ / Alt+↓：整行上移 / 下移（单行可视编辑的一部分）。
            if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0
                && (Keyboard.Modifiers & ModifierKeys.Control) == 0
                && e.Key is Key.Up or Key.Down)
            {
                e.Handled = true;
                ActiveView?.ApplyLineMove(e.Key == Key.Up ? -1 : 1);
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            {
                return;
            }

            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            switch (e.Key)
            {
                case Key.N:
                    e.Handled = true;
                    NewDocument();
                    break;

                case Key.OemComma:
                    e.Handled = true;
                    OnSettingsClick(this, new RoutedEventArgs());
                    break;

                case Key.P:
                    e.Handled = true;
                    PrintActiveDocument();
                    break;

                case Key.O:
                    e.Handled = true;
                    await OpenFilesAsync();
                    break;

                case Key.S when shift:
                    e.Handled = true;
                    await SaveActiveAsync(forceDialog: true);
                    break;

                case Key.S:
                    e.Handled = true;
                    await SaveActiveAsync(forceDialog: false);
                    break;

                case Key.W:
                    e.Handled = true;
                    CloseActiveTab();
                    break;

                case Key.F:
                    e.Handled = true;
                    ActiveView?.ShowFind(replace: false);
                    break;

                case Key.H:
                    e.Handled = true;
                    ActiveView?.ShowFind(replace: true);
                    break;

                case Key.L:
                    e.Handled = true;
                    ActiveView?.SelectCurrentLine();
                    break;

                case Key.OemPlus:
                case Key.Add:
                    e.Handled = true;
                    Zoom(+0.1d);
                    break;

                case Key.OemMinus:
                case Key.Subtract:
                    e.Handled = true;
                    Zoom(-0.1d);
                    break;

                case Key.D0:
                case Key.NumPad0:
                    e.Handled = true;
                    Zoom(0d);
                    break;
            }
        });

    /// <summary>关闭已确认（保存流程在异步中完成过一轮，二次进入时直接放行）。</summary>
    private bool _closeConfirmed;

    private void OnWindowClosing(object sender, CancelEventArgs e) =>
        RunGuarded(nameof(OnWindowClosing), async () =>
        {
            // 无论最终是否取消关闭，都先记下当前会话（下次启动恢复）。
            SaveSession();

            if (_closeConfirmed)
            {
                return;
            }

            var dirty = _entries.Where(entry => entry.Model.IsDirty).ToList();
            if (dirty.Count == 0)
            {
                return;
            }

            var result = MessageBox.Show(
                this,
                $"有 {dirty.Count} 个文档存在未保存的修改。\n\n" +
                "选择「是」逐个保存；「否」放弃修改并退出；「取消」返回继续编辑。",
                "PRT 阅读器",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            // 逐个保存要等安全模块裁决，而裁决是异步的——本事件本身是同步的，
            // 没法"边等边决定要不要取消"。WPF 的既有解法就是先取消本次关闭，
            // 等异步流程跑完再主动关一次；这也是本处理器需要 await 的原因
            // （await 的载体是 RunGuarded，见 MainWindow.Guard.cs）。
            // e.Cancel 必须在第一个 await 之前设置：await 之后事件早已处理完毕。
            e.Cancel = true;

            if (await SaveAllDirtyAsync(dirty))
            {
                _closeConfirmed = true;
                Close();
            }
        });

    /// <summary>逐个保存所有有修改的标签；全部成功返回 true。</summary>
    private async Task<bool> SaveAllDirtyAsync(List<TabEntry> dirty)
    {
        foreach (var entry in dirty)
        {
            var path = entry.Model.FilePath;
            if (string.IsNullOrEmpty(path))
            {
                var dialog = new SaveFileDialog
                {
                    Filter = FileService.Filter,
                    DefaultExt = FileService.DefaultExtension,
                    Title = "保存 PRT 文档",
                    FileName = entry.Model.Title + FileService.DefaultExtension,
                };

                if (dialog.ShowDialog(this) != true || !await SaveEntryAsync(entry, dialog.FileName))
                {
                    return false;
                }
            }
            else if (!await SaveEntryAsync(entry, path))
            {
                return false;
            }
        }
        return true;
    }
}
