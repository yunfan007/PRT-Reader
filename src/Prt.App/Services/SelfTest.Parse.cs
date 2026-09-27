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

/// <summary>自检用例组：解析 / 渲染 / 导出 / COMP 降级（<see cref="SelfTest"/> 的 partial）。</summary>
internal static partial class SelfTest
{
    /// <summary>自检用例组：解析 / 渲染 / 导出 / COMP 降级。</summary>
    private static void RunParseAndExportCases(CaseRunner runner, string sampleText, string? samplePath)
    {
        // 1. 解析随程序分发的示例文档（严格模式）。
        PrtDocument? parsed = null;
        runner.Check("解析示例文档（严格模式）", () =>
        {
            parsed = PrtParser.Parse(sampleText, new PrtOptions());
            if (parsed.HasErrors)
            {
                var first = parsed.SortedDiagnostics.First(d => d.Severity == DiagnosticSeverity.Error);
                throw new InvalidOperationException("存在解析错误：" + first.Format());
            }
            return $"错误 0，警告 {parsed.Diagnostics.WarningCount}，" +
                   $"目录 {parsed.Structure.Toc.Count} 项，脚注 {parsed.Structure.Footnotes.Count} 条，" +
                   $"引用目标 {parsed.References.Targets.Count} 个" +
                   (samplePath is null ? "（使用内置示例内容）" : string.Empty);
        });

        // 2. 渲染预览（WPF FlowDocument）——逐个内置主题。
        foreach (var themeName in new[] { "default", "dark", "print", "accessible" })
        {
            runner.Check($"渲染预览（主题 {themeName}）", () =>
            {
                var document = parsed ?? PrtParser.Parse(sampleText, new PrtOptions());
                var theme = PrtTheme.BuiltIn(themeName, out var resolved);
                var builder = new PreviewBuilder(new PreviewStyle(theme));
                var result = builder.Build(document);

                if (result.Document.Blocks.Count == 0)
                {
                    throw new InvalidOperationException("预览文档为空");
                }

                SnapshotPreview($"preview-{themeName}.txt", result);

                return $"主题 {resolved}，顶层块 {result.Document.Blocks.Count} 个，锚点 {result.Anchors.Count} 个";
            });
        }

        // 3. 主题叠加：文档内 `::: theme` 覆盖（标准 12.2）。
        runner.Check("主题解析（跟随文档声明）", () => ParseAndExportCase3(sampleText, parsed));

        // 4. 语义块渲染（标准 8.3）：12 种类型都必须渲染为带底色与侧边线的提示框，
        //    且正文可见——避免「语义块在预览中不可见」这类回归。
        runner.Check("语义块渲染（12 类）", () => ParseAndExportCase4());

        // 5. 相对路径图片（标准 9.4 图块）：文档目录必须被渲染器采纳，
        //    否则 `::: figure` 中的相对路径图片会永远退化为占位符。
        runner.Check("相对路径图片解析", () => ParseAndExportCase5());

        // 6. 三格式导出。
        runner.Check("导出 HTML", () => ParseAndExportCase6(sampleText, parsed));

        runner.Check("导出 Markdown", () => ParseAndExportCase6b(sampleText, parsed));

        runner.Check("导出纯文本", () => ParseAndExportCase7(sampleText, parsed));

        // 7. COMP 降级行为（标准第 13 章）：插值原文输出、计算块保留源码。
        runner.Check("COMP 降级（不执行求值）", () => ParseAndExportCase7b());

        // 8. 章节渲染的「默认不自作主张编号」：未显式声明 numeration 时，
        //    普通标题不自动加「第 X 章」前缀，目录保留原标题、不改写也不拼编号。
        runner.Check("章节默认不自动编号、目录保留原标题", () => ParseAndExportCase8());

        // 9. 显式「自动排布章节」结构仍编号：整篇 meta 声明 numeration=cn 时标题恢复编号。
        runner.Check("显式 numeration（自动排布章节）仍编号", () => ParseAndExportCase9());
    }

    /// <summary>用例 3：主题解析（跟随文档声明）</summary>
    private static string ParseAndExportCase3(string sampleText, PrtDocument? parsed)
    {
            var document = parsed ?? PrtParser.Parse(sampleText, new PrtOptions());
            var theme = PrtTheme.Resolve(document.Metadata.Theme, document.ThemeBlocks, out var resolved);
            return $"生效主题 {resolved}，变量 {theme.Variables.Count} 项";
    }

