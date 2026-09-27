namespace Prt.Core.Syntax;

/// <summary>语法树节点的基类，携带源位置（行号、列号，均从 1 起）。</summary>
public abstract class PrtNode
{
    /// <summary>节点起始行号（从 1 起）。</summary>
    public int Line { get; set; } = 1;

    /// <summary>节点起始列号（从 1 起）。</summary>
    public int Column { get; set; } = 1;

    /// <summary>节点在源文本中的原始文本（可选，用于降级与调试）。</summary>
    public string? RawText { get; set; }
}

/// <summary>块级节点基类（标准第 8 章）。</summary>
public abstract class PrtBlock : PrtNode
{
}

/// <summary>行内节点基类（标准第 7 章）。</summary>
public abstract class PrtInline : PrtNode
{
}

/// <summary>
/// 可被交叉引用且带自动编号的块所实现的接口（标准 9.4）。
/// </summary>
public interface IReferenceable
{
    /// <summary>引用目标 id；为空表示该块不可被交叉引用。</summary>
    string? Id { get; }

    /// <summary>引用目标种类。</summary>
    ReferenceTargetKind TargetKind { get; }

    /// <summary>结构解析阶段填充的自动编号文本，如「第一章」「表1」「式(1)」。</summary>
    string? AssignedNumber { get; set; }

    /// <summary>用于默认引用文本回退的标题或题注。</summary>
    string? Caption { get; }
}
