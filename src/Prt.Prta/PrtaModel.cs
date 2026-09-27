namespace Prt.Prta.Compiler;

/// <summary>一个待编译的 PRTA 运行块（对应 <c>::: run lang="prt" ...</c> 围栏）。</summary>
/// <param name="Id">块的 <c>id=</c> 属性，文档作用域内唯一。</param>
/// <param name="Sig">原始签名文本；<c>null</c> 表示 <c>sig</c> 属性整体省略（＝无形参、返回未定）。</param>
/// <param name="BlockType">块类型：<c>null</c>（普通可执行块）、<c>"prtopt"</c>（输出端口块）或 <c>"prtui"</c>（交互组件块）。</param>
/// <param name="Attributes">其余属性（ui / label / bind / on / options 等）。</param>
/// <param name="DocumentIndex">块在文档中的声明序号（0 起），用于跨块写入顺序校验。</param>
/// <param name="Body">块体指令文本（围栏行之间的原始文本）。</param>
public sealed record PrtaBlock(
    string Id,
    string? Sig,
    string? BlockType,
    IReadOnlyDictionary<string, string> Attributes,
    int DocumentIndex,
    string Body);

/// <summary>签名的形参。</summary>
/// <param name="Name">形参名（自动绑定为块内名变量）。</param>
/// <param name="Type">类型标注；<c>null</c> 表示「未定型」，不做静态类型检查。</param>
public sealed record PrtaParam(string Name, string? Type);

/// <summary>解析后的签名（第 11 章）。</summary>
/// <param name="Identifier">书写在签名文本中的标识；<c>null</c> 表示未书写（块名恒取 <c>id</c>）。</param>
/// <param name="Params">形参表。</param>
/// <param name="ReturnType">返回类型；<c>null</c> 表示「未定」，不校验 <c>RETURN</c> 值。</param>
public sealed record PrtaSignature(string? Identifier, IReadOnlyList<PrtaParam> Params, string? ReturnType);

/// <summary>诊断（附录 C 错误码）。</summary>
/// <param name="Code">错误码，如 <c>E_LEX</c> / <c>E_SIGNATURE</c> / <c>E_OUTPUT</c> / <c>E_UI</c> / <c>E_TYPE</c>。</param>
/// <param name="BlockId">所属运行块的 id。</param>
/// <param name="Line">块体内 1 起的行号；0 表示块级或文档级问题。</param>
/// <param name="Message">通俗描述。</param>
public sealed record PrtaDiagnostic(string Code, string BlockId, int Line, string Message)
{
    public override string ToString() =>
        Line > 0 ? $"[{Code}] 块 {BlockId} 第 {Line} 行：{Message}" : $"[{Code}] 块 {BlockId}：{Message}";
}

/// <summary>编译选项。</summary>
public sealed class PrtaCompileOptions
{
    /// <summary>入口块 id（<c>env.entry</c> 指向的块）；缺省取文档中第一个非组件块。</summary>
    public string? EntryBlockId { get; init; }

    /// <summary>
    /// 白名单函数名集合（小写原名）。同时用于两件事：识别「函数名大写即操作码」的指令形式，
    /// 以及静态校验函数是否在白名单内（非 null 时启用校验）。
    /// <c>null</c> 表示采用 <see cref="PrtaParser.DefaultWhitelist"/> 作识别清单、不做 E_WHITELIST 校验。
    /// </summary>
    public IReadOnlySet<string>? Whitelist { get; init; }

    /// <summary>是否执行有限的静态类型检查（仅在双侧类型均可静态判定时进行，第 11.4 节）。</summary>
    public bool StaticTypeChecks { get; init; } = true;

    /// <summary>
    /// 是否校验端口存在性与跨块写入顺序（<c>EMIT @port</c> / <c>LOADPORT</c>）。
    /// 编译完整文档时应保持 <c>true</c>；编辑单个块（无文档上下文）时置 <c>false</c>，
    /// 允许 <c>EMIT @结果</c> 这类指向外部端口的写法通过静态检查。
    /// </summary>
    public bool ValidatePorts { get; set; } = true;
}

/// <summary>静态校验失败（携带全部诊断）。</summary>
public sealed class PrtaCompileException : Exception
{
    public PrtaCompileException(IReadOnlyList<PrtaDiagnostic> diagnostics)
        : base("PRTA 静态校验失败：" + diagnostics.Count + " 个问题。" + Environment.NewLine
               + string.Join(Environment.NewLine, diagnostics.Take(10).Select(d => "  " + d)))
    {
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<PrtaDiagnostic> Diagnostics { get; }
}