    /// <summary>用例 4：语义块渲染（12 类）</summary>
    private static string ParseAndExportCase4()
    {
            var kinds = new[]
            {
                "note", "warning", "danger", "attention", "example", "definition",
                "quote", "comment", "info", "tip", "result", "task", "todo",
            };

            var source = new StringBuilder();
            foreach (var kind in kinds)
            {
                if (source.Length > 0)
                {
                    source.Append('\n');
                }
                source.Append("::: ").Append(kind).Append('\n');
                source.Append("语义块内容 ").Append(kind).Append('\n');
                source.Append("::: end ").Append(kind).Append('\n');
            }

            var document = PrtParser.Parse(source.ToString(), new PrtOptions());
            if (document.HasErrors)
            {
                throw new InvalidOperationException("语义块用例存在解析错误：" + document.SortedDiagnostics[0].Format());
            }

            var style = new PreviewStyle(PrtTheme.BuiltIn("default", out _));
            var result = new PreviewBuilder(style).Build(document);

            var callouts = result.Document.Blocks
                .OfType<System.Windows.Documents.Section>()
                .ToList();
            if (callouts.Count != kinds.Length)
            {
                throw new InvalidOperationException($"期望 {kinds.Length} 个提示框，实际 {callouts.Count} 个");
            }

            foreach (var callout in callouts)
            {
                if (callout.Background is not System.Windows.Media.SolidColorBrush background
                    || background.Color == System.Windows.Media.Colors.Transparent)
                {
                    throw new InvalidOperationException("提示框缺少底色");
                }
                if (callout.BorderThickness.Left < 3)
                {
                    throw new InvalidOperationException("提示框缺少侧边强调线");
                }
                if (callout.Blocks.Count < 2)
                {
                    throw new InvalidOperationException("提示框正文未渲染");
                }
            }

            return $"{callouts.Count} 个提示框均带底色、侧边线与正文";
    }

