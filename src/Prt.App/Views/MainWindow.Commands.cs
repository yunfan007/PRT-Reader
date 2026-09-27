using System.IO;
using System.Windows;
using Perisc.Safety;
using Prt.App.Models;
using Prt.App.Services;
using Prt.Core;
using Prt.Core.Import;
using Microsoft.Win32;
using StructLayoutKind = System.Runtime.InteropServices.LayoutKind;

namespace Prt.App.Views;

// 菜单命令（MainWindow 的 partial；D-06）
// 拆分依据：《工程改进方案》§4.8 / CRS 9.6.4 D-06；拆分**只做位置迁移**，未改行为。
public partial class MainWindow : Window
{
    // ─────────────────────────────── 菜单命令 ───────────────────────────────

    private void OnNewClick(object sender, RoutedEventArgs e) => NewDocument();

    private void OnOpenClick(object sender, RoutedEventArgs e) =>
        RunGuarded(nameof(OpenFilesAsync), () => OpenFilesAsync());

    /// <summary>
    /// 总设置页：改动**即时生效**（预览），点「确定」写入 settings.json 持久化；
    /// 取消或直接关闭窗口则回滚到打开设置页之前的状态。
    /// <para>
    /// 回滚口径：设置页自己持有草稿与初始快照，回滚时把快照交回来重放一遍
    /// （见 <see cref="ApplySettingsToUi"/>）——单一应用路径，界面不会漂移。
    /// </para>
    /// </summary>
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow { Owner = this };
        var baseline = CaptureSettingsFromUi();
        dialog.LoadFrom(SettingsStore.Current);

        // 实时预览：设置页每动一项就把整份草稿交回来应用（不落盘）。
        dialog.SettingsChanged += (_, draft) =>
        {
            ApplySettingsToUi(draft, announce: false);
            SetStatus(Localizer.T("status.settings.preview"));
        };

        var confirmed = dialog.ShowDialog() == true;

        if (!confirmed)
        {
            // 取消 / 直接关闭：把打开之前的快照重放回界面，并明确告知已放弃。
            ApplySettingsToUi(baseline, announce: false);
            SetStatus(Localizer.T("status.settings.reverted"));
            return;
        }

