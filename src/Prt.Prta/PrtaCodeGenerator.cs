using System.Text;

namespace Prt.Prta.Compiler;

/// <summary>
/// PRTA-JS 代码生成器（附录 D，规范性口径）：
/// D.1 骨架（环境对象成员提取顺序固定、16 个寄存器完整声明、入口 env.entry）、
/// D.2 逐指令模板（每条指令恰一次 budget.step()，位于目标语句之前）、
/// D.3 结构化映射（IF/ELSE/ENDIF、WHILE/BREAK/CONTINUE，不产生任何标签）。
/// </summary>
internal static class PrtaCodeGenerator
{
    public static string GenerateDocument(
        IReadOnlyList<BlockAnalysis> analyses,
        IReadOnlyList<PrtaDiagnostic> diagnostics,
        PrtaCompileOptions options)
    {
        var sb = new StringBuilder(4096);
        var nl = Environment.NewLine;

        // ── D.1 骨架头部 ──
        sb.Append("\"use strict\";").Append(nl);
        sb.Append("(function (env) {").Append(nl);
        sb.Append("  \"use strict\";").Append(nl);
        sb.Append("  var dec    = env.dec;").Append(nl);
        sb.Append("  var api    = env.api;").Append(nl);
        sb.Append("  var cmp    = env.cmp;").Append(nl);
        sb.Append("  var truthy = env.truthy;").Append(nl);
        sb.Append("  var budget = env.budget;").Append(nl);
        sb.Append("  var out    = env.out;").Append(nl);
        sb.Append("  var vars   = env.vars;").Append(nl);
        sb.Append("  var ports  = env.ports;").Append(nl);
        sb.Append("  var stack  = env.stack;").Append(nl);

        foreach (var analysis in analyses)
        {
            if (analysis.IsComponent)
            {
                continue; // D.6：组件块不产生目标程序
            }

            AppendFunction(sb, analysis, nl);
        }

        // ── 入口（D.1）──
        var entry = ResolveEntry(analyses, options, diagnostics);
        if (entry is not null)
        {
            sb.Append(nl);
            sb.Append("  env.entry(function () { return __fn_").Append(entry).Append("(); });").Append(nl);
        }

        sb.Append("})(/* PRTA-ENV */);").Append(nl);
        return sb.ToString();
    }

    private static string? ResolveEntry(IReadOnlyList<BlockAnalysis> analyses, PrtaCompileOptions options, IReadOnlyList<PrtaDiagnostic> diagnostics)
    {
        if (options.EntryBlockId is { } requested)
        {
            foreach (var analysis in analyses)
            {
                if (analysis.Block.Id == requested)
                {
                    return analysis.IsComponent ? null : requested;
                }
            }

            return null;
        }

        foreach (var analysis in analyses)
        {
            if (!analysis.IsComponent)
            {
                return analysis.Block.Id;
            }
        }

        return null;
    }

    private static void AppendFunction(StringBuilder sb, BlockAnalysis analysis, string nl)
    {
        var block = analysis.Block;
        var paramCount = analysis.Signature?.Params.Count ?? 0;

        // 函数签名：形参槽按签名个数展开（D.1）。
        var parameters = new string[paramCount];
        for (var i = 0; i < paramCount; i++)
        {
            parameters[i] = "__p" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        sb.Append(nl);
        sb.Append("  // ── ").Append(block.Id).Append(" ──").Append(nl);
        sb.Append("  function __fn_").Append(block.Id).Append('(').Append(string.Join(", ", parameters)).Append(") {").Append(nl);

        // 16 个寄存器完整声明，不得省略（D.1）。
        sb.Append("    var");
        for (var i = 0; i < 16; i++)
        {
            sb.Append(" __r").Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" = null,");
        }

        sb.Length -= 1; // 去掉末尾逗号
        sb.Append(';').Append(nl);

        // VAR 声明按声明顺序合并为一条 var（D.1 / D.5；不计步）。
        if (analysis.VarDeclarations.Count > 0)
        {
            sb.Append("    var");
            foreach (var decl in analysis.VarDeclarations)
            {
                sb.Append(" __v_").Append(decl.Name).Append(" = ").Append(decl.Init is { } init ? init.Detail : "null").Append(',');
            }

            sb.Length -= 1;
            sb.Append(';').Append(nl);
        }

        // 指令序列。
        var indent = "    ";
        var stack = new Stack<string>();
        foreach (var instruction in analysis.Instructions)
        {
            switch (instruction.Opcode)
            {
                case "IF":
                    // 条件求值前调用一次 budget.step()（D.3）。
                    sb.Append(indent).Append("budget.step();").Append(nl);
                    sb.Append(indent).Append("if (").Append(BuildComparison(instruction, analysis)).Append(") {").Append(nl);
                    stack.Push("if");
                    indent += "  ";
                    break;
                case "ELSE":
                    indent = indent[..^2];
                    sb.Append(indent).Append("} else {").Append(nl);
                    indent += "  ";
                    break;
                case "ENDIF":
                    indent = indent[..^2];
                    sb.Append(indent).Append('}').Append(nl);
                    _ = stack.Pop();
                    break;
                case "WHILE":
                    sb.Append(indent).Append("while (true) {").Append(nl);
                    indent += "  ";
                    sb.Append(indent).Append("budget.step();").Append(nl);
                    sb.Append(indent).Append("if (!").Append(BuildComparison(instruction, analysis)).Append(") { break; }").Append(nl);
                    stack.Push("while");
                    break;
                case "ENDWHILE":
                    indent = indent[..^2];
                    sb.Append(indent).Append('}').Append(nl);
                    _ = stack.Pop();
                    break;
                case "BREAK":
                    sb.Append(indent).Append("budget.step(); break;").Append(nl);
                    break;
                case "CONTINUE":
                    sb.Append(indent).Append("budget.step(); continue;").Append(nl);
                    break;
                case "NOP":
                    sb.Append(indent).Append("budget.step();").Append(nl);
                    break;
                case "HALT":
                    sb.Append(indent).Append("budget.step(); return null;").Append(nl);
                    break;
                default:
                    sb.Append(indent).Append("budget.step(); ").Append(BuildStatement(instruction, analysis)).Append(nl);
                    break;
            }
        }

        // D.1 骨架：函数体以 return null; 结束。
        sb.Append("    return null;").Append(nl);
        sb.Append("  }").Append(nl);
    }

