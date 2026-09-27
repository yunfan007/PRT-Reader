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

/// <summary>自检用例组：片段插入 / 包裹选区 / 选区换算 / 编辑器外壳（<see cref="SelfTest"/> 的 partial）。</summary>
internal static partial class SelfTest
{
    /// <summary>自检用例组：片段插入 / 包裹选区 / 选区换算 / 编辑器外壳。</summary>
    private static void RunEditingCases(CaseRunner runner)
    {
        // 8. 右键「插入框」：每个片段在空文档中插入后都必须能被解析器接受。
        runner.Check("插入片段可解析（语义框 / 结构块 / 基础块）", () => EditingCase8());

        // 9. 包裹选区：把选中的整行内容包进语义框，原内容完整保留且结果可解析。
        runner.Check("包裹选区为语义框", () => EditingCase9());

        // 10. 文本位置换算：「选中整行」与「空白行插入 / 非空行另起一段」的边界行为。
        runner.Check("整行选区与插入落点换算", () => EditingCase10());
    }

    /// <summary>用例 8：插入片段可解析（语义框 / 结构块 / 基础块）</summary>
    private static string EditingCase8()
    {
            var inserted = 0;
            foreach (var snippet in PrtSnippets.All)
            {
                var edit = PrtSnippets.InsertSnippet(string.Empty, 0, snippet);
                var document = PrtParser.Parse(edit.Text, new PrtOptions());
                if (document.HasErrors)
                {
                    var first = document.SortedDiagnostics.First(d => d.Severity == DiagnosticSeverity.Error);
                    throw new InvalidOperationException($"{snippet.Label}（{snippet.Id}）插入结果无法解析：{first.Format()}");
                }

                if (edit.SelectionStart < 0 || edit.SelectionStart > edit.Text.Length)
                {
                    throw new InvalidOperationException($"{snippet.Label}：编辑后选区越界");
                }

                inserted++;
            }

            return $"{inserted} 个片段插入后均无解析错误（语义框 {PrtSnippets.InGroup(PrtSnippets.GroupSemantic).Count} · " +
                   $"结构块 {PrtSnippets.InGroup(PrtSnippets.GroupStructure).Count} · " +
                   $"基础块 {PrtSnippets.InGroup(PrtSnippets.GroupBasic).Count} · " +
                   $"行内格式 {PrtSnippets.InGroup(PrtSnippets.GroupInline).Count} · " +
                   $"计算与指令 {PrtSnippets.InGroup(PrtSnippets.GroupCompute).Count}）";
    }

    /// <summary>用例 9：包裹选区为语义框</summary>
    private static string EditingCase9()
    {
            const string source = "第一行内容\r\n第二行内容\r\n\r\n后续段落";
            var snippet = PrtSnippets.ById("sem.warning")
                          ?? throw new InvalidOperationException("缺少语义框片段");
            var edit = PrtSnippets.WrapSelection(source, 0, 12, snippet);
            var document = PrtParser.Parse(edit.Text, new PrtOptions());

            if (document.HasErrors)
            {
                throw new InvalidOperationException("包裹结果无法解析：" + document.SortedDiagnostics[0].Format());
            }

            var warning = FindSemantic(document.Root.Children, SemanticKind.Warning)
                          ?? throw new InvalidOperationException("未生成 warning 语义块");
            if (warning.Children.Count == 0)
            {
                throw new InvalidOperationException("语义块内未保留原文内容");
            }

            if (!edit.Text.Contains("第一行内容", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("原文内容丢失");
            }

            return $"警告框包裹 {warning.Children.Count} 个块，编辑后选中 {edit.SelectionLength} 字符";
    }

    /// <summary>用例 10：整行选区与插入落点换算</summary>
    private static string EditingCase10()
    {
            const string text = "第一行\r\n第二行\r\n\r\n第四行";

            var (lineStart, lineLength) = PrtSnippets.WholeLineRange(text, 2);
            var line = text.Substring(lineStart, lineLength);
            if (line != "第一行\r\n")
            {
                throw new InvalidOperationException($"「选中整行」结果错误：{line.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)}");
            }

            var expanded = PrtSnippets.ExpandToWholeLines(text, 1, 2);
            if (expanded.Start != 0 || expanded.Length != lineLength - 2)
            {
                throw new InvalidOperationException($"选区扩张结果错误：[{expanded.Start},{expanded.Length})");
            }

            var snippet = PrtSnippets.ById("sem.note")
                          ?? throw new InvalidOperationException("缺少语义框片段");

            // 非空行中间插入 → 必须另起一段，围栏不能并进原行。
            var midApplied = ApplyEdit(text, PrtSnippets.InsertSnippet(text, 2, snippet));
            if (!midApplied.Contains("第一行\r\n::: note\r\n", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("非空行插入未另起一段：" + midApplied.Replace("\r", "\\r", StringComparison.Ordinal));
            }

            // 空白行插入 → 直接占用该行。
            var blankIndex = text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 2;
            var blankApplied = ApplyEdit(text, PrtSnippets.InsertSnippet(text, blankIndex, snippet));
            if (!blankApplied.Contains("第二行\r\n::: note\r\n", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("空白行插入落点错误：" + blankApplied.Replace("\r", "\\r", StringComparison.Ordinal));
            }

            if (PrtParser.Parse(blankApplied, new PrtOptions()).HasErrors)
            {
                throw new InvalidOperationException("空白行插入后解析失败：" + blankApplied.Replace("\r", "\\r", StringComparison.Ordinal));
            }

            return "整行含换行、选区扩张到行首、非空行另起段、空白行就地占用均正确";
    }
}
