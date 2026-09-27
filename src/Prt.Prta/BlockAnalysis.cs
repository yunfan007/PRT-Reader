namespace Prt.Prta.Compiler;

/// <summary>单个块的内部校验产物（供代码生成器使用）。</summary>
internal sealed class BlockAnalysis
{
    public required PrtaBlock Block { get; init; }
    public required PrtaSignature? Signature { get; init; }
    public required List<ParsedInstruction> Instructions { get; init; }
    public required List<VarDecl> VarDeclarations { get; init; }
    /// <summary>形参名 → 形参槽下标（名变量绑定的映射）。</summary>
    public required Dictionary<string, int> ParamBindings { get; init; }
    public bool IsComponent => string.Equals(Block.BlockType, "prtui", StringComparison.Ordinal);
}
