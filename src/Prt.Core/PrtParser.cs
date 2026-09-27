using Prt.Core.Diagnostics;
using Prt.Core.Parsing;
using Prt.Core.Structure;
using Prt.Core.Syntax;
using Prt.Core.Text;

namespace Prt.Core;

/// <summary>
/// PRT 解析器对外门面。按《PRT 标准》5.1 节的流水线组织：
/// 词法分析 → 语法解析 → 结构解析 →（受限计算，本实现不支持，按第 13 章降级）→ 渲染。
/// </summary>
public static class PrtParser
{
    /// <summary>解析一份 PRT 源文本。</summary>
    public static PrtDocument Parse(string? text, PrtOptions? options = null)
    {
        var requested = options ?? new PrtOptions();
        var source = SourceText.From(text);

        // 严格模式决策（标准 14.4）：渲染器配置优先于 meta.strict，再退化为默认严格。
        var metaStrict = PreScanStrictMode(source);
        var effectiveStrict = requested.StrictOverride ?? metaStrict ?? requested.Strict;

        var effectiveOptions = requested.Clone();
        effectiveOptions.Strict = effectiveStrict;

        var diagnostics = new DiagnosticBag();
        var blockParser = new BlockParser(source, effectiveOptions, diagnostics);
        var root = blockParser.ParseDocument();

        var metadata = ExtractMetadata(root, effectiveOptions, diagnostics);

        // 严格模式可经 meta.strict 声明为宽松（渲染器配置优先的规则已在上面处理）。
        var (registry, structure) = StructureResolver.Resolve(root, metadata, effectiveOptions, diagnostics);
        NestingValidator.Validate(root, effectiveOptions, diagnostics);

        var hasComputation = ContainsComputation(root);
        if (hasComputation)
        {
            // 【COMP 不支持】按标准第 13 章降级：不报错，仅给出一次性说明性警告。
            diagnostics.Warn(
                PrtDiagnosticCodes.ComputationNotSupported,
                "文档包含受限计算结构（COMP）。本工具声明 EXT 等级，不支持 COMP；"
                + "相关内容已按《PRT 标准》第 13 章降级处理（插值输出原文、计算块输出源码）。",
                1,
                1);
        }

        return new PrtDocument(source, root, metadata, registry, structure, diagnostics, effectiveOptions)
        {
            HasUnsupportedComputation = hasComputation,
        };
    }

    /// <summary>预扫描 meta 块中的 `strict` 键，避免「解析前需要知道模式」的顺序问题。</summary>
    private static bool? PreScanStrictMode(SourceText source)
    {
        var inMeta = false;
        for (var i = 0; i < source.LineCount; i++)
        {
            var text = source.Lines[i].Text.Trim();
            var fence = ParsingHelpers.ParseFenceLine(source.Lines[i].Text);
            if (fence.IsFence)
            {
                if (!fence.IsEnd && string.Equals(fence.TypeName, "meta", StringComparison.OrdinalIgnoreCase))
                {
                    inMeta = true;
                    continue;
                }
                if (fence.IsEnd && inMeta)
                {
                    inMeta = false;
                    continue;
                }
            }

            if (!inMeta)
            {
                continue;
            }

            var colon = text.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }
            var key = text[..colon].Trim();
            if (!key.Equals("strict", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var value = text[(colon + 1)..].Trim();
            if (ParsingHelpers.TryParseBoolean(value, out var strict))
            {
                return strict;
            }
        }
        return null;
    }

    /// <summary>提取文档元数据（取首个 meta 块；多个 meta 块时给出提示）。</summary>
    private static PrtMetadata ExtractMetadata(
        DocumentBlock root,
        PrtOptions options,
        DiagnosticBag diagnostics)
    {
        PrtMetadata? result = null;
        var count = 0;

        foreach (var block in PrtTree.WalkBlocks(root))
        {
            if (block is not MetaBlock meta)
            {
                continue;
            }
            count++;
            if (result is null)
            {
                result = meta.Metadata;
            }
        }

        if (count > 1)
        {
            diagnostics.Warn(
                PrtDiagnosticCodes.InvalidMetaEntry,
                $"文档包含 {count} 个 `::: meta` 块，仅首个生效",
                1,
                1);
        }

        return result ?? new PrtMetadata();
    }

    /// <summary>判断文档是否含计算块（COMP）。</summary>
    private static bool ContainsComputation(DocumentBlock root)
    {
        foreach (var block in PrtTree.WalkBlocks(root))
        {
            if (block is ComputationBlock)
            {
                return true;
            }
        }
        return false;
    }
}
