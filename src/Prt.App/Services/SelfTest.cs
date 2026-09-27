using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shell;
using Perisc.Safety;
using Prt.App.Models;
using Prt.App.Rendering;
using Prt.App.Views;
using Prt.Core;
using Prt.Core.Diagnostics;
using Prt.Core.Export;
using Prt.Core.Rendering;
using Prt.Core.Syntax;

namespace Prt.App.Services;

/// <summary>
/// 启动自检：在无人工干预的前提下验证「解析 → 渲染（WPF 原生预览）→ 三格式导出」整条链路。
/// <para>
/// 用途：CI 环境或部署后的可用性验证。通过 <c>Prt.App.exe --selftest [报告路径]</c> 触发，
/// 结果写入文本文件（Windows 图形程序无控制台输出通道）。
/// 该模式不会显示任何窗口。
/// </para>
/// <para>
/// <b>文件构成（9.6.4 / D-06）</b>：用例体按主题拆到同目录的 partial——
/// <c>SelfTest.Parse.cs</c>（解析/渲染/导出/COMP 降级）、<c>SelfTest.Editing.cs</c>（片段与选区）、
/// <c>SelfTest.Activation.cs</c>（激活码与设置项）、<c>SelfTest.Shell.cs</c>（外壳与打印）、
/// <c>SelfTest.Tutorial.cs</c>（教程与结业测试）、<c>SelfTest.License.cs</c>（授权与时钟）、
/// <c>SelfTest.Audit.cs</c>（审计）、<c>SelfTest.Safety.cs</c>（申报与释放契约）。
/// 本文件只留三件东西：运行骨架（<see cref="RunAsync"/>）、用例登记器（<see cref="CaseRunner"/>）、
/// 以及各用例组共用的探测辅助。拆分的唯一目的是让"读一条用例"不再需要翻 2000 行。
/// </para>
/// <para>
/// 用例序号**刻意保留原编号**（含历史上的跳号与重复段）：它们已被《工程改进方案》
/// 与历次自检报告当作稳定标识引用，重排会使 9.6.3 要求的证据定位失效。
/// </para>
/// </summary>
internal static partial class SelfTest
{
    /// <summary>
    /// 等价性快照目录（仅拆分重构时使用；<c>PRT_SELFTEST_SNAPSHOT</c> 环境变量）。
    /// 为空表示不写快照——正常运行与 CI 都不受影响。
    /// </summary>
    private static string? _snapshotDirectory;

