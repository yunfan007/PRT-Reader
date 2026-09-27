namespace Prt.Prta.Compiler;

internal static partial class PrtaAnalyzer
{
    // ─────────────────────────────── 操作数辅助 ───────────────────────────────

    private static void RequireOperandCount(IReadOnlyList<Operand> operands, int expected, string op, string blockId, int line, List<PrtaDiagnostic> diagnostics)
    {
        if (operands.Count != expected)
        {
            diagnostics.Add(new PrtaDiagnostic("E_LEX", blockId, line, $"{op} 需要 {expected} 个操作数，实际 {operands.Count} 个。"));
        }
    }

    private static void RequireRel(Operand operand, string blockId, int line, List<PrtaDiagnostic> diagnostics)
    {
        if (operand.Kind != OperandKind.Immediate || !PrtaParser.ValidRels.Contains(operand.Text, StringComparer.Ordinal))
        {
            diagnostics.Add(new PrtaDiagnostic("E_LEX", blockId, line, "关系操作数必须是 == / != / < / <= / > / >= 之一：" + operand.Text));
        }
    }

    private static void RequireWritableDest(Operand operand, HashSet<string> declared, string blockId, int line, List<PrtaDiagnostic> diagnostics)
    {
        switch (operand.Kind)
        {
            case OperandKind.Register or OperandKind.ParamSlot:
                return;
            case OperandKind.Named:
                if (!declared.Contains(operand.Detail!))
                {
                    diagnostics.Add(new PrtaDiagnostic("E_LEX", blockId, line, "名变量未声明或先于声明使用：" + operand.Detail));
                }

                return;
            case OperandKind.Immediate:
                diagnostics.Add(new PrtaDiagnostic("E_TYPE", blockId, line, "立即数不得作目的操作数：" + operand.Text));
                return;
            default:
                diagnostics.Add(new PrtaDiagnostic("E_TYPE", blockId, line, "目的操作数必须是可写存储单元：" + operand.Text));
                return;
        }
    }

    private static void RequireReadable(Operand operand, HashSet<string> declared, string blockId, int line, List<PrtaDiagnostic> diagnostics, bool allowImmediate)
    {
        switch (operand.Kind)
        {
            case OperandKind.Register:
                return;
            case OperandKind.ParamSlot:
                return;
            case OperandKind.Named:
                if (!declared.Contains(operand.Detail!))
                {
                    diagnostics.Add(new PrtaDiagnostic("E_LEX", blockId, line, "名变量未声明或先于声明使用：" + operand.Detail));
                }

                return;
            case OperandKind.Immediate:
                if (!allowImmediate)
                {
                    diagnostics.Add(new PrtaDiagnostic("E_TYPE", blockId, line, "该位置不允许立即数：" + operand.Text));
                }

                return;
            default:
                diagnostics.Add(new PrtaDiagnostic("E_TYPE", blockId, line, "操作数种类不符：" + operand.Text));
                return;
        }
    }

    private static void CheckThreeOperandArithmetic(IReadOnlyList<Operand> operands, HashSet<string> declared, string blockId, int line, List<PrtaDiagnostic> diagnostics)
    {
        if (operands.Count != 3)
        {
            return;
        }

        RequireWritableDest(operands[0], declared, blockId, line, diagnostics);
        RequireReadable(operands[1], declared, blockId, line, diagnostics, allowImmediate: true);
        RequireReadable(operands[2], declared, blockId, line, diagnostics, allowImmediate: true);
    }

    /// <summary>立即数的静态类型（用于有限的静态类型检查）。</summary>
    private static string? InferImmediateType(Operand operand)
    {
        var text = operand.Text;
        if (text == "null")
        {
            return "Null";
        }

        if (text is "true" or "false")
        {
            return "Boolean";
        }

        if (text.StartsWith('"'))
        {
            return "String";
        }

        return text.Contains('.', StringComparison.Ordinal) ? "Decimal" : "Integer";
    }

    /// <summary>操作数的静态类型：立即数、带类型标注的名变量或形参；无法判定返回 null。</summary>
    private static string? InferStaticType(Operand operand, Dictionary<string, int> paramBindings, string blockId)
    {
        if (operand.Kind == OperandKind.Immediate)
        {
            return InferImmediateType(operand);
        }

        return null; // 寄存器/形参槽/无标注名变量的类型不可静态判定
    }

    /// <summary>类型相容（11.4）：同一类型恒相容；Integer → Decimal 单向相容。</summary>
    private static bool IsCompatible(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return true;
        }

        return from == "Integer" && to == "Decimal";
    }
}
