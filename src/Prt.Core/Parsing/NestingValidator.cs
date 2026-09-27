using Prt.Core.Diagnostics;
using Prt.Core.Syntax;

namespace Prt.Core.Parsing;

/// <summary>
/// 嵌套权限校验（《PRT 标准》8.7 嵌套权限矩阵）。
/// 核心规则：语义块内允许基础块、布局块、计算块，<b>禁止</b>再嵌套语义块与结构块。
/// </summary>
internal static class NestingValidator
{
    public static void Validate(DocumentBlock document, PrtOptions options, DiagnosticBag diagnostics)
    {
        foreach (var block in Structure.PrtTree.WalkBlocks(document))
        {
            if (block is not SemanticBlock semantic)
            {
                continue;
            }

            foreach (var child in Structure.PrtTree.GetChildren(semantic))
            {
                if (child is SemanticBlock)
                {
                    ParsingHelpers.Report(
                        diagnostics,
                        options.Strict,
                        DiagnosticCodes.NestingViolation,
                        "语义块内不得再嵌套语义块（标准 8.7 嵌套权限矩阵）",
                        child.Line,
                        child.Column);
                }
                else if (IsStructureBlock(child))
                {
                    ParsingHelpers.Report(
                        diagnostics,
                        options.Strict,
                        DiagnosticCodes.NestingViolation,
                        "语义块内不得嵌套结构块（标准 8.7 嵌套权限矩阵）",
                        child.Line,
                        child.Column);
                }
            }
        }
    }

    private static bool IsStructureBlock(PrtBlock block) => block switch
    {
        MetaBlock => true,
        SectionBlock => true,
        SummaryBlock => true,
        RefsBlock => true,
        ThemeBlock => true,
        TableBlock => true,
        FigureBlock => true,
        EquationBlock => true,
        _ => false,
    };

    // 便于阅读的别名，避免与 Prt.Core.Diagnostics 命名冲突。
    private static class DiagnosticCodes
    {
        public const string NestingViolation = PrtDiagnosticCodes.NestingViolation;
    }
}
