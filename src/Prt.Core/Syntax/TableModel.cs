namespace Prt.Core.Syntax;

/// <summary>表格单元格数据的水平对齐（标准 10.1，由分隔行冒号决定）。</summary>
public enum TableAlignment
{
    Left,
    Center,
    Right,
}

/// <summary>表格单元格。</summary>
public sealed class TableCellData
{
    /// <summary>单元格原始文本（未解析行内标记）。</summary>
    public string RawText { get; set; } = string.Empty;

    /// <summary>单元格行内内容。</summary>
    public List<PrtInline> Inlines { get; } = new();

    /// <summary>跨列数（colspan）。1 表示不合并。由左侧主格承载。</summary>
    public int ColumnSpan { get; set; } = 1;

    /// <summary>跨行数（rowspan）。1 表示不合并。由上方主格承载。</summary>
    public int RowSpan { get; set; } = 1;

    /// <summary>该单元格是否已被其他合并单元格覆盖（自身不产生内容）。</summary>
    public bool IsCovered { get; set; }

    /// <summary>该单元格是否为合并标记（`++` / `^^`）。</summary>
    public bool IsMergeMarker { get; set; }
}

/// <summary>表格行。</summary>
public sealed class TableRowData
{
    public List<TableCellData> Cells { get; } = new();

    /// <summary>该行所属区域：表头 / 表体 / 表尾。</summary>
    public TableRowSection Section { get; set; } = TableRowSection.Body;

    public int Line { get; set; } = 1;

    public int Column { get; set; } = 1;
}

/// <summary>表格行所属区域（标准 10.3）。</summary>
public enum TableRowSection
{
    Header,
    Body,
    Footer,
}

/// <summary>
/// 表格模型，同时服务于简化写法（管道表，10.1）与完整写法（`::: table`，10.2/10.3）。
/// </summary>
public sealed class TableModel
{
    /// <summary>列数。</summary>
    public int ColumnCount { get; set; }

    /// <summary>各列对齐方式（长度与 ColumnCount 一致，可含 null 表示默认）。</summary>
    public List<TableAlignment?> Alignments { get; } = new();

    public List<TableRowData> HeaderRows { get; } = new();

    public List<TableRowData> BodyRows { get; } = new();

    public List<TableRowData> FooterRows { get; } = new();

    /// <summary>是否存在表尾分隔行 `|==|`。</summary>
    public bool HasFooterSeparator { get; set; }
}
