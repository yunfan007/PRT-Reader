namespace Prt.Prta.Compiler;

internal static partial class PrtaAnalyzer
{
    /// <summary>
    /// 白名单函数指令的静态校验（12.2，v5.0）：第一操作数恒为可写目的操作数，其余按序为实参；
    /// 不安全白名单函数仅 <c>unsafe=true</c> 块可调用。
    /// </summary>
    private static void AnalyzeFunctionCall(
        ParsedInstruction instruction,
        string functionName,
        PrtaBlock block,
        PrtaCompileOptions options,
        HashSet<string> declared,
        List<PrtaDiagnostic> diagnostics)
    {
        var line = instruction.Line;
        var operands = instruction.Operands;

        if (operands.Count < 1)
        {
            diagnostics.Add(new PrtaDiagnostic("E_LEX", block.Id, line, instruction.Opcode + " 至少需要目的操作数。"));
            return;
        }

        RequireWritableDest(operands[0], declared, block.Id, line, diagnostics);
        for (var i = 1; i < operands.Count; i++)
        {
            RequireReadable(operands[i], declared, block.Id, line, diagnostics, allowImmediate: true);
        }

        if (PrtaParser.IsUnsafeFunction(functionName) && !IsUnsafeBlock(block))
        {
            diagnostics.Add(new PrtaDiagnostic("E_UNSAFE", block.Id, line,
                "安全块不得调用不安全白名单函数：" + functionName + "（须声明 unsafe=\"true\"，10.12）。"));
        }

        if (options.Whitelist is { } whitelist && !whitelist.Contains(functionName))
        {
            diagnostics.Add(new PrtaDiagnostic("E_WHITELIST", block.Id, line, "调用了非白名单函数：" + functionName));
        }
    }

    private static void AnalyzeVarDeclaration(ParsedInstruction instruction, string blockId, Dictionary<string, int> paramBindings, List<VarDecl> varDecls, HashSet<string> declared, IReadOnlyDictionary<string, string> functions, List<PrtaDiagnostic> diagnostics)
    {
        var name = instruction.VarName ?? string.Empty;
        if (name.Length == 0)
        {
            return; // 解析时已报错
        }

        if (paramBindings.ContainsKey(name))
        {
            diagnostics.Add(new PrtaDiagnostic("E_LEX", blockId, instruction.Line, "名变量与形参名同名，不得重复声明：" + name));
            return;
        }

        if (PrtaParser.IsRegister(name, out _) || PrtaParser.IsParamSlot(name, out _))
        {
            diagnostics.Add(new PrtaDiagnostic("E_LEX", blockId, instruction.Line, "名变量不得与寄存器名或形参槽名同名：" + name));
            return;
        }

        if (PrtaParser.IsOpcode(name))
        {
            diagnostics.Add(new PrtaDiagnostic("E_LEX", blockId, instruction.Line, "名变量不得与操作码同名：" + name));
            return;
        }

        if (functions.ContainsKey(name))
        {
            diagnostics.Add(new PrtaDiagnostic("E_LEX", blockId, instruction.Line, "名变量不得与白名单函数的大写操作码同名：" + name));
            return;
        }

        if (!declared.Add(name))
        {
            diagnostics.Add(new PrtaDiagnostic("E_LEX", blockId, instruction.Line, "名变量重复声明：" + name));
            return;
        }

        varDecls.Add(new VarDecl(name, instruction.VarType, instruction.VarInit, instruction.Line));
    }

