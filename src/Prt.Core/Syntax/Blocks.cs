using Prt.Core.Structure;

namespace Prt.Core.Syntax;

/// <summary>文档根节点。</summary>
public sealed class DocumentBlock : PrtBlock
{
    public List<PrtBlock> Children { get; } = new();
}

/// <summary>标题（标准 8.2，CORE）：`#` 到 `######`。</summary>
public sealed class HeadingBlock : PrtBlock
{
    /// <summary>层级 1–6。</summary>
    public int Level { get; set; } = 1;

    public List<PrtInline> Inlines { get; } = new();

    /// <summary>结构解析阶段填充的自动编号文本（隐式章节编号）。</summary>
    public string? AssignedNumber { get; set; }

    /// <summary>纯文本标题内容（用于目录与交叉引用）。</summary>
    public string PlainText { get; set; } = string.Empty;
}

/// <summary>段落（标准 8.2，CORE）。</summary>
public sealed class ParagraphBlock : PrtBlock
{
    public List<PrtInline> Inlines { get; } = new();
}

/// <summary>列表（标准 8.2，CORE 无序/有序；任务清单为 EXT）。</summary>
public sealed class ListBlock : PrtBlock
{
    public bool Ordered { get; set; }

    public int StartNumber { get; set; } = 1;

    public List<ListItemBlock> Items { get; } = new();
}

/// <summary>列表项。任务清单项以 `[ ]` / `[x]` 起始（标准 8.2，EXT）。</summary>
public sealed class ListItemBlock : PrtBlock
{
    /// <summary>是否含任务清单复选框。</summary>
    public bool IsTask { get; set; }

    /// <summary>任务是否已完成（`[x]`）。</summary>
    public bool IsChecked { get; set; }

    /// <summary>该项的行内内容。</summary>
    public List<PrtInline> Inlines { get; } = new();

    /// <summary>该项下的子块（嵌套列表或续段）。</summary>
    public List<PrtBlock> Children { get; } = new();
}

/// <summary>引用块（标准 8.2，CORE）：`&gt; `。</summary>
public sealed class QuoteBlock : PrtBlock
{
    public List<PrtBlock> Children { get; } = new();
}

/// <summary>代码块（标准 8.2，CORE）：三个反引号或三个波浪起止；内容不解析任何标记。</summary>
public sealed class CodeBlock : PrtBlock
{
    public string? Language { get; set; }

    public string Code { get; set; } = string.Empty;
}

/// <summary>分隔线（标准 8.2，CORE）：单独一行的 `---`。</summary>
public sealed class ThematicBreakBlock : PrtBlock
{
}

/// <summary>简化表格（管道表，标准 10.1，CORE）。</summary>
public sealed class PipeTableBlock : PrtBlock
{
    public TableModel Table { get; set; } = new();
}

/// <summary>完整表格（`::: table`，标准 10.2/10.3，EXT）。</summary>
public sealed class TableBlock : PrtBlock, IReferenceable
{
    public TableModel Table { get; set; } = new();

    public string? Id { get; set; }

    public string? Caption { get; set; }

    public TableAlignment? TableAlign { get; set; }

    public string? AssignedNumber { get; set; }

    public ReferenceTargetKind TargetKind => ReferenceTargetKind.Table;

    string? IReferenceable.Caption => Caption;
}

/// <summary>语义块（标准 8.3；CORE 八类 + EXT 四类）。</summary>
public sealed class SemanticBlock : PrtBlock
{
    public SemanticKind Kind { get; set; }

    /// <summary>`definition` 的标题参数（被定义术语）。</summary>
    public string? Title { get; set; }

    public List<PrtBlock> Children { get; } = new();
}

/// <summary>分栏中的一栏（标准 8.4）。</summary>
public sealed class ColumnBlock : PrtBlock
{
    public List<PrtBlock> Children { get; } = new();
}

/// <summary>选项卡中的一个标签页（标准 8.4）。</summary>
public sealed class TabBlock : PrtBlock
{
    public string? Name { get; set; }

    public List<PrtBlock> Children { get; } = new();
}

/// <summary>布局块（标准 8.4，EXT）：分栏 / 折叠 / 选项卡。</summary>
public sealed class LayoutBlock : PrtBlock
{
    public LayoutKind Kind { get; set; }

    /// <summary>collapse 的标题。</summary>
    public string? Title { get; set; }

    /// <summary>collapse 是否默认展开。</summary>
    public bool DefaultOpen { get; set; }

    /// <summary>columns 的栏数。</summary>
    public int Columns { get; set; } = 2;

    /// <summary>columns 的各栏。</summary>
    public List<ColumnBlock> ColumnItems { get; } = new();

    /// <summary>tabs 的各标签页。</summary>
    public List<TabBlock> TabItems { get; } = new();

    /// <summary>collapse 的内容。</summary>
    public List<PrtBlock> Children { get; } = new();
}