        // 启动图属于文件操作，只在确认后落地（取消时不会有任何文件被复制）。
        var saveError = string.Empty;
        if (dialog.ResetSplashRequested)
        {
            SettingsStore.ResetSplashImage(out saveError);
        }
        else if (dialog.PendingSplashSource is { Length: > 0 } source)
        {
            if (SettingsStore.TrySetSplashImage(source, out var error))
            {
                SetStatus(Localizer.T("status.splash.updated"));
            }
            else
            {
                MessageBox.Show(this, Localizer.T("settings.splash.failed", error), Localizer.T("app.name"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // 启动图的实际落点由 SettingsStore 决定（改名 / 清理旧文件都在那里做），
        // 回填进草稿后再整体落盘，避免「先写图、后被草稿覆盖」。
        dialog.Draft.SplashImage = SettingsStore.Current.SplashImage;

        // 语言只在「确定」后落盘；界面文字要重启才完全生效，这里明确提示。
        var languageChanged = !string.Equals(dialog.Draft.Language, baseline.Language, StringComparison.Ordinal);
        Localizer.Apply(dialog.Draft.Language);

        SettingsStore.Update(dialog.Draft);
        if (!SettingsStore.TrySave(out var settingsError))
        {
            saveError = settingsError;
        }

        ApplySettingsToUi(dialog.Draft, announce: false);

        if (saveError.Length > 0)
        {
            // 设置只落程序目录（《设计取舍》第 13 条），没有「换个地方写」的退路，
            // 所以写不进去必须当场说清楚——否则用户会以为设置已生效，重启后全丢。
            var message = Localizer.T("settings.save.failed", saveError);
            SetStatus(message);
            MessageBox.Show(this, message, Localizer.T("app.name"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetStatus(languageChanged
            ? Localizer.T("status.language.restart")
            : Localizer.T("status.settings.applied"));
    }

    /// <summary>互动教程窗口（单实例：已打开则前置，不重复开）。</summary>
    private TutorialWindow? _tutorialWindow;

    /// <summary>
    /// 打开内置**互动教程**窗口（F1）：左边选课、右边读说明与效果，示例可一键插入当前文档。
    /// 用非模态窗口——读教程时可以直接在后面的编辑器里试，不用来回开关。
    /// </summary>
    private void OnOpenTutorialClick(object sender, RoutedEventArgs e)
    {
        if (_tutorialWindow is { IsLoaded: true })
        {
            _tutorialWindow.Activate();
            return;
        }

        _tutorialWindow = new TutorialWindow(InsertTutorialSnippet, ReadActiveDocumentText) { Owner = this };
        _tutorialWindow.Closed += (_, _) => _tutorialWindow = null;
        _tutorialWindow.Show();
        SetStatus(Localizer.T("status.tutorial.opened"));
    }

    /// <summary>把教程示例插入当前文档的光标处；没有可插入的文档时返回 false。</summary>
    private bool InsertTutorialSnippet(string snippet)
    {
        var view = ActiveView;
        if (view is null)
        {
            return false;
        }

        view.InsertAtCaret(snippet);
        return true;
    }

    /// <summary>读取当前文档全文（教程练习「校验我的写法」用）；没有打开的文档时返回 null。</summary>
    internal string? ReadActiveDocumentText() => ActiveView?.Text;

    /// <summary>打开内置《PRT 作者指南》（按任务查的参考手册，仍是文档）。</summary>
    private void OnOpenAuthorGuideClick(object sender, RoutedEventArgs e) =>
        RunGuarded(nameof(OpenBundledDocumentAsync), () => OpenBundledDocumentAsync(
            SampleDocument.LocateAuthorGuide(),
            Localizer.T("doc.authorguide"),
            Localizer.T("doc.authorguide.missing")));

    /// <summary>
    /// 打开随程序分发的说明文档（作者指南等）：已在标签中打开则直接激活，否则新开标签。
    /// 找不到文件时给出状态栏提示，不弹错误框。
    /// </summary>
    private async Task OpenBundledDocumentAsync(string? path, string label, string missingMessage)
    {
        if (path is null)
        {
            SetStatus(Localizer.T("status.bundled.missing", missingMessage));
            return;
        }

        await OpenPathAsync(path); // 内部已处理「已打开则激活对应标签」。
        SetStatus(Localizer.T("status.bundled.opened", label, Path.GetFileName(path)));
    }

    /// <summary>
    /// 从 HTML 新建文档：选一个 .html/.htm 文件，转成 PRT 文本后在**新标签**中打开
    /// （不覆盖原 HTML；转换结果未保存，可另存为 .prt）。
    /// </summary>
    private void OnImportHtmlClick(object sender, RoutedEventArgs e) =>
        RunGuarded(nameof(OnImportHtmlClick), async () =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "从 HTML 新建文档",
                Filter = "网页文件 (*.html;*.htm)|*.html;*.htm|所有文件 (*.*)|*.*",
                CheckFileExists = true,
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            try
            {
                // PSS 3.8：读取经 SRT（以 PRIV-01 申报），此处不再单独申报一次，
                // 免得一次导入走两遍裁决、单次许可下连弹两次对话框。
                var read = SafeRuntime.File.ReadText(dialog.FileName, "读取 HTML 文件以导入正文");
                if (!read.Executed)
                {
                    MessageBox.Show(
                        this,
                        "「从 HTML 导入」被安全模块拒绝。\n\n对象：" + dialog.FileName +
                        "\n原因：" + read.Detail,
                        "PRT 阅读器 · 安全模块",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                var prt = HtmlImporter.Convert(read.Value ?? string.Empty);
                if (string.IsNullOrWhiteSpace(prt))
                {
                    SetStatus("这个 HTML 里没有可导入的正文内容");
                    return;
                }

                if (!EnsureTabSlot())
                {
                    return;
                }

                _untitledCounter++;
                AddTab(new DocumentTab { Text = prt, UntitledIndex = _untitledCounter });
                SetStatus("已从 HTML 导入，可另存为 PRT 文档：" + Path.GetFileName(dialog.FileName));
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "导入失败：\n" + ex.Message,
                    "PRT 阅读器",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        });

    private void OnOpenSampleClick(object sender, RoutedEventArgs e) =>
        RunGuarded(nameof(OpenSampleAsync), () => OpenSampleAsync());

    /// <summary>打开随程序分发的示例文档；找不到时退回内置示例内容。</summary>
    private async Task OpenSampleAsync()
    {
        var path = SampleDocument.LocateFeatureTour();
        if (path is null)
        {
            SetStatus("未找到随程序分发的示例文档，已改用内置示例内容");
            if (!EnsureTabSlot())
            {
                return;
            }

            _untitledCounter++;
            AddTab(new DocumentTab { Text = SampleDocument.Welcome, UntitledIndex = _untitledCounter });
            return;
        }

        await OpenPathAsync(path);
    }

    private void OnActivationClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ActivationWindow { Owner = this };
        dialog.ShowDialog();
        SetStatus("授权：" + Activation.CurrentSummary());
    }

    private void OnSafetyCenterClick(object sender, RoutedEventArgs e)
        => Services.SafetyBridge.OpenCenter(Perisc.Safety.SafetyUiPage.Audit);

    private void OnSaveClick(object sender, RoutedEventArgs e) =>
        RunGuarded(nameof(SaveActiveAsync), () => SaveActiveAsync(forceDialog: false));

    private void OnSaveAsClick(object sender, RoutedEventArgs e) =>
        RunGuarded(nameof(SaveActiveAsync), () => SaveActiveAsync(forceDialog: true));

    private void OnReloadClick(object sender, RoutedEventArgs e) =>
        RunGuarded(nameof(ReloadActiveAsync), () => ReloadActiveAsync());

    private void OnCloseTabClick(object sender, RoutedEventArgs e) => CloseActiveTab();

    private void OnCloseAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _entries.ToList())
        {
            if (!ConfirmDiscard(entry))
            {
                continue;
            }
            Tabs.Items.Remove(entry.Item);
            _entries.Remove(entry);
        }

        if (_entries.Count == 0)
        {
            // 全部关闭后落空：开完全空白的标签，不填「# 未命名文档」之类的示例内容。
            NewDocument(withWelcome: false);
        }
        else
        {
            UpdateOutline();
            UpdateProblems();
            UpdateStatusBar();
            UpdateWindowTitle();
        }
    }

    private void OnExportHtmlClick(object sender, RoutedEventArgs e) =>
        RunGuarded(nameof(ExportAsAsync), () => ExportAsAsync("html"));

    private void OnExportMarkdownClick(object sender, RoutedEventArgs e) =>
        RunGuarded(nameof(ExportAsAsync), () => ExportAsAsync("md"));

    private void OnExportPlainTextClick(object sender, RoutedEventArgs e) =>
        RunGuarded(nameof(ExportAsAsync), () => ExportAsAsync("text"));

    private void OnPrintClick(object sender, RoutedEventArgs e) =>
        RunGuarded(nameof(OnPrintClick), async () =>
        {
            // PSS 申报：使用打印机（DEV-07）。
            if (!await Services.SafetyBridge.EnsureAllowedAsync("DEV-07", "系统打印机", "打印当前文档", "打印"))
            {
                return;
            }
            PrintActiveDocument();
        });

    /// <summary>
    /// 打印当前文档：先打开分页预览（可选纸张 / 方向 / 边距、切黑白、调页眉页脚），
    /// 确认后交给系统打印对话框。打印用独立构建的文档，不影响屏幕预览。
    /// </summary>
    private void PrintActiveDocument()
    {
        var entry = ActiveEntry;
        if (entry is null)
        {
            SetStatus(Localizer.T("print.missing"));
            return;
        }

        var dialog = new PrintPreviewWindow(entry.Model.DisplayTitle, entry.View.BuildPrintableDocument)
        {
            Owner = this,
        };

        dialog.ShowDialog();
        SetStatus(dialog.Printed ? Localizer.T("status.print.done") : Localizer.T("status.print.cancelled"));
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnUndoClick(object sender, RoutedEventArgs e) => ActiveView?.Undo();

    private void OnRedoClick(object sender, RoutedEventArgs e) => ActiveView?.Redo();

    private void OnCutClick(object sender, RoutedEventArgs e) => ActiveView?.Cut();

    // 剪贴板的申报与写入收口在 DocumentView（经 SRT，DEV-05），此处只做转发——
    // 免得"命令层申报一次、视图层又申报一次"，单次许可下会连弹两次对话框。
    private void OnCopyClick(object sender, RoutedEventArgs e) => ActiveView?.Copy();

    private void OnPasteClick(object sender, RoutedEventArgs e) => ActiveView?.Paste();

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => ActiveView?.SelectAllText();

    private void OnFindClick(object sender, RoutedEventArgs e) => ActiveView?.ShowFind(replace: false);

    private void OnReplaceClick(object sender, RoutedEventArgs e) => ActiveView?.ShowFind(replace: true);

    private void OnSelectLineClick(object sender, RoutedEventArgs e) => ActiveView?.SelectCurrentLine();

    private void OnComplianceClick(object sender, RoutedEventArgs e)
        => MessageBox.Show(this, PrtCapabilities.ComplianceStatement, "PRT 符合性声明", MessageBoxButton.OK, MessageBoxImage.Information);

    private void OnShortcutsClick(object sender, RoutedEventArgs e)
    {
        const string text =
            "Ctrl+N        新建文档\n" +
            "Ctrl+O        打开文档\n" +
            "Ctrl+S        保存\n" +
            "Ctrl+Shift+S  另存为\n" +
            "Ctrl+W        关闭当前标签\n" +
            "Ctrl+F        查找\n" +
            "Ctrl+H        替换\n" +
            "F3            查找下一个\n" +
            "Ctrl+A        全选\n" +
            "Ctrl+加号     放大\n" +
            "Ctrl+减号     缩小\n" +
            "Ctrl+0        重置缩放";

        MessageBox.Show(this, text, "快捷键", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        // 三个子动作复用「帮助」菜单的既有实现：文案只有一处来源，不在此重复维护。
        new AboutWindow
        {
            Owner = this,
            ShowActivationRequested = () => OnActivationClick(this, new RoutedEventArgs()),
            ShowComplianceRequested = () => OnComplianceClick(this, new RoutedEventArgs()),
            ShowShortcutsRequested = () => OnShortcutsClick(this, new RoutedEventArgs()),
        }.ShowDialog();
    }
}
