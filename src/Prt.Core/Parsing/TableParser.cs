using System.Globalization;
using System.Text.RegularExpressions;
using Prt.Core.Diagnostics;
using Prt.Core.Syntax;

namespace Prt.Core.Parsing;

/// <summary>
/// 表格解析器，覆盖简化写法（管道表，标准 10.1）与完整写法（标准 10.2 / 10.3）：
/// 单元格合并（`++` / `++N` 跨列、`^^` / `^^N` 跨行）、多行表头、表尾分隔（`|==|`）。
/// </summary>
internal static class TableParser
{
    private static readonly Regex ColumnMergePattern = new(@"^\+\+(\d+)?$", RegexOptions.Compiled);
    private static readonly Regex RowMergePattern = new(@"^\^\^(\d+)?$", RegexOptions.Compiled);

    /// <summary>
    /// 取合并标记里的跨度数字（D-18）：缺失或不可解析时回落到 <paramref name="fallback"/>，
    /// 不抛异常。合并跨度是**文档内容**（外部输入），必须按可失败解析处理。
    /// </summary>
    private static int ParseSpan(Group group, int fallback)
        => group.Success && int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    /// <summary>判断某一行是否可作为表格数据行（含竖线且非空）。</summary>
    public static bool LooksLikeTableRow(string line)
    {
        var t = line.Trim();
        return t.Contains('|', StringComparison.Ordinal) && t.Length > 1;
    }

    /// <summary>判断某一行是否为表尾分隔行 `|==|`（标准 10.3）。</summary>
    public static bool IsFooterSeparatorRow(string line)
    {
        var compact = line
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("\t", string.Empty, StringComparison.Ordinal)
            .Trim();
        return compact == "|==|";
    }

    /// <summary>
    /// 解析表格。行序列中应已包含分隔行（`| :--- |`）。
    /// </summary>
    public static TableModel? Parse(
        IReadOnlyList<(string Text, int Line)> rows,
        PrtOptions options,
        DiagnosticBag diagnostics)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        var separatorIndex = -1;
        var footerIndex = -1;
        for (var i = 0; i < rows.Count; i++)
        {
            if (separatorIndex < 0 && ParsingHelpers.IsTableSeparatorRow(rows[i].Text))
            {
                separatorIndex = i;
                continue;
            }
            if (IsFooterSeparatorRow(rows[i].Text))
            {
                footerIndex = i;
            }
        }

        var model = new TableModel();

        // 组装所有行并标记区域。
        var allRows = new List<TableRowData>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (i == footerIndex)
            {
                model.HasFooterSeparator = true;
                continue;
            }

            // 对齐分隔行（`| :-- | :-: |`）本身不是数据行：仅用于推导对齐方式，
            // 不得进入 HeaderRows / BodyRows（标准 10.1 / 10.3）。
            if (i == separatorIndex)
            {
                continue;
            }

            var row = new TableRowData { Line = rows[i].Line, Column = 1 };
            if (separatorIndex < 0)
            {
                row.Section = TableRowSection.Body;
            }
            else if (i < separatorIndex)
            {
                row.Section = TableRowSection.Header;
            }
            else if (footerIndex >= 0 && i > footerIndex)
            {
                row.Section = TableRowSection.Footer;
            }
            else if (footerIndex < 0 && i > separatorIndex)
            {
                row.Section = TableRowSection.Body;
            }
            else
            {
                row.Section = TableRowSection.Body;
            }