    private static void AnalyzeInstruction(
        ParsedInstruction instruction,
        PrtaBlock block,
        Dictionary<string, PrtaBlock> blockById,
        PrtaCompileOptions options,
        PrtaSignature? signature,
        Dictionary<string, int> paramBindings,
        HashSet<string> declared,
        List<PrtaDiagnostic> diagnostics)
    {
        // 白名单函数指令（v5.0）：函数名大写即操作码，与普通指令同构（12.2）。
        if (instruction.FuncName is { } functionName)
        {
            AnalyzeFunctionCall(instruction, functionName, block, options, declared, diagnostics);
            return;
        }

        // 结构化控制流配对与作用域（9.5）由配对栈处理；这里做其余检查。
        //
        // 分派拆成「族 → 具体操作码」两级，是为了把原先近 270 行的单一 switch 按操作码族切开
        // （D-06「超百行方法」）。**各族方法内部仍是完整的 switch，case 体与拆分前逐行相同**，
        // 因此这次拆分是纯结构调整，不改变任何一条诊断的产生条件。
        if (!DispatchInstruction(instruction, block, blockById, options, signature, paramBindings, declared, diagnostics))
        {
            diagnostics.Add(new PrtaDiagnostic("E_LEX", block.Id, instruction.Line, "未知操作码：" + instruction.Opcode));
        }
    }

    /// <summary>
    /// 按操作码族分派；返回 false 表示该操作码不属于任何族（调用方据此报「未知操作码」）。
    /// <para>
    /// <b>族清单与各族 switch 的 case 标签必须同时登记</b>：漏在这里登记、却出现在族方法里的
    /// 操作码会走不到；反之则会落到「未知操作码」。新增指令时两处都要改。
    /// </para>
    /// </summary>
    private static bool DispatchInstruction(
        ParsedInstruction instruction,
        PrtaBlock block,
        Dictionary<string, PrtaBlock> blockById,
        PrtaCompileOptions options,
        PrtaSignature? signature,
        Dictionary<string, int> paramBindings,
        HashSet<string> declared,
        List<PrtaDiagnostic> diagnostics)
    {
        switch (instruction.Opcode)
        {
            case "IF" or "WHILE" or "ELSE" or "ENDIF" or "ENDWHILE" or "HALT" or "NOP" or "BREAK" or "CONTINUE":
                AnalyzeControlInstruction(instruction, block, declared, diagnostics);
                return true;
            case "LOAD" or "MOV" or "NEG" or "NOT":
                AnalyzeMoveInstruction(instruction, block, declared, diagnostics);
                return true;
            case "RETURN":
                AnalyzeReturnInstruction(instruction, block, options, signature, declared, diagnostics);
                return true;
            case "PUSH" or "POP":
                AnalyzeStackInstruction(instruction, block, declared, diagnostics);
                return true;
            case "LOADVAR" or "LOADPORT":
                AnalyzeVarPortInstruction(instruction, block, blockById, options, declared, diagnostics);
                return true;
            case "ADD" or "SUB" or "MUL" or "DIV" or "MOD" or "AND" or "OR":
                AnalyzeArithmeticInstruction(instruction, block, declared, diagnostics);
                return true;
            case "CALL":
                AnalyzeCallInstruction(instruction, block, blockById, options, paramBindings, declared, diagnostics);
                return true;
            case "SYSCALL":
                AnalyzeSyscallInstruction(instruction, block, diagnostics);
                return true;
            case "LIST_NEW" or "MAP_NEW" or "LIST_LEN" or "MAP_KEYS" or "MAP_VALUES"
                 or "LIST_GET" or "MAP_GET":
                AnalyzeCollectionReadInstruction(instruction, block, declared, diagnostics);
                return true;
            case "LIST_PUSH" or "MAP_SET":
                AnalyzeCollectionWriteInstruction(instruction, block, declared, diagnostics);
                return true;
            case "EMIT":
                AnalyzeEmitInstruction(instruction, block, blockById, options, declared, diagnostics);
                return true;
            default:
                return false;
        }
    }

    // ───────────────────────── 各族的具体校验 ─────────────────────────
    //
    // 以下每个方法对应拆分前 switch 里的一组 case；方法体的 switch 是原 switch 的同名切片，
    // case 体逐行保留（含既有的中文说明注释）。因此**阅读诊断逻辑时看这些方法即可**。