    /// <summary>执行自检；返回进程退出码（0 表示全部通过）。</summary>
    public static async Task<int> RunAsync(string? reportPath)
    {
        var target = string.IsNullOrWhiteSpace(reportPath)
            ? Path.Combine(Directory.GetCurrentDirectory(), "selftest-report.txt")
            : Path.GetFullPath(reportPath);

        var report = new StringBuilder();

        report.Append("PRT 阅读器 启动自检报告\n");
        report.Append("编写：软体程式部　评议通过：专家委员会\n");
        report.Append("符合性：").Append(PrtCapabilities.ComplianceStatement.Replace('\n', ' ')).Append("\n\n");

        // 等价性快照（仅拆分重构用）：设了 PRT_SELFTEST_SNAPSHOT 目录时，
        // 把三格式导出与预览渲染树的结构指纹写到该目录，供"拆分前后逐字节一致"的比对。
        _snapshotDirectory = Environment.GetEnvironmentVariable("PRT_SELFTEST_SNAPSHOT");

        // 载入随程序分发的示例文档：解析与导出两组用例共用，因此在这里读一次、按参数传进去。
        var sampleText = SampleDocument.Welcome;
        var samplePath = SampleDocument.LocateFeatureTour();
        if (samplePath is not null)
        {
            sampleText = FileService.ReadAllText(samplePath);
        }

        // 用例组按主题分文件；这里只负责"按原顺序把它们跑完"。
        // 顺序本身没有语义，但它决定了报告行的顺序——历次报告是比对基准，故保持不变。
        var runner = new CaseRunner(report);

        RunParseAndExportCases(runner, sampleText, samplePath);
        RunEditingCases(runner);
        RunActivationAndSettingsCases(runner);
        // Shell 组之后的教程 / 授权窗口用例要构造 WPF 窗口，必须回到 UI 线程（STA）。
        // 这里不能用 ConfigureAwait(false)：那会把续体排队到线程池，窗口构造会报
        // 「调用线程必须为 STA」。保持上下文捕获，让续体经 DispatcherSynchronizationContext 回主线程。
        await RunShellCasesAsync(runner);
        RunTutorialCases(runner);
        RunLicenseCases(runner);
        RunAuditCases(runner);
        await RunSafetyCasesAsync(runner).ConfigureAwait(false);

        report.Append('\n').Append(CultureInfo.InvariantCulture, $"结果：通过 {runner.Passed} 项，失败 {runner.Failed} 项。\n");
        try
        {
            // 报告落盘经 SRT（3.8）：报告路径由命令行给出，落点可能在任何地方，
            // 行为编号交给 SRT 按落点归类。自检无头模式下界面挂钩"一律允许"，
            // 所以这一步不会打断自检（见 SafetyBridge.InitializeHeadless）。
            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
            {
                SafeRuntime.File.CreateDirectory(directory, "创建自检报告目录");
            }
            var written = SafeRuntime.File.WriteText(target, report.ToString(), "写入自检报告");
            if (!written.Executed)
            {
                throw new IOException("自检报告未写出（安全模块未许可）：" + written.Detail);
            }
        }
        catch (Exception)
        {
            // 报告写不出去时不再尝试打印异常——报告本身就是它的输出通道，通道坏了没有别处可写。
            // 退出码升为 2（与"用例失败"的 1 区分），并把失败计数 +1 表示"本次运行不完整"。
            runner.NoteFailure();
            return 2;
        }

        return runner.Failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// 用例登记器：通过 / 失败计数与报告行的拼装。
    /// <para>
    /// 原先这四段是 <c>RunAsync</c> 内的局部函数，于是 <c>RunAsync</c> 本身成了约 1700 行的方法——
    /// 正是 9.6.4 / D-06 说的"超长方法"（局部函数与它所在的方法在可读性上是同一件事）。
    /// 用例体按主题移出后，"计数 + 报告"是它们唯一的公共依赖，因此提成独立类型，
    /// 而不是继续靠闭包共享。
    /// </para>
    /// </summary>
    private sealed class CaseRunner
    {
        private readonly StringBuilder _report;

        public CaseRunner(StringBuilder report) => _report = report;

        /// <summary>通过数。</summary>
        public int Passed { get; private set; }

        /// <summary>失败数。</summary>
        public int Failed { get; private set; }

        /// <summary>跑一条同步用例：用例体返回一句"实测结果"，抛异常即判失败。</summary>
        public void Check(string name, Func<string> action)
        {
            try
            {
                Record(name, action());
            }
            catch (Exception ex)
            {
                RecordFailure(name, ex);
            }
        }

        /// <summary>跑一条异步用例：需要等待界面 / 任务链的用例用这个（同步用例不必改成异步）。</summary>
        public async Task CheckAsync(string name, Func<Task<string>> action)
        {
            try
            {
                Record(name, await action());
            }
            catch (Exception ex)
            {
                RecordFailure(name, ex);
            }
        }

        /// <summary>记录一次框架自身的失败（如报告写不出去）。</summary>
        public void NoteFailure() => Failed++;

        private void Record(string name, string detail)
        {
            Passed++;
            _report.Append("[通过] ").Append(name);
            if (detail.Length > 0)
            {
                _report.Append(" —— ").Append(detail);
            }
            _report.Append('\n');
        }

        private void RecordFailure(string name, Exception ex)
        {
            Failed++;
            _report.Append("[失败] ").Append(name).Append(" —— ")
                   .Append(ex.GetType().Name).Append('：').Append(ex.Message).Append('\n');

            // 带上前几层栈帧：定位「哪个方法抛的」比只看消息有用得多。
            foreach (var frame in (ex.StackTrace ?? string.Empty)
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Take(5))
            {
                _report.Append("        ").Append(frame).Append('\n');
            }
        }
    }

    /// <summary>授权样例：只填到期判定需要的字段，其余取固定值——该用例考的是 IsExpired 的时间语义。</summary>
    private static LicenseInfo License(string tag, DateTime? expiresOn) => new()
    {
        Username = "自检",
        Email = "selftest@example.invalid",
        LevelName = "Standard",
        Level = LicenseLevel.Standard,
        ExpiresOn = expiresOn,
        Seats = 1,
        IssuedBy = "自检",
        Code = "PRTL1.selftest." + tag,
    };

    // ───────────────────────────── 自检辅助 ─────────────────────────────

    /// <summary>
    /// 夹具用的 SRT 包装：自检里的文件操作同样经模块四（3.8 的门槛对"程序使用了哪个函数"生效，
    /// 不因调用方是自检而豁免）。包装把"未执行"直接抛成用例失败——
    /// 自检无头模式下界面挂钩一律允许，出现未执行就说明夹具自己不成立，不该被静默跳过。
    /// </summary>
    private static T Fixture<T>(SafeResult<T> result)
        => result.Executed
            ? result.Value!
            : throw new InvalidOperationException("夹具操作未经 SRT 执行：" + result.Code + " " + result.Detail);

    /// <inheritdoc cref="Fixture{T}(SafeResult{T})"/>
    private static void Fixture(SafeResult result)
    {
        if (!result.Executed)
        {
            throw new InvalidOperationException("夹具操作未经 SRT 执行：" + result.Code + " " + result.Detail);
        }
    }

    /// <summary>递归删除临时目录；失败不影响结论（自检不因清理失败而判负）。</summary>
    private static void DeleteTreeQuietly(string directory)
    {
        try
        {
            if (Fixture(SafeRuntime.File.DirectoryExists(directory)))
            {
                SafeRuntime.File.DeleteDirectory(directory, recursive: true, reason: "清理自检临时目录");
            }
        }
        catch
        {
            // 吞掉的是"临时目录删不掉"（文件仍被句柄占着 / 权限）；降级到"留着，由系统临时目录清理"。
            // 何时应传播：不需要——清理与否不改变任何用例的结论。
        }
    }

    // ───────────────────────────── 等价性快照（拆分重构专用） ─────────────────────────────

    /// <summary>
    /// 把一段文本写入快照目录；未设 <c>PRT_SELFTEST_SNAPSHOT</c> 时不做任何事。
    /// <para>
    /// 存在的理由：D-06 拆分超大文件的验收要求是"行为等价"，而"看起来等价"不算证据。
    /// 三格式导出与预览渲染树的结构指纹是这条链路上最完整、最便宜的可比对产物：
    /// 拆分前后两次运行必须逐字节一致。
    /// </para>
    /// </summary>
    private static void Snapshot(string name, string content)
    {
        var directory = _snapshotDirectory;
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }
        Fixture(SafeRuntime.File.CreateDirectory(directory, "创建等价性快照目录"));
        Fixture(SafeRuntime.File.WriteText(Path.Combine(directory, name), content, "写入等价性快照"));
    }