            foreach (var cellText in ParsingHelpers.SplitTableRow(rows[i].Text))
            {
                row.Cells.Add(new TableCellData { RawText = cellText });
            }
            allRows.Add(row);
        }

        if (allRows.Count == 0)
        {
            return null;
        }

        // 列数与对齐。
        var columnCount = 0;
        foreach (var row in allRows)
        {
            columnCount = Math.Max(columnCount, row.Cells.Count);
        }
        if (columnCount > options.MaxTableColumns)
        {
            columnCount = options.MaxTableColumns;
        }
        model.ColumnCount = columnCount;

        if (separatorIndex >= 0 && separatorIndex < rows.Count)
        {
            var alignments = ParsingHelpers.ParseAlignments(ParsingHelpers.SplitTableRow(rows[separatorIndex].Text));
            for (var c = 0; c < columnCount; c++)
            {
                model.Alignments.Add(c < alignments.Count ? alignments[c] : null);
            }
        }
        else
        {
            for (var c = 0; c < columnCount; c++)
            {
                model.Alignments.Add(null);
            }
        }

        // 补齐/裁剪单元格数量，保证网格规整（标准 10.3 边界规则）。
        foreach (var row in allRows)
        {
            if (row.Cells.Count < columnCount)
            {
                while (row.Cells.Count < columnCount)
                {
                    row.Cells.Add(new TableCellData { RawText = string.Empty });
                }
            }
            else if (row.Cells.Count > columnCount)
            {
                ParsingHelpers.Report(
                    diagnostics,
                    options.Strict,
                    PrtDiagnosticCodes.TableStructure,
                    "表格行的单元格数超过表头列数，多余单元格已忽略",
                    row.Line,
                    1);
                row.Cells.RemoveRange(columnCount, row.Cells.Count - columnCount);
            }
        }

        ApplyMerges(allRows, options, diagnostics);

        // 解析行内内容（跳过合并标记与被覆盖单元格）。
        foreach (var row in allRows)
        {
            foreach (var cell in row.Cells)
            {
                if (cell.IsMergeMarker || cell.IsCovered)
                {
                    continue;
                }
                if (cell.RawText.Contains('|', StringComparison.Ordinal))
                {
                    ParsingHelpers.Report(
                        diagnostics,
                        options.Strict,
                        PrtDiagnosticCodes.TableStructure,
                        "单元格包含未转义的竖线，可能造成列错位（请使用 `\\|`）",
                        row.Line,
                        1);
                }
                cell.Inlines.AddRange(InlineParser.Parse(cell.RawText, row.Line, row.Column, options, diagnostics));
            }
        }

        // 按区域切分。
        foreach (var row in allRows)
        {
            switch (row.Section)
            {
                case TableRowSection.Header:
                    model.HeaderRows.Add(row);
                    break;
                case TableRowSection.Footer:
                    model.FooterRows.Add(row);
                    break;
                default:
                    model.BodyRows.Add(row);
                    break;
            }
        }

        // 若表头行为空而表体非空，按标准宜把首行视作表头（容错）。
        if (model.HeaderRows.Count == 0 && model.BodyRows.Count > 0)
        {
            model.HeaderRows.Add(model.BodyRows[0]);
            model.BodyRows.RemoveAt(0);
        }

        return model;
    }

    /// <summary>
    /// 应用跨列 / 跨行合并，并执行标准 10.3 的边界规则校验。
    /// <para>
    /// 语义（已确认）：单元格为独立 `++` 时，其左侧主格向右多跨 1 列；
    /// `++N` 时主格向右再多跨 N 列。`^^` / `^^N` 同理向下。
    /// </para>
    /// </summary>
    private static void ApplyMerges(
        List<TableRowData> rows,
        PrtOptions options,
        DiagnosticBag diagnostics)
    {
        var headerRowCount = rows.Count(r => r.Section == TableRowSection.Header);

        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            for (var c = 0; c < row.Cells.Count; c++)
            {
                var cell = row.Cells[c];
                var text = cell.RawText.Trim();

                var colMatch = ColumnMergePattern.Match(text);
                if (colMatch.Success)
                {
                    cell.IsMergeMarker = true;
                    cell.IsCovered = true;

                    // D-18：即便正则已把取值限定为 \d+ 也走 TryParse —— 位数溢出（如 `++99999999999999`）
                    // 会让 int.Parse 抛异常；解析路径上抛异常等于让整篇文档解析失败，代价与收益完全不成比例。
                    var span = ParseSpan(colMatch.Groups[1], 1);

                    var master = FindColumnMaster(rows, r, c);
                    if (master is null)
                    {
                        Report(diagnostics, options,
                            "跨列合并标记 `++` 左侧不存在可合并的主单元格，已按格式错误处理", row.Line);
                        continue;
                    }
                    if (master.IsCovered || master.IsMergeMarker)
                    {
                        Report(diagnostics, options,
                            "禁止链式合并：主单元格本身已被合并（标准 10.3）", row.Line);
                        continue;
                    }
                    master.ColumnSpan += span;
                    continue;
                }

                var rowMatch = RowMergePattern.Match(text);
                if (rowMatch.Success)
                {
                    cell.IsMergeMarker = true;
                    cell.IsCovered = true;

                    if (row.Section == TableRowSection.Footer)
                    {
                        Report(diagnostics, options,
                            "表尾不参与跨行合并：`^^` 不得出现在表尾（标准 10.3）", row.Line);
                        continue;
                    }

                    var span = ParseSpan(rowMatch.Groups[1], 1);
                    var master = FindRowMaster(rows, r, c);
                    if (master is null)
                    {
                        Report(diagnostics, options,
                            "跨行合并标记 `^^` 上方不存在可合并的主单元格，已按格式错误处理", row.Line);
                        continue;
                    }
                    if (master.Value.Cell.IsCovered || master.Value.Cell.IsMergeMarker)
                    {
                        Report(diagnostics, options,
                            "禁止链式合并：主单元格本身已被合并（标准 10.3）", row.Line);
                        continue;
                    }

                    // 表头区内的合并不得跨越到表体。
                    if (row.Section == TableRowSection.Header
                        && master.Value.MasterRow + master.Value.Cell.RowSpan + span - 1 >= headerRowCount)
                    {
                        Report(diagnostics, options,
                            "表头区内的合并不得跨越到表体（标准 10.3）", row.Line);
                        continue;
                    }

                    master.Value.Cell.RowSpan += span;
                }
            }
        }
    }

    private static TableCellData? FindColumnMaster(List<TableRowData> rows, int rowIndex, int cellIndex)
    {
        var row = rows[rowIndex];
        for (var i = cellIndex - 1; i >= 0; i--)
        {
            var candidate = row.Cells[i];
            if (candidate.IsMergeMarker)
            {
                continue;
            }
            return candidate;
        }
        return null;
    }

    private static (TableCellData Cell, int MasterRow)? FindRowMaster(List<TableRowData> rows, int rowIndex, int cellIndex)
    {
        for (var r = rowIndex - 1; r >= 0; r--)
        {
            if (cellIndex >= rows[r].Cells.Count)
            {
                continue;
            }
            var candidate = rows[r].Cells[cellIndex];
            if (candidate.IsMergeMarker)
            {
                continue;
            }
            return (candidate, r);
        }
        return null;
    }

    private static void Report(DiagnosticBag diagnostics, PrtOptions options, string message, int line)
    {
        ParsingHelpers.Report(diagnostics, options.Strict, PrtDiagnosticCodes.TableMergeViolation, message, line, 1);
    }
}