    /// <summary>IF/WHILE/ELSE/ENDIF/ENDWHILE/HALT/NOP/BREAK/CONTINUE 的操作数与形态。</summary>
    private static void AnalyzeControlInstruction(ParsedInstruction instruction, PrtaBlock block, HashSet<string> declared, List<PrtaDiagnostic> diagnostics)
    {
        var op = instruction.Opcode;
        var line = instruction.Line;
        var operands = instruction.Operands;

        switch (op)
        {
            case "IF" or "WHILE":
                RequireOperandCount(operands, 3, op, block.Id, line, diagnostics);
                if (operands.Count == 3)
                {
                    RequireRel(operands[1], block.Id, line, diagnostics);
                    RequireReadable(operands[0], declared, block.Id, line, diagnostics, allowImmediate: true);
                    RequireReadable(operands[2], declared, block.Id, line, diagnostics, allowImmediate: true);
                }

                break;
            case "ELSE" or "ENDIF" or "ENDWHILE" or "HALT" or "NOP":
                RequireOperandCount(operands, 0, op, block.Id, line, diagnostics);
                break;
            case "BREAK" or "CONTINUE":
                RequireOperandCount(operands, 0, op, block.Id, line, diagnostics);
                break;
        }
    }

    /// <summary>LOAD / MOV / NEG / NOT 的目的可写与源可读。</summary>
    private static void AnalyzeMoveInstruction(ParsedInstruction instruction, PrtaBlock block, HashSet<string> declared, List<PrtaDiagnostic> diagnostics)
    {
        var op = instruction.Opcode;
        var line = instruction.Line;
        var operands = instruction.Operands;

        switch (op)
        {
            case "LOAD":
                // 宽容扩展（第 9.2 节 LOAD rd, imm 的超集）：源操作数除立即数外，
                // 亦接受寄存器、形参槽与名变量——此时 LOAD 与 MOV 等价。
                // 这样作者可以直接写 `LOAD r0, a`（a 为名变量），无需先区分 LOAD / MOV。
                RequireOperandCount(operands, 2, op, block.Id, line, diagnostics);
                if (operands.Count == 2)
                {
                    RequireWritableDest(operands[0], declared, block.Id, line, diagnostics);
                    RequireReadable(operands[1], declared, block.Id, line, diagnostics, allowImmediate: true);
                }

                break;
            case "MOV":
                RequireOperandCount(operands, 2, op, block.Id, line, diagnostics);
                if (operands.Count == 2)
                {
                    RequireWritableDest(operands[0], declared, block.Id, line, diagnostics);
                    RequireReadable(operands[1], declared, block.Id, line, diagnostics, allowImmediate: true);
                }

                break;
            case "NEG" or "NOT":
                RequireOperandCount(operands, 2, op, block.Id, line, diagnostics);
                if (operands.Count == 2)
                {
                    RequireWritableDest(operands[0], declared, block.Id, line, diagnostics);
                    RequireReadable(operands[1], declared, block.Id, line, diagnostics, allowImmediate: true);
                }

                break;
        }
    }

    /// <summary>RETURN 的可读性与（启用静态类型检查时）返回类型相容。</summary>
    private static void AnalyzeReturnInstruction(ParsedInstruction instruction, PrtaBlock block, PrtaCompileOptions options, PrtaSignature? signature, HashSet<string> declared, List<PrtaDiagnostic> diagnostics)
    {
        var op = instruction.Opcode;
        var line = instruction.Line;
        var operands = instruction.Operands;

        switch (op)
        {
            case "RETURN":
                RequireOperandCount(operands, 1, op, block.Id, line, diagnostics);
                if (operands.Count == 1)
                {
                    RequireReadable(operands[0], declared, block.Id, line, diagnostics, allowImmediate: true);
                    if (options.StaticTypeChecks && operands[0].Kind == OperandKind.Immediate && signature?.ReturnType is { } rt)
                    {
                        var literalType = InferImmediateType(operands[0]);
                        if (literalType is { } lt && !IsCompatible(lt, rt))
                        {
                            diagnostics.Add(new PrtaDiagnostic("E_TYPE", block.Id, line, $"RETURN 交付值类型 {lt} 与签名返回类型 {rt} 不相容。"));
                        }
                    }
                }

                break;
        }
    }