    /// <summary>写预览渲染树的结构指纹：块类型 / 行内类型序列 / 纯文本（逐块递归）。</summary>
    private static void SnapshotPreview(string name, PreviewResult result)
    {
        if (string.IsNullOrEmpty(_snapshotDirectory))
        {
            return;
        }
        var sb = new StringBuilder();
        WalkBlocks(result.Document.Blocks, sb, 0);
        sb.Append("anchors=").Append(result.Anchors.Count).Append('\n');
        Snapshot(name, sb.ToString());
    }

    /// <summary>
    /// 遍历块树并写出指纹。
    /// 块类型覆盖了容器（Section / List / Table）与内嵌控件（BlockUIContainer 的子元素类型名）——
    /// 后者正是"布局块借助原生控件实现"这条约束的落点，必须进入快照。
    /// </summary>
    private static void WalkBlocks(System.Windows.Documents.BlockCollection blocks, StringBuilder sb, int depth)
    {
        var indent = new string(' ', depth * 2);
        foreach (var block in blocks)
        {
            sb.Append(indent).Append(block.GetType().Name).Append('|').Append(BlockText(block)).Append('\n');
            switch (block)
            {
                case System.Windows.Documents.Paragraph paragraph:
                    // 行内类型序列：捕获加粗 / 斜体 / 链接 / 内嵌控件这类"排版结构"，
                    // 只比纯文本会漏掉整类回归。
                    sb.Append(indent).Append("  inlines|")
                      .Append(string.Join(",", paragraph.Inlines.Select(i => i.GetType().Name)))
                      .Append('\n');
                    break;
                case System.Windows.Documents.Section section:
                    WalkBlocks(section.Blocks, sb, depth + 1);
                    break;
                case System.Windows.Documents.List list:
                    foreach (var item in list.ListItems)
                    {
                        WalkBlocks(item.Blocks, sb, depth + 1);
                    }
                    break;
                case System.Windows.Documents.Table table:
                    foreach (var group in table.RowGroups)
                    {
                        foreach (var row in group.Rows)
                        {
                            foreach (var cell in row.Cells)
                            {
                                WalkBlocks(cell.Blocks, sb, depth + 1);
                            }
                        }
                    }
                    break;
                case System.Windows.Documents.BlockUIContainer ui:
                    sb.Append(indent).Append("  UI|")
                      .Append(ui.Child?.GetType().FullName ?? "null").Append('\n');
                    break;
            }
        }
    }

