namespace Prt.Core;

/// <summary>命名色（《PRT 标准》12.1 节，规范性取值）。</summary>
public enum NamedColor
{
    Black,
    White,
    Gray,
    Navy,
    Red,
    Orange,
    Yellow,
    Green,
    Teal,
    Blue,
    Purple,
    Cyan,
    Magenta,
    Brown,
    Olive,
    Maroon,
    Danger,
    Warning,
    Info,
    Success,
}

/// <summary>块级对齐（标准 12.1）。</summary>
public enum TextAlignment
{
    Left,
    Center,
    Right,
    Justify,
}

/// <summary>字号档位（标准 12.1），仅改变字号、不改变行高。</summary>
public enum FontSizeScale
{
    Small,
    Normal,
    Large,
    XLarge,
}

/// <summary>章节编号样式（标准 9.2）。</summary>
public enum NumberingStyle
{
    /// <summary>默认：一级「第一章」，二级及以下 1.1 / 1.1.1。</summary>
    Mixed,
    /// <summary>一级「第一章」，二级「第X节」，三级「一、」。</summary>
    Cn,
    /// <summary>1 / 1.1 / 1.1.1。</summary>
    Arabic,
    /// <summary>I / I.1 / I.1.1。</summary>
    Roman,
    /// <summary>不编号。</summary>
    None,
}

/// <summary>语义块类型（标准 8.3）。CORE 与 EXT 分列。</summary>
public enum SemanticKind
{
    Note,
    Warning,
    Danger,
    Attention,
    Example,
    Definition,
    Quote,
    Comment,
    Info,
    Tip,
    Result,
    Task,
}

/// <summary>布局块类型（标准 8.4，EXT）。</summary>
public enum LayoutKind
{
    Columns,
    Collapse,
    Tabs,
}

/// <summary>计算块类型（标准 8.6 / 第 11 章，COMP —— 本实现不执行）。</summary>
public enum ComputationKind
{
    Set,
    If,
    Loop,
    Run,
}

/// <summary>PRT 符合性功能等级（标准 14.3）。</summary>
public enum PrtConformanceLevel
{
    /// <summary>核心：基础行内标记、语义块、基础结构、样式、降级。</summary>
    Core,
    /// <summary>扩展：CORE + 章节编号、交叉引用、完整表格、布局块、主题定制、术语链接、脚注等。</summary>
    Ext,
    /// <summary>计算：EXT + 变量、条件、循环、受限运行与全部白名单函数。本实现不支持。</summary>
    Comp,
}

/// <summary>引用目标种类（用于交叉引用默认文本的推导，标准 9.4）。</summary>
public enum ReferenceTargetKind
{
    Section,
    Figure,
    Table,
    Equation,
    Term,
    Alias,
}