    /// <summary>PUSH 的可读源与 POP 的可写目的。</summary>
    private static void AnalyzeStackInstruction(ParsedInstruction instruction, PrtaBlock block, HashSet<string> declared, List<PrtaDiagnostic> diagnostics)
    {
        var op = instruction.Opcode;
        var line = instruction.Line;
        var operands = instruction.Operands;

        switch (op)
        {
            case "PUSH":
                RequireOperandCount(operands, 1, op, block.Id, line, diagnostics);
                if (operands.Count == 1)
                {
                    RequireReadable(operands[0], declared, block.Id, line, diagnostics, allowImmediate: true);
                }

                break;
            case "POP":
                RequireOperandCount(operands, 1, op, block.Id, line, diagnostics);
                if (operands.Count == 1)
                {
                    RequireWritableDest(operands[0], declared, block.Id, line, diagnostics);
                }

                break;
        }
    }

    /// <summary>LOADVAR / LOADPORT 的前缀形态与端口可读性。</summary>
    private static void AnalyzeVarPortInstruction(ParsedInstruction instruction, PrtaBlock block, Dictionary<string, PrtaBlock> blockById, PrtaCompileOptions options, HashSet<string> declared, List<PrtaDiagnostic> diagnostics)
    {
        var op = instruction.Opcode;
        var line = instruction.Line;
        var operands = instruction.Operands;

        switch (op)
        {
            case "LOADVAR":
                RequireOperandCount(operands, 2, op, block.Id, line, diagnostics);
                if (operands.Count == 2)
                {
                    if (operands[1].Kind != OperandKind.VarRef)
                    {
                        diagnostics.Add(new PrtaDiagnostic("E_LEX", block.Id, line, "LOADVAR 的第二操作数必须以 $ 引用计算变量：" + operands[1].Text));
                    }

                    RequireWritableDest(operands[0], declared, block.Id, line, diagnostics);
                }

                break;
            case "LOADPORT":
                RequireOperandCount(operands, 2, op, block.Id, line, diagnostics);
                if (operands.Count == 2)
                {
                    if (operands[1].Kind != OperandKind.PortRef)
                    {
                        diagnostics.Add(new PrtaDiagnostic("E_LEX", block.Id, line, "LOADPORT 的第二操作数必须以 @ 引用端口：" + operands[1].Text));
                    }
                    else if (options.ValidatePorts && !IsReadablePort(operands[1].Detail!, block, blockById))
                    {
                        diagnostics.Add(new PrtaDiagnostic("E_OUTPUT", block.Id, line, "LOADPORT 的目标端口不存在或文档顺序晚于本块：@" + operands[1].Detail));
                    }

                    RequireWritableDest(operands[0], declared, block.Id, line, diagnostics);
                }

                break;
        }
    }

    /// <summary>三操作数算术 / 逻辑指令。</summary>
    private static void AnalyzeArithmeticInstruction(ParsedInstruction instruction, PrtaBlock block, HashSet<string> declared, List<PrtaDiagnostic> diagnostics)
    {
        var op = instruction.Opcode;
        var line = instruction.Line;
        var operands = instruction.Operands;

        switch (op)
        {
            case "ADD" or "SUB" or "MUL" or "DIV" or "MOD" or "AND" or "OR":
                RequireOperandCount(operands, 3, op, block.Id, line, diagnostics);
                CheckThreeOperandArithmetic(operands, declared, block.Id, line, diagnostics);
                break;
        }
    }

