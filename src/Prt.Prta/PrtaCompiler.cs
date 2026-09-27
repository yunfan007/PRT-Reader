namespace Prt.Prta.Compiler;

/// <summary>
/// PRTA → JavaScript 编译门面。
/// <para>用法：把文档中每个 <c>::: run lang="prt" …</c> 围栏构造为 <see cref="PrtaBlock"/>
/// （按文档声明顺序赋予 <see cref="PrtaBlock.DocumentIndex"/>），调用
/// <see cref="CompileDocument"/> 得到整份目标程序（附录 D 骨架）。</para>
/// </summary>
public static class PrtaCompiler
{
    /// <summary>编译整个文档的运行块集合。静态校验失败时抛出 <see cref="PrtaCompileException"/>。</summary>
    public static string CompileDocument(IEnumerable<PrtaBlock> blocks, PrtaCompileOptions? options = null)
    {
        var result = TryCompileDocument(blocks, options, out var js, out var diagnostics);
        if (!result)
        {
            throw new PrtaCompileException(diagnostics);
        }

        return js;
    }

    /// <summary>尝试编译；返回 false 时 <paramref name="diagnostics"/> 携带全部问题。</summary>
    public static bool TryCompileDocument(
        IEnumerable<PrtaBlock> blocks,
        PrtaCompileOptions? options,
        out string javaScript,
        out IReadOnlyList<PrtaDiagnostic> diagnostics)
    {
        var blockList = blocks as IReadOnlyList<PrtaBlock> ?? [.. blocks];
        var effectiveOptions = options ?? new PrtaCompileOptions();
        var allDiagnostics = new List<PrtaDiagnostic>();

        var analyses = PrtaAnalyzer.Analyze(blockList, effectiveOptions, allDiagnostics);
        foreach (var analysis in analyses)
        {
            if (!analysis.IsComponent)
            {
                PrtaAnalyzer.CheckControlFlow(analysis.Instructions, analysis.Block.Id, allDiagnostics);
            }
        }

        if (allDiagnostics.Count > 0)
        {
            javaScript = string.Empty;
            diagnostics = allDiagnostics;
            return false;
        }

        javaScript = PrtaCodeGenerator.GenerateDocument(analyses, allDiagnostics, effectiveOptions);
        diagnostics = [];
        return true;
    }

    /// <summary>
    /// 编译单个块（不做跨块校验：CALL 目标存在性、EMIT/LOADPORT 端口写序、组件处理器存在性
    /// 等文档级检查会被跳过）。适用于独立编辑器场景的即时反馈。
    /// </summary>
    public static string CompileBlock(PrtaBlock block, PrtaCompileOptions? options = null)
    {
        var effectiveOptions = options ?? new PrtaCompileOptions();
        effectiveOptions.ValidatePorts = false; // 单块无文档上下文，端口存在性属文档级检查
        var diagnostics = new List<PrtaDiagnostic>();
        var analysis = PrtaAnalyzer.Analyze([block], effectiveOptions, diagnostics);
        PrtaAnalyzer.CheckControlFlow(analysis[0].Instructions, block.Id, diagnostics);
        if (diagnostics.Count > 0)
        {
            throw new PrtaCompileException(diagnostics);
        }

        // 单块模式不产出 env.entry（无文档上下文）。
        return PrtaCodeGenerator.GenerateDocument(analysis, diagnostics, new PrtaCompileOptions { EntryBlockId = block.Id });
    }
}
