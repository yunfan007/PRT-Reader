namespace Prt.Prta.Compiler;

/// <summary>静态校验器（第 5.1 阶段 ①②；错误码口径见附录 C）。</summary>
internal static partial class PrtaAnalyzer
{
    private static readonly HashSet<string> ComponentUiKinds = new(["button", "input", "select", "checkbox"], StringComparer.Ordinal);
    private static readonly HashSet<string> EventNames = new(["click", "change", "submit"], StringComparer.Ordinal);
    private static readonly Dictionary<string, string> EventUiMatch = new(StringComparer.Ordinal)
    {
        ["click"] = "button",
        ["change"] = "input|select|checkbox",
        ["submit"] = "input",
    };

    public static List<BlockAnalysis> Analyze(IReadOnlyList<PrtaBlock> blocks, PrtaCompileOptions options, List<PrtaDiagnostic> diagnostics)
    {
        // 白名单函数：操作码（大写，或冲突时的 FN_ 前缀形式）→ 函数原名。
        var functions = PrtaParser.BuildFunctionMap(options.Whitelist);

        // ── 文档级：id 唯一（E_SIGNATURE）──
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in blocks)
        {
            if (!PrtaParser.IsIdentifier(block.Id))
            {
                diagnostics.Add(new PrtaDiagnostic("E_SIGNATURE", block.Id, 0, "块 id 含非标识符字符，无法映射为 __fn_<id>：" + block.Id));
            }

            if (!seenIds.Add(block.Id))
            {
                diagnostics.Add(new PrtaDiagnostic("E_SIGNATURE", block.Id, 0, "块 id 在文档作用域内重复。"));
            }
        }

        var analyses = new List<BlockAnalysis>(blocks.Count);
        var blockById = new Dictionary<string, PrtaBlock>(StringComparer.Ordinal);
        foreach (var block in blocks)
        {
            if (!blockById.TryAdd(block.Id, block))
            {
                blockById[block.Id] = block; // 重复者取首个声明（错误已记录）
            }
        }

        foreach (var block in blocks)
        {
            analyses.Add(AnalyzeBlock(block, blockById, options, functions, diagnostics));
        }

        return analyses;
    }

    private static BlockAnalysis AnalyzeBlock(PrtaBlock block, Dictionary<string, PrtaBlock> blockById, PrtaCompileOptions options, IReadOnlyDictionary<string, string> functions, List<PrtaDiagnostic> diagnostics)
    {
        var signature = PrtaParser.ParseSignature(block.Sig, diagnostics, block.Id);

        // 书写签名标识时必须与 id 逐字节相同（11.4）。
        if (signature?.Identifier is { } identifier && identifier != block.Id)
        {
            diagnostics.Add(new PrtaDiagnostic("E_SIGNATURE", block.Id, 0, "签名标识与块 id 不一致：" + identifier + " ≠ " + block.Id));
        }

        if (signature is { Params.Count: > 8 })
        {
            // 个数超限已在解析时记录；此处不再重复。
        }

        // ── 交互组件块（10.8）──
        if (string.Equals(block.BlockType, "prtui", StringComparison.Ordinal))
        {
            // unsafe 与 type="prtui" 不得同时声明（10.12.1）。
            if (IsUnsafeBlock(block))
            {
                diagnostics.Add(new PrtaDiagnostic("E_UNSAFE", block.Id, 0, "unsafe=\"true\" 与 type=\"prtui\" 不得同时声明。"));
            }

            ValidateComponent(block, signature, blockById, diagnostics);
            return new BlockAnalysis
            {
                Block = block,
                Signature = signature,
                Instructions = [],
                VarDeclarations = [],
                ParamBindings = [],
            };
        }

        var instructions = PrtaParser.ParseBody(block.Body, block.Id, functions, diagnostics);
        var varDecls = new List<VarDecl>();
        var paramBindings = new Dictionary<string, int>(StringComparer.Ordinal);
        if (signature is not null)
        {
            for (var i = 0; i < signature.Params.Count; i++)
            {
                paramBindings[signature.Params[i].Name] = i;
            }
        }

        var executable = new List<ParsedInstruction>(instructions.Count);
        var declared = new HashSet<string>(paramBindings.Keys, StringComparer.Ordinal);

        foreach (var instruction in instructions)
        {
            if (instruction.Opcode == "VAR")
            {
                AnalyzeVarDeclaration(instruction, block.Id, paramBindings, varDecls, declared, functions, diagnostics);
                continue;
            }

            AnalyzeInstruction(instruction, block, blockById, options, signature, paramBindings, declared, diagnostics);
            executable.Add(instruction);
        }

        return new BlockAnalysis
        {
            Block = block,
            Signature = signature,
            Instructions = executable,
            VarDeclarations = varDecls,
            ParamBindings = paramBindings,
        };
    }

    /// <summary>块是否声明为不安全块（<c>unsafe="true"</c>，10.12.1）。</summary>
    public static bool IsUnsafeBlock(PrtaBlock block)
        => block.Attributes.TryGetValue("unsafe", out var value)
           && string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
}