    /// <summary>用例 5：相对路径图片解析</summary>
    private static string ParseAndExportCase5()
    {
            var directory = Path.Combine(Path.GetTempPath(), "prt-selftest-" + Guid.NewGuid().ToString("N"));
            Fixture(SafeRuntime.File.CreateDirectory(directory, "创建自检临时目录"));
            var imageName = "pixel.png";
            var imagePath = Path.Combine(directory, imageName);

            // 1×1 透明 PNG。
            Fixture(SafeRuntime.File.WriteBytes(imagePath, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg=="), "写入相对路径用例的 1×1 PNG"));

            try
            {
                var document = PrtParser.Parse(
                    "::: figure id=\"fig-test\" caption=\"测试图\"\n![示意图](" + imageName + ")\n::: end figure",
                    new PrtOptions());

                var builder = new PreviewBuilder(new PreviewStyle(PrtTheme.BuiltIn("default", out _)))
                {
                    DocumentDirectory = directory,
                };
                var result = builder.Build(document);

                if (!string.Equals(builder.DocumentDirectory, directory, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("渲染过程清空了 DocumentDirectory");
                }

                if (CountImages(result.Document.Blocks) == 0)
                {
                    throw new InvalidOperationException("相对路径图片未加载");
                }

                return "相对路径按文档目录解析成功";
            }
            finally
            {
                try
                {
                    Fixture(SafeRuntime.File.DeleteDirectory(directory, recursive: true, reason: "清理自检临时目录"));
                }
                catch
                {
                    // 清理失败不影响结论。
                }
            }
    }

    /// <summary>用例 6：导出 HTML</summary>
    private static string ParseAndExportCase6(string sampleText, PrtDocument? parsed)
    {
            var document = parsed ?? PrtParser.Parse(sampleText, new PrtOptions());
            var html = HtmlRenderer.Render(document);
            if (!html.Contains("<!DOCTYPE html>", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("HTML 输出缺少文档类型声明");
            }
            Snapshot("export-html.html", html);
            return $"{html.Length} 字符";
    }

    /// <summary>用例 6：导出 Markdown</summary>
    private static string ParseAndExportCase6b(string sampleText, PrtDocument? parsed)
    {
            var document = parsed ?? PrtParser.Parse(sampleText, new PrtOptions());
            var markdown = MarkdownRenderer.Render(document);
            if (markdown.Length == 0)
            {
                throw new InvalidOperationException("Markdown 输出为空");
            }
            Snapshot("export-markdown.md", markdown);
            return $"{markdown.Length} 字符";
    }

    /// <summary>用例 7：导出纯文本</summary>
    private static string ParseAndExportCase7(string sampleText, PrtDocument? parsed)
    {
            var document = parsed ?? PrtParser.Parse(sampleText, new PrtOptions());
            var text = PlainTextRenderer.Render(document);
            if (text.Length == 0)
            {
                throw new InvalidOperationException("纯文本输出为空");
            }
            Snapshot("export-plain.txt", text);
            return $"{text.Length} 字符";
    }

    /// <summary>用例 7：COMP 降级（不执行求值）</summary>
    private static string ParseAndExportCase7b()
    {
            var document = PrtParser.Parse("值 = {{ 1 + 1 }}\n\n::: run\nprint(1);\n::: end run", new PrtOptions());
            var text = PlainTextRenderer.Render(document);
            if (!text.Contains("{{ 1 + 1 }}", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("插值未按原文降级");
            }
            if (!text.Contains("print(1);", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("计算块源码丢失");
            }
            return "插值输出原文，计算块保留源码";
    }

    /// <summary>
    /// 用例 8：章节渲染「默认不自作主张编号」。未声明 numeration 时，标题不自动加「第 X 章」前缀，
    /// 目录条目不拼编号、保留原标题并按层级列出（回归保护：防止将来退回默认 Mixed 造成显示错误）。
    /// </summary>
    private static string ParseAndExportCase8()
    {
        var source = "# 概述\n正文内容。\n\n## 背景\n细节内容。\n\n# 实现\n结束。";
        var document = PrtParser.Parse(source, new PrtOptions());
        if (document.HasErrors)
        {
            throw new InvalidOperationException("默认标题用例存在解析错误：" + document.SortedDiagnostics[0].Format());
        }

        foreach (var entry in document.Structure.Toc)
        {
            if (!string.IsNullOrWhiteSpace(entry.Number))
            {
                throw new InvalidOperationException($"目录条目被自动拼上了编号「{entry.Number}」，标题改为 {entry.Title}");
            }
        }

        var unnumberedHeadings = document.Structure.Toc
            .Select(e => e.Source)
            .OfType<HeadingBlock>()
            .Where(h => !string.IsNullOrWhiteSpace(h.AssignedNumber))
            .ToList();
        if (unnumberedHeadings.Count != 0)
        {
            throw new InvalidOperationException($"普通标题被自动编号：{string.Join("、", unnumberedHeadings.Select(h => h.AssignedNumber))}");
        }

        var titles = string.Join(" / ", document.Structure.Toc.Select(e => e.Title));
        var levels = string.Join(",", document.Structure.Toc.Select(e => e.Level));
        return $"默认不编号，目录 {document.Structure.Toc.Count} 项：{titles}（层级 {levels}）";
    }

    /// <summary>
    /// 用例 9：显式「自动排布章节」结构仍应编号——整篇 meta 声明 numeration=cn 时标题恢复编号，
    /// 验证需求里「除非用了自动排布章节的结构」这一例外分支没有被默认关闭连带破坏。
    /// </summary>
    private static string ParseAndExportCase9()
    {
        var source = "::: meta\nnumeration: cn\n::: end meta\n# 概述\n## 背景";
        var document = PrtParser.Parse(source, new PrtOptions());
        if (document.HasErrors)
        {
            throw new InvalidOperationException("显式编号用例存在解析错误：" + document.SortedDiagnostics[0].Format());
        }

        var numbered = document.Structure.Toc
            .Select(e => e.Source)
            .OfType<HeadingBlock>()
            .Where(h => !string.IsNullOrWhiteSpace(h.AssignedNumber))
            .ToList();
        if (numbered.Count == 0)
        {
            throw new InvalidOperationException("显式 numeration=cn 时标题应恢复编号");
        }

        return "标题已编号：" + string.Join("、", numbered.Select(h => h.AssignedNumber));
    }
}