/// <summary>章节（`::: section`，标准 9.2，EXT）。可嵌套以表达层级。</summary>
public sealed class SectionBlock : PrtBlock, IReferenceable
{
    public string? Id { get; set; }

    public string? Title { get; set; }

    public NumberingStyle? Numeration { get; set; }

    /// <summary>是否进入目录，默认 true。</summary>
    public bool IncludeInToc { get; set; } = true;

    public List<PrtBlock> Children { get; } = new();

    /// <summary>块头显示的自动编号（如「第一章」）。</summary>
    public string? AssignedNumber { get; set; }

    /// <summary>编号计数快照（用于推导 `[[ref]]` 默认文本，标准 9.4）。</summary>
    public int[]? NumberCounters { get; set; }

    /// <summary>该章节生效的编号样式。</summary>
    public NumberingStyle EffectiveStyle { get; set; } = NumberingStyle.Mixed;

    /// <summary>该章节在编号层级中的深度（从 1 起）。</summary>
    public int NumberLevel { get; set; }

    public ReferenceTargetKind TargetKind => ReferenceTargetKind.Section;

    string? IReferenceable.Caption => Title;
}

/// <summary>摘要块（`::: summary`，标准 9.3，CORE）。</summary>
public sealed class SummaryBlock : PrtBlock
{
    public List<PrtBlock> Children { get; } = new();
}

/// <summary>元数据块（`::: meta`，标准 9.1，CORE）。</summary>
public sealed class MetaBlock : PrtBlock
{
    public PrtMetadata Metadata { get; set; } = new();
}

/// <summary>引用注册表块（`::: refs`，标准 9.4，EXT）。</summary>
public sealed class RefsBlock : PrtBlock
{
    public List<RefEntry> Entries { get; } = new();
}

/// <summary>自定义主题块（`::: theme`，标准 12.2，EXT）。仅覆盖附录 C 变量。</summary>
public sealed class ThemeBlock : PrtBlock
{
    public string? Name { get; set; }

    public string? Base { get; set; }

    public List<KeyValuePair<string, string>> Variables { get; } = new();
}

/// <summary>样式块（`::: text`，标准 12.1，CORE）。</summary>
public sealed class TextStyleBlock : PrtBlock
{
    public NamedColor? Color { get; set; }

    public FontSizeScale? Size { get; set; }

    public TextAlignment? Align { get; set; }

    public bool Bold { get; set; }

    public bool Italic { get; set; }

    public bool Strike { get; set; }

    public List<PrtBlock> Children { get; } = new();
}

/// <summary>对齐块（`::: align`，标准 12.1，CORE）。</summary>
public sealed class AlignBlock : PrtBlock
{
    public TextAlignment Align { get; set; } = TextAlignment.Left;

    public List<PrtBlock> Children { get; } = new();
}

/// <summary>插图（`::: figure`，标准 9.4，EXT）。</summary>
public sealed class FigureBlock : PrtBlock, IReferenceable
{
    public string? Id { get; set; }

    public string? Caption { get; set; }

    /// <summary>图片替代文本。</summary>
    public string? Alt { get; set; }

    /// <summary>图片来源路径。</summary>
    public string? Source { get; set; }

    /// <summary>图片后的补充说明段落。</summary>
    public List<PrtBlock> Children { get; } = new();

    public string? AssignedNumber { get; set; }

    public ReferenceTargetKind TargetKind => ReferenceTargetKind.Figure;

    string? IReferenceable.Caption => Caption;
}

/// <summary>公式（`::: equation`，标准 9.4，EXT）。</summary>
public sealed class EquationBlock : PrtBlock, IReferenceable
{
    public string? Id { get; set; }

    public string? Caption { get; set; }

    /// <summary>公式源码（LaTeX/ASCII）。本实现按第 13 章降级为代码块呈现。</summary>
    public string Source { get; set; } = string.Empty;

    public string? AssignedNumber { get; set; }

    public ReferenceTargetKind TargetKind => ReferenceTargetKind.Equation;

    string? IReferenceable.Caption => Caption;
}

/// <summary>
/// 计算块（`::: set` / `::: if` / `::: loop` / `::: run`，标准 8.6、第 11 章，COMP）。
/// <para>
/// 【COMP 不支持】本实现不解析其内部语义、不执行任何求值，仅完整保留源码，
/// 并在渲染时按第 13 章降级为代码块（不丢内容）。
/// </para>
/// </summary>
public sealed class ComputationBlock : PrtBlock
{
    public ComputationKind Kind { get; set; }

    /// <summary>完整的源码文本（含首行与结束行）。</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>首行冒号后的参数部分（如 `分数 = 85` 或 `i in 1..3`）。</summary>
    public string Header { get; set; } = string.Empty;
}

/// <summary>未知块类型：按标准 14.4 在宽松模式下降级为纯文本，严格模式报错。</summary>
public sealed class UnknownBlock : PrtBlock
{
    public string TypeName { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;
}