    private static string BuildComparison(ParsedInstruction instruction, BlockAnalysis analysis)
    {
        var rel = instruction.Operands[1].Text;
        var a = MapOperand(instruction.Operands[0], analysis);
        var b = MapOperand(instruction.Operands[2], analysis);
        var comparator = rel switch
        {
            "==" => "eq",
            "!=" => "ne",
            "<" => "lt",
            "<=" => "le",
            ">" => "gt",
            ">=" => "ge",
            _ => "eq",
        };
        return $"cmp.{comparator}({a}, {b})";
    }

    private static string BuildStatement(ParsedInstruction instruction, BlockAnalysis analysis)
    {
        var op = instruction.Opcode;
        var operands = instruction.Operands;
        string Map(int i) => MapOperand(operands[i], analysis);

        // 白名单函数指令（v5.0）：函数名大写即操作码 → __rdst = api.<func>(<rarg…>);
        if (instruction.FuncName is { } functionName)
        {
            return $"{Map(0)} = api.{functionName}({JoinArgs(operands, 1, analysis)});";
        }

        return op switch
        {
            "LOAD" or "MOV" => $"{Map(0)} = {Map(1)};",
            "ADD" => $"{Map(0)} = dec.add({Map(1)}, {Map(2)});",
            "SUB" => $"{Map(0)} = dec.sub({Map(1)}, {Map(2)});",
            "MUL" => $"{Map(0)} = dec.mul({Map(1)}, {Map(2)});",
            "DIV" => $"{Map(0)} = dec.div({Map(1)}, {Map(2)});",
            "MOD" => $"{Map(0)} = dec.mod({Map(1)}, {Map(2)});",
            "NEG" => $"{Map(0)} = dec.neg({Map(1)});",
            "AND" => $"{Map(0)} = truthy({Map(1)}) ? {Map(2)} : {Map(1)};",
            "OR" => $"{Map(0)} = truthy({Map(1)}) ? {Map(1)} : {Map(2)};",
            "NOT" => $"{Map(0)} = !truthy({Map(1)});",
            "CALL" => $"{Map(1)} = __fn_{operands[0].Detail}({JoinArgs(operands, 2, analysis)});",
            "RETURN" => $"return {Map(0)};",
            "LIST_NEW" => $"{Map(0)} = [];",
            "LIST_LEN" => $"{Map(0)} = api.len({Map(1)});",
            "LIST_GET" => $"{Map(0)} = api.listGet({Map(1)}, {Map(2)});",
            "LIST_PUSH" => $"api.listPush({Map(0)}, {Map(1)});",
            "MAP_NEW" => $"{Map(0)} = api.mapNew();",
            "MAP_GET" => $"{Map(0)} = api.mapGet({Map(1)}, {Map(2)});",
            "MAP_SET" => $"api.mapSet({Map(0)}, {Map(1)}, {Map(2)});",
            "MAP_KEYS" => $"{Map(0)} = api.mapKeys({Map(1)});",
            "MAP_VALUES" => $"{Map(0)} = api.mapValues({Map(1)});",
            "PUSH" => $"stack.push({Map(0)});",
            "POP" => $"{Map(0)} = stack.pop();",
            "EMIT" when operands[0].Kind == OperandKind.PortRef => $"out.port(\"{operands[0].Detail}\", {Map(1)});",
            "EMIT" => $"out.var(\"{operands[0].Detail}\", {Map(1)});",
            "LOADVAR" => $"{Map(0)} = vars.get(\"{operands[1].Detail}\");",
            "LOADPORT" => $"{Map(0)} = ports.get(\"{operands[1].Detail}\");",
            _ => ";",
        };
    }

    private static string JoinArgs(IReadOnlyList<Operand> operands, int from, BlockAnalysis analysis)
    {
        if (operands.Count <= from)
        {
            return string.Empty;
        }

        var parts = new string[operands.Count - from];
        for (var i = from; i < operands.Count; i++)
        {
            parts[i - from] = MapOperand(operands[i], analysis);
        }

        return string.Join(", ", parts);
    }

    /// <summary>操作数映射（D.2）：rN→__rN，pN→__pN，名变量→__v_&lt;name&gt;（形参名绑定的名变量→__pN），立即数→字面量。</summary>
    private static string MapOperand(Operand operand, BlockAnalysis analysis)
    {
        return operand.Kind switch
        {
            OperandKind.Register => "__r" + operand.Detail,
            OperandKind.ParamSlot => "__p" + operand.Detail,
            OperandKind.Named => analysis.ParamBindings.TryGetValue(operand.Detail!, out var slot)
                ? "__p" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "__v_" + operand.Detail,
            OperandKind.Immediate => operand.Detail ?? "null",
            _ => "null",
        };
    }
}