    /// <summary>CALL 的目标块、实参个数与（启用时）实参类型相容。</summary>
    private static void AnalyzeCallInstruction(ParsedInstruction instruction, PrtaBlock block, Dictionary<string, PrtaBlock> blockById, PrtaCompileOptions options, Dictionary<string, int> paramBindings, HashSet<string> declared, List<PrtaDiagnostic> diagnostics)
    {
        var op = instruction.Opcode;
        var line = instruction.Line;
        var operands = instruction.Operands;

        switch (op)
        {
            case "CALL":
            {
                if (operands.Count < 2)
                {
                    diagnostics.Add(new PrtaDiagnostic("E_LEX", block.Id, line, "CALL 至少需要 @目标 与目的操作数两个操作数。"));
                    break;
                }

                if (operands[0].Kind != OperandKind.PortRef)
                {
                    diagnostics.Add(new PrtaDiagnostic("E_LEX", block.Id, line, "CALL 的第一操作数必须以 @ 引用目标块：" + operands[0].Text));
                    break;
                }

                var targetId = operands[0].Detail!;
                if (!blockById.TryGetValue(targetId, out var targetBlock))
                {
                    diagnostics.Add(new PrtaDiagnostic("E_SIGNATURE", block.Id, line, "CALL 的目标块未在文档中声明：@" + targetId));
                    break;
                }

                var targetSig = PrtaParser.ParseSignature(targetBlock.Sig, diagnostics, targetBlock.Id) ?? new PrtaSignature(null, [], null);
                var expectedArity = targetSig.Params.Count;
                var argCount = operands.Count - 2;
                if (argCount != expectedArity)
                {
                    diagnostics.Add(new PrtaDiagnostic("E_SIGNATURE", block.Id, line, $"CALL @{targetId} 实参个数 {argCount} ≠ 形参个数 {expectedArity}。"));
                    break;
                }

                RequireWritableDest(operands[1], declared, block.Id, line, diagnostics);
                for (var i = 2; i < operands.Count; i++)
                {
                    RequireReadable(operands[i], declared, block.Id, line, diagnostics, allowImmediate: true);
                    if (options.StaticTypeChecks
                        && targetSig.Params[i - 2].Type is { } targetType
                        && InferStaticType(operands[i], paramBindings, targetBlock.Id) is { } argType
                        && !IsCompatible(argType, targetType))
                    {
                        diagnostics.Add(new PrtaDiagnostic("E_SIGNATURE", block.Id, line, $"CALL @{targetId} 第 {i - 1} 个实参类型 {argType} 与形参类型 {targetType} 不相容。"));
                    }
                }

                break;
            }
        }
    }

    /// <summary>SYSCALL（v5.0 已废止）的迁移指引。</summary>
    private static void AnalyzeSyscallInstruction(ParsedInstruction instruction, PrtaBlock block, List<PrtaDiagnostic> diagnostics)
    {
        var op = instruction.Opcode;
        var line = instruction.Line;

        switch (op)
        {
            // v5.0 起该指令已废止；此处仅保留分支以给出迁移指引（词法层通常已先行拦截）。
            case "SYSCALL":
                diagnostics.Add(new PrtaDiagnostic("E_LEX", block.Id, line,
                    "SYSCALL 已在 PRTA v5.0 废止：白名单函数改为「函数名大写即操作码」，请改写为 FUNC 目的, 实参… 形式（例如 SYSCALL upper, r0, s → UPPER r0, s）。"));
                break;
        }
    }

