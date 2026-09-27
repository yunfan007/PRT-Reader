using System.IO;
using System.Windows;
using System.Windows.Controls;
using Perisc.Safety;
using Prt.App.Models;
using Prt.App.Services;
using Microsoft.Win32;
using StructLayoutKind = System.Runtime.InteropServices.LayoutKind;

namespace Prt.App.Views;

// 文件打开 / 拖放导入 / 三格式导出（MainWindow 的 partial；D-06）
// 拆分依据：《工程改进方案》§4.8 / CRS 9.6.4 D-06；拆分**只做位置迁移**，未改行为。
public partial class MainWindow : Window
{
    // ─────────────────────────────── 文件 ───────────────────────────────

    private async Task OpenFilesAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = FileService.Filter,
            Multiselect = true,
            Title = "打开 PRT 文档",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        foreach (var fileName in dialog.FileNames)
        {
            await OpenPathAsync(fileName);
        }
    }

    /// <summary>
    /// 打开一个文档路径；成功（含"该文档已打开，切到它"）返回 true。
    /// <para>
    /// <paramref name="quiet"/> 为 true 时不弹任何失败提示，只回报成败——
    /// 用于会话恢复与命令行参数这两条路径：那里的路径不是用户此刻点选的，
    /// 文件早被删掉属正常情况，弹一串错误框只会打断启动。
    /// </para>
    /// <para>
    /// 早先这两条路径各自先做一次 <c>File.Exists</c> 探测再调用本方法。探测已去掉：
    /// 它与真正的读取申报同一个行为编号与对象（PRIV-01 + 同一路径），
    /// 在单次许可下会变成"探测用掉许可、读取再弹一次窗"；现在由读取本身回答"在不在"。
    /// </para>
    /// </summary>
    private async Task<bool> OpenPathAsync(string path, bool quiet = false)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var existing = _entries.FirstOrDefault(
                entry => string.Equals(entry.Model.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                Tabs.SelectedItem = existing.Item;
                SetStatus("该文档已打开：" + fullPath);
                return true;
            }

            // PSS 3.8：读取文档的申报由 SRT 完成（FileService → SafeRuntime.File.ReadBytes，以 PRIV-01 申报）。
            // 此处**不再**单独申报一次：否则一次「打开」会走两遍裁决，用户只给了单次许可时
            // 第二遍必然落空、连弹两个对话框；申报点重复也违背"收口到一处"的初衷。
            //
            // 读取整篇文本是 I/O 密集操作，落到后台线程避免阻塞 UI（大文档尤其明显）；
            // 异常随 await 正常传播到外层 try/catch。
            var text = await Task.Run(() => FileService.ReadAllText(fullPath));

            var model = new DocumentTab
            {
                FilePath = fullPath,
                Text = text,
            };

            if (!EnsureTabSlot())
            {
                return false;
            }

            AddTab(model);
            SetStatus("已打开：" + fullPath);
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            // SRT 裁决为「不执行」时由 FileService 抛出本条（3.8 ③ 同失败语义：deny / 超时 /
            // 不可用一律不读、不部分读）。单独接住是为了给出"这是安全裁决、不是磁盘故障"的提示。
            if (!quiet)
            {
                MessageBox.Show(
                    this,
                    "「打开文档」被安全模块拒绝。\n\n" + ex.Message,
                    "PRT 阅读器 · 安全模块",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            return false;
        }
        catch (Exception ex)
        {
            if (!quiet)
            {
                MessageBox.Show(
                    this,
                    "打开失败：\n" + ex.Message,
                    "PRT 阅读器",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            return false;
        }
    }

    /// <summary>由命令行参数（或系统「打开方式」）触发的批量打开。</summary>
    public async Task OpenPathsFromCommandLineAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            await OpenPathAsync(path, quiet: true);
        }
    }

    // ─────────────────────────────── 拖放 ───────────────────────────────

    /// <summary>
    /// 仅当拖入的是文件时接管拖拽事件；其余情况（如编辑器内部拖动选中文本）放行，
    /// 不影响 TextBox 自带的文字拖放。
    /// </summary>
    private void OnWindowPreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnWindowDrop(object sender, DragEventArgs e) =>
        RunGuarded(nameof(OnWindowDrop), async () =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
            {
                return;
            }

            e.Handled = true;
            await OpenPathsFromCommandLineAsync(paths);
        });

    private async Task<bool> SaveActiveAsync(bool forceDialog)
    {
        var entry = ActiveEntry;
        if (entry is null)
        {
            return false;
        }

        var path = entry.Model.FilePath;
        if (forceDialog || string.IsNullOrEmpty(path))
        {
            var dialog = new SaveFileDialog
            {
                Filter = FileService.Filter,
                DefaultExt = FileService.DefaultExtension,
                Title = "保存 PRT 文档",
                FileName = path is null ? "未命名.prt" : Path.GetFileName(path),
                InitialDirectory = FileService.DirectoryOf(path),
            };

            if (dialog.ShowDialog(this) != true)
            {
                return false;
            }

            path = dialog.FileName;
        }

        return await SaveEntryAsync(entry, path!);
    }

    // 不再标 async：安全裁决已在 SRT 的调用栈内完成，本方法自身没有可等待点。
    // 返回 Task 只为与调用点（SaveActiveAsync / SaveAsAsync）的既有契约保持一致。
    private Task<bool> SaveEntryAsync(TabEntry entry, string path)
    {
        // PSS 3.8：落盘的申报由 SRT 完成（FileService → SafeRuntime.File）。
        // 这里不再先申报一次再裸写：申报点重复会让一次保存走两遍裁决，
        // 用户只给单次许可时第二遍必然落空。
        try
        {
            // 只有真的执行了才更新界面状态：SRT 返回未执行时 FileService 会抛，
            // 界面上就不会出现"标签已变干净、但磁盘上还是旧内容"的假象（3.8 ③ 不得静默成功）。
            FileService.WriteAllText(path, entry.View.Text);
            entry.Model.FilePath = path;
            entry.Model.IsDirty = false;
            entry.View.MarkClean();
            entry.HeaderText.Text = entry.Model.DisplayTitle;
            SetStatus("已保存：" + path);
            UpdateWindowTitle();
            return Task.FromResult(true);
        }
        catch (UnauthorizedAccessException ex)
        {
            // 安全裁决为"不执行"（deny / 超时 / 模块不可用），区别于磁盘故障。
            MessageBox.Show(
                this,
                "「保存文档」被安全模块拒绝。\n\n" + ex.Message,
                "PRT 阅读器 · 安全模块",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return Task.FromResult(false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "保存失败：\n" + ex.Message,
                "PRT 阅读器",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return Task.FromResult(false);
        }
    }

    // 不再标 async：读取的申报已收口到 SRT（FileService → SafeRuntime.File.ReadBytes）。
    // 返回值仍为 Task，与 RunGuarded 的契约一致。
    private Task ReloadActiveAsync()
    {
        var entry = ActiveEntry;
        if (entry?.Model.FilePath is null)
        {
            SetStatus("当前文档尚未保存到磁盘，无法重新载入");
            return Task.CompletedTask;
        }

        if (entry.Model.IsDirty)
        {
            var result = MessageBox.Show(
                this,
                "重新载入将放弃未保存的修改，是否继续？",
                "PRT 阅读器",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
            {
                return Task.CompletedTask;
            }
        }

        // PSS 3.8：此处不再单独申报一次读取——FileService 已由 SRT 申报，
        // 重复申报会让一次「重新载入」走两遍裁决。
        try
        {
            entry.View.Reload(FileService.ReadAllText(entry.Model.FilePath));
            entry.HeaderText.Text = entry.Model.DisplayTitle;
            SetStatus("已重新载入：" + entry.Model.FilePath);
            UpdateWindowTitle();
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show(
                this,
                "「重新载入」被安全模块拒绝。\n\n" + ex.Message,
                "PRT 阅读器 · 安全模块",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "重新载入失败：\n" + ex.Message, "PRT 阅读器", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        return Task.CompletedTask;
    }

    // ─────────────────────────────── 导出 ───────────────────────────────

    // 不再标 async：导出落盘已收口到 SRT，安全裁决在调用栈内同步完成（库侧等答复、
    // 界面侧由授权弹窗负责），本方法自身没有可等待点。返回 Task 只为与 RunGuarded 的契约一致。
    private Task ExportAsAsync(string kind)
    {
        var entry = ActiveEntry;
        if (entry is null)
        {
            return Task.CompletedTask;
        }

        // 授权门槛：按等级开放导出格式（免费版纯文本；标准版起 HTML；专业版起 Markdown）。
        var level = Activation.CurrentLevel;
        if (kind == "md" && level < LicenseLevel.Professional)
        {
            MessageBox.Show(
                this,
                "导出为 Markdown 是 Professional（专业版）功能。\n\n当前授权：" + Activation.CurrentSummary() +
                "\n请在「帮助 → 激活与授权…」中升级。",
                "PRT 阅读器",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return Task.CompletedTask;
        }

        if (kind == "html" && level < LicenseLevel.Standard)
        {
            MessageBox.Show(
                this,
                "导出为 HTML 是 Standard（标准版）及以上功能。\n\n当前授权：" + Activation.CurrentSummary() +
                "\n请在「帮助 → 激活与授权…」中升级；免费版仍可导出纯文本。",
                "PRT 阅读器",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return Task.CompletedTask;
        }

        var (filter, extension, title) = kind switch
        {
            "html" => ("HTML 文件 (*.html)|*.html", ".html", "导出为 HTML"),
            "md" => ("Markdown 文件 (*.md)|*.md", ".md", "导出为 Markdown"),
            _ => ("文本文件 (*.txt)|*.txt", ".txt", "导出为纯文本"),
        };

        var dialog = new SaveFileDialog
        {
            Filter = filter,
            DefaultExt = extension,
            Title = title,
            FileName = Path.GetFileNameWithoutExtension(entry.Model.Title) + extension,
            InitialDirectory = FileService.DirectoryOf(entry.Model.FilePath),
        };

        if (dialog.ShowDialog(this) != true)
        {
            return Task.CompletedTask;
        }

        var content = kind switch
        {
            "html" => entry.View.ToHtml(),
            "md" => entry.View.ToMarkdown(),
            _ => entry.View.ToPlainText(),
        };

        // PSS 3.8：导出落盘经 SRT —— 申报 → 裁决 → 执行 → 审计四步在同一次调用内完成，
        // 行为编号由 SRT 按落点归类（用户目录 FS-02、程序目录 FS-01、可执行文件 FS-06、其余 FS-03）。
        // 原先的「EnsureAllowedAsync + 裸 File.WriteAllText + RecordExecuted」三处手写等价于此，
        // 但那是"记得就写"的纪律；收口到 SRT 后，漏申报在接口形状上就写不出来。
        var written = SafeRuntime.File.WriteText(dialog.FileName, content, "导出文档内容");
        if (written.Executed)
        {
            SetStatus("已导出：" + dialog.FileName);
        }
        else if (!Services.SafetyBridge.Quiet)
        {
            MessageBox.Show(
                this,
                $"「导出文档」被安全模块拒绝。\n\n对象：{dialog.FileName}\n原因：{written.Detail}",
                "PRT 阅读器 · 安全模块",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        return Task.CompletedTask;
    }
}
