namespace Prt.Prta.Compiler;

internal static partial class PrtaAnalyzer
{
    /// <summary>控制流配对栈：检查 IF/ELSE/ENDIF、WHILE/ENDWHILE、BREAK/CONTINUE 作用域与嵌套深度（9.5）。</summary>
    public static void CheckControlFlow(List<ParsedInstruction> instructions, string blockId, List<PrtaDiagnostic> diagnostics)
    {
        const int maxNesting = 64;
        // 栈元素：(种类 "IF"/"WHILE", 是否已见 ELSE, 该 IF 所属 WHILE 深度)
        var stack = new List<(string Kind, bool SawElse, int WhileDepth)>();
        var whileDepth = 0;

        foreach (var instruction in instructions)
        {
            switch (instruction.Opcode)
            {
                case "IF":
                    if (stack.Count >= maxNesting)
                    {
                        diagnostics.Add(new PrtaDiagnostic("E_LEX", blockId, instruction.Line, "控制块嵌套深度超过上限 64。"));
                    }

                    stack.Add(("IF", false, whileDepth));
                    break;
                case "ELSE":
                    if (stack.Count == 0 || stack[^1].Kind != "IF")
                    {
                        diagnostics.Add(new PrtaDiagnostic("E_LEX", blockId, instruction.Line, "ELSE 不在 IF 结构内。"));
                    }
                    else if (stack[^1].SawElse)
                    {
                        diagnostics.Add(new PrtaDiagnostic("E_LEX", blockId, instruction.Line, "同一 IF 出现多个 ELSE。"));
                    }
                    else
                    {
                        stack[^1] = (stack[^1].Kind, true, stack[^1].WhileDepth);
                    }

                    break;
                case "ENDIF":
                    if (stack.Count == 0 || stack[^1].Kind != "IF")
                    {
                        diagnostics.Add(new PrtaDiagnostic("E_LEX", blockId, instruction.Line, "ENDIF 没有配对的 IF。"));
                    }
                    else
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }

                    break;
                case "WHILE":
                    if (stack.Count >= maxNesting)
                    {
                        diagnostics.Add(new PrtaDiagnostic("E_LEX", blockId, instruction.Line, "控制块嵌套深度超过上限 64。"));
                    }

                    stack.Add(("WHILE", false, whileDepth));
                    whileDepth++;
                    break;
                case "ENDWHILE":
                    if (stack.Count == 0 || stack[^1].Kind != "WHILE")
                    {
                        diagnostics.Add(new PrtaDiagnostic("E_LEX", blockId, instruction.Line, "ENDWHILE 没有配对的 WHILE。"));
                    }
                    else
                    {
                        stack.RemoveAt(stack.Count - 1);
                        whileDepth--;
                    }

                    break;
                case "BREAK" or "CONTINUE":
                    if (whileDepth == 0)
                    {
                        diagnostics.Add(new PrtaDiagnostic("E_LEX", blockId, instruction.Line, instruction.Opcode + " 不在任何 WHILE 体内。"));
                    }

                    break;
            }
        }

        foreach (var open in stack)
        {
            diagnostics.Add(new PrtaDiagnostic(
                "E_LEX",
                blockId,
                0,
                open.Kind == "IF" ? "IF 缺少配对的 ENDIF。" : "WHILE 缺少配对的 ENDWHILE。"));
        }
    }
}
