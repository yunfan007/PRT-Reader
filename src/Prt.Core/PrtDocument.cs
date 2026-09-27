using Prt.Core.Diagnostics;
using Prt.Core.Structure;
using Prt.Core.Syntax;
using Prt.Core.Text;

namespace Prt.Core;

/// <summary>
/// 一份已解析的 PRT 文档：语法树 + 元数据 + 引用注册表 + 结构解析结果 + 诊断集合。
/// 这是 Core 对外的唯一数据载体，UI 层与导出器均基于它工作。
/// </summary>
public sealed class PrtDocument
{
    internal PrtDocument(
        SourceText source,
        DocumentBlock root,
        PrtMetadata metadata,
        ReferenceRegistry references,
        DocumentStructure structure,
        DiagnosticBag diagnostics,
        PrtOptions options)
    {
        Source = source;
        Root = root;
        Metadata = metadata;
        References = references;
        Structure = structure;
        Diagnostics = diagnostics;
        Options = options;
    }

    /// <summary>规范化后的源文本（行尾统一为 LF）。</summary>
    public SourceText Source { get; }

    /// <summary>语法树根节点。</summary>
    public DocumentBlock Root { get; }

    /// <summary>文档元数据（`::: meta`）。</summary>
    public PrtMetadata Metadata { get; }

    /// <summary>引用注册表（标准 9.4）。</summary>
    public ReferenceRegistry References { get; }

    /// <summary>结构解析结果（目录、脚注）。</summary>
    public DocumentStructure Structure { get; }

    /// <summary>解析与结构解析阶段产生的全部诊断。</summary>
    public DiagnosticBag Diagnostics { get; }

    /// <summary>本次解析使用的有效选项（含最终生效的严格模式）。</summary>
    public PrtOptions Options { get; }

    /// <summary>是否存在致命错误（严格模式命中任一「应」级错误时）。</summary>
    public bool HasErrors => Diagnostics.HasErrors;

    /// <summary>是否存在 COMP 计算结构（本实现不支持，已按第 13 章降级）。</summary>
    public bool HasUnsupportedComputation { get; internal set; }

    /// <summary>按标准 14.4 排序去重后的诊断列表。</summary>
    public IReadOnlyList<Diagnostic> SortedDiagnostics => Diagnostics.SortedAndDeduplicated();

    private IReadOnlyList<ThemeBlock>? _themeBlocks;

    /// <summary>
    /// 文档内声明过的全部 `::: theme` 块（按文档物理顺序）。
    /// 查看器据此叠加自定义主题（标准 12.2）：<c>PrtTheme.Resolve(名, doc.ThemeBlocks, out _)</c>。
    /// </summary>
    public IReadOnlyList<ThemeBlock> ThemeBlocks
    {
        get
        {
            if (_themeBlocks is null)
            {
                var list = new List<ThemeBlock>();
                foreach (var block in PrtTree.WalkBlocks(Root))
                {
                    if (block is ThemeBlock theme)
                    {
                        list.Add(theme);
                    }
                }
                _themeBlocks = list;
            }
            return _themeBlocks;
        }
    }
}