    /// <summary>LIST_* / MAP_* 的读取类指令（新建、取长度、取元素）。</summary>
    private static void AnalyzeCollectionReadInstruction(ParsedInstruction instruction, PrtaBlock block, HashSet<string> declared, List<PrtaDiagnostic> diagnostics)
    {
        var op = instruction.Opcode;
        var line = instruction.Line;
        var operands = instruction.Operands;

        switch (op)
        {
            case "LIST_NEW" or "MAP_NEW":
                RequireOperandCount(operands, 1, op, block.Id, line, diagnostics);
                if (operands.Count == 1)
                {
                    RequireWritableDest(operands[0], declared, block.Id, line, diagnostics);
                }

                break;
            case "LIST_LEN" or "MAP_KEYS" or "MAP_VALUES":
                RequireOperandCount(operands, 2, op, block.Id, line, diagnostics);
                if (operands.Count == 2)
                {
                    RequireWritableDest(operands[0], declared, block.Id, line, diagnostics);
                    RequireReadable(operands[1], declared, block.Id, line, diagnostics, allowImmediate: false);
                }

                break;
            case "LIST_GET" or "MAP_GET":
                RequireOperandCount(operands, 3, op, block.Id, line, diagnostics);
                if (operands.Count == 3)
                {
                    RequireWritableDest(operands[0], declared, block.Id, line, diagnostics);
                    RequireReadable(operands[1], declared, block.Id, line, diagnostics, allowImmediate: false);
                    RequireReadable(operands[2], declared, block.Id, line, diagnostics, allowImmediate: true);
                }

                break;
        }
    }

    /// <summary>LIST_PUSH / MAP_SET 的写入类指令。</summary>
    private static void AnalyzeCollectionWriteInstruction(ParsedInstruction instruction, PrtaBlock block, HashSet<string> declared, List<PrtaDiagnostic> diagnostics)
    {
        var op = instruction.Opcode;
        var line = instruction.Line;
        var operands = instruction.Operands;

        switch (op)
        {
            case "LIST_PUSH" or "MAP_SET":
            {
                var expected = op == "LIST_PUSH" ? 2 : 3;
                RequireOperandCount(operands, expected, op, block.Id, line, diagnostics);
                if (operands.Count >= 1)
                {
                    RequireWritableDest(operands[0], declared, block.Id, line, diagnostics);
                }

                for (var i = 1; i < operands.Count; i++)
                {
                    RequireReadable(operands[i], declared, block.Id, line, diagnostics, allowImmediate: true);
                }

                break;
            }
        }
    }

    /// <summary>EMIT 的去向前缀与（启用端口校验时）目标端口可写性。</summary>
    private static void AnalyzeEmitInstruction(ParsedInstruction instruction, PrtaBlock block, Dictionary<string, PrtaBlock> blockById, PrtaCompileOptions options, HashSet<string> declared, List<PrtaDiagnostic> diagnostics)
    {
        var op = instruction.Opcode;
        var line = instruction.Line;
        var operands = instruction.Operands;

        switch (op)
        {
            case "EMIT":
            {
                if (operands.Count != 2)
                {
                    diagnostics.Add(new PrtaDiagnostic("E_LEX", block.Id, line, "EMIT 需要恰好两个操作数：去向 与 值。"));
                    break;
                }

                var dest = operands[0];
                if (dest.Kind is not (OperandKind.PortRef or OperandKind.VarRef))
                {
                    diagnostics.Add(new PrtaDiagnostic("E_LEX", block.Id, line, "EMIT 的去向必须带 @（端口）或 $（变量）前缀：" + dest.Text));
                    break;
                }

                RequireReadable(operands[1], declared, block.Id, line, diagnostics, allowImmediate: true);
                if (options.ValidatePorts && dest.Kind == OperandKind.PortRef)
                {
                    var portId = dest.Detail!;
                    if (!IsReadablePort(portId, block, blockById))
                    {
                        diagnostics.Add(new PrtaDiagnostic("E_OUTPUT", block.Id, line, "EMIT 的目标端口不存在或文档顺序晚于本块：@" + portId));
                    }
                }

                break;
            }
        }
    }

    private static bool IsReadablePort(string portId, PrtaBlock current, Dictionary<string, PrtaBlock> blockById)
    {
        if (!blockById.TryGetValue(portId, out var target))
        {
            return false;
        }

        if (!string.Equals(target.BlockType, "prtopt", StringComparison.Ordinal))
        {
            return false;
        }

        return target.DocumentIndex <= current.DocumentIndex;
    }
}