    /// <summary>块的纯文本（换行显式转义，避免快照自身的行结构被内容搅乱）。</summary>
    private static string BlockText(System.Windows.Documents.Block block)
    {
        var text = new System.Windows.Documents.TextRange(block.ContentStart, block.ContentEnd).Text;
        return text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
    }

    /// <summary>把一次编辑结果落到原文上，得到编辑后的完整文本。</summary>
    private static string ApplyEdit(string text, EditResult edit)
        => text[..edit.Start] + edit.Text + text[(edit.Start + edit.Length)..];

    /// <summary>在块的子树里查找指定类型的语义块。</summary>
    private static SemanticBlock? FindSemantic(IEnumerable<PrtBlock> blocks, SemanticKind kind)
    {
        foreach (var block in blocks)
        {
            if (block is SemanticBlock semantic && semantic.Kind == kind)
            {
                return semantic;
            }

            var found = FindSemantic(ChildrenOf(block), kind);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>取块的子块序列（非容器块返回空）。</summary>
    private static IEnumerable<PrtBlock> ChildrenOf(PrtBlock block) => block switch
    {
        DocumentBlock document => document.Children,
        SectionBlock section => section.Children,
        SemanticBlock semantic => semantic.Children,
        QuoteBlock quote => quote.Children,
        SummaryBlock summary => summary.Children,
        FigureBlock figure => figure.Children,
        LayoutBlock layout => layout.Children,
        ColumnBlock column => column.Children,
        TabBlock tab => tab.Children,
        TextStyleBlock style => style.Children,
        AlignBlock align => align.Children,
        ListItemBlock item => item.Children,
        _ => Array.Empty<PrtBlock>(),
    };

    /// <summary>统计渲染结果中真正加载出来的图片元素数量（沿块容器与布局控件递归）。</summary>
    private static int CountImages(IEnumerable<System.Windows.Documents.Block> blocks)
    {
        var count = 0;
        foreach (var block in blocks)
        {
            switch (block)
            {
                case System.Windows.Documents.BlockUIContainer { Child: { } child }:
                    count += CountImagesInVisual(child);
                    break;

                case System.Windows.Documents.Section section:
                    count += CountImages(section.Blocks);
                    break;

                case System.Windows.Documents.List list:
                    foreach (var item in list.ListItems)
                    {
                        count += CountImages(item.Blocks);
                    }
                    break;
            }
        }
        return count;
    }

    /// <summary>沿可视化树向上找到最近的指定类型祖先；不在树上或没有匹配祖先时返回 null。</summary>
    private static T? FindVisualAncestor<T>(DependencyObject? node)
        where T : DependencyObject
    {
        for (var current = node; current is not null;)
        {
            var parent = VisualTreeHelper.GetParent(current);
            if (parent is T match)
            {
                return match;
            }

            current = parent;
        }

        return null;
    }

    private static int CountImagesInVisual(System.Windows.DependencyObject element)
    {
        if (element is System.Windows.Controls.Image)
        {
            return 1;
        }

        if (element is System.Windows.Controls.Border { Child: { } borderChild })
        {
            return CountImagesInVisual(borderChild);
        }

        if (element is System.Windows.Controls.Panel panel)
        {
            var count = 0;
            foreach (System.Windows.UIElement child in panel.Children)
            {
                count += CountImagesInVisual(child);
            }
            return count;
        }

        return 0;
    }
}
