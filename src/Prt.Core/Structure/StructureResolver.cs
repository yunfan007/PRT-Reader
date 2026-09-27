using System.Globalization;
using Prt.Core.Diagnostics;
using Prt.Core.Parsing;
using Prt.Core.Syntax;

namespace Prt.Core.Structure;

/// <summary>
/// 结构解析器（《PRT 标准》第 5.1 节流水线的「结构解析」阶段）。
/// 职责：收集 meta / 章节 / 图 / 表 / 公式的 id，生成编号与交叉引用表；
/// 按物理顺序编号脚注；汇总目录条目。
/// </summary>
internal sealed class StructureResolver
{
    private readonly PrtOptions _options;
    private readonly DiagnosticBag _diagnostics;
    private readonly ReferenceRegistry _registry = new();
    private readonly DocumentStructure _structure = new();

    /// <summary>编号计数（下标 1 起，最多支持 7 级）。</summary>
    private readonly int[] _counters = new int[8];

    private int _tableCounter;
    private int _figureCounter;
    private int _equationCounter;
    /// <summary>
    /// 文档级默认编号样式。<b>默认不编号</b>：普通标题不自动加「第 X 章 / 1.2」前缀，
    /// 以免与用户已在原文中手写的编号叠加造成显示错误；仅当物体验式地用
    /// <c>numeration</c>（整篇 meta 或「自动排布章节」的 <c>section</c>）显式开启自动编号时才编号。
    /// </summary>
    private NumberingStyle _defaultStyle = NumberingStyle.None;

    private StructureResolver(PrtOptions options, DiagnosticBag diagnostics)
    {
        _options = options;
        _diagnostics = diagnostics;
    }

    /// <summary>对文档执行结构解析。</summary>
    public static (ReferenceRegistry Registry, DocumentStructure Structure) Resolve(
        DocumentBlock document,
        PrtMetadata metadata,
        PrtOptions options,
        DiagnosticBag diagnostics)
    {
        var resolver = new StructureResolver(options, diagnostics);
        // 默认不自动编号（None）；仅当原文显式声明 numeration（meta 或 section 自动排布章节）时才编号。
        resolver._defaultStyle = metadata.Numeration ?? NumberingStyle.None;
        resolver.RegisterTargets(document, metadata);
        resolver.AssignNumbers(document.Children, 0, resolver._defaultStyle);
        resolver.ResolveReferences(document);
        return (resolver._registry, resolver._structure);
    }

    // ─────────────────────────── 第 1 步：引用注册 ───────────────────────────

    private void RegisterTargets(DocumentBlock document, PrtMetadata metadata)
    {
        // 具名块的 id（隐式登记）。
        foreach (var block in PrtTree.WalkBlocks(document))
        {
            switch (block)
            {
                case SectionBlock s when !string.IsNullOrWhiteSpace(s.Id):
                    RegisterBlock(s.Id!, s, ReferenceTargetKind.Section, block.Line, block.Column);
                    break;
                case TableBlock t when !string.IsNullOrWhiteSpace(t.Id):
                    RegisterBlock(t.Id!, t, ReferenceTargetKind.Table, block.Line, block.Column);
                    break;
                case FigureBlock f when !string.IsNullOrWhiteSpace(f.Id):
                    RegisterBlock(f.Id!, f, ReferenceTargetKind.Figure, block.Line, block.Column);
                    break;
                case EquationBlock e when !string.IsNullOrWhiteSpace(e.Id):
                    RegisterBlock(e.Id!, e, ReferenceTargetKind.Equation, block.Line, block.Column);
                    break;
            }
        }

        // 术语表（隐式登记）。
        foreach (var entry in metadata.Glossary)
        {
            _registry.RegisterTerm(entry.Term, entry.Definition);
        }

        // `::: refs` 显式别名（与文本先后顺序无关）。
        foreach (var block in PrtTree.WalkBlocks(document))
        {
            if (block is not RefsBlock refs)
            {
                continue;
            }
            foreach (var entry in refs.Entries)
            {
                if (entry.Alias.Length == 0)
                {
                    continue;
                }
                _registry.RegisterAlias(entry.Alias, entry.TargetId, entry.DefaultDisplay, out var replaced);
                if (replaced)
                {
                    _diagnostics.Warn(
                        PrtDiagnosticCodes.DuplicateAlias,
                        $"别名「{entry.Alias}」重复声明，以最后一条为准",
                        entry.Line,
                        entry.Column);
                }
            }
        }
    }

    private void RegisterBlock(string id, IReferenceable block, ReferenceTargetKind kind, int line, int column)
    {
        if (!_registry.RegisterBlock(id, block, kind))
        {
            _diagnostics.Warn(
                PrtDiagnosticCodes.DuplicateId,
                $"引用目标 id「{id}」重复登记，后者被忽略",
                line,
                column);
        }
    }

    // ─────────────────────────── 第 2 步：编号与目录 ───────────────────────────

    private void AssignNumbers(List<PrtBlock> blocks, int sectionDepth, NumberingStyle inheritedStyle)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case SectionBlock section:
                {
                    var level = Math.Min(sectionDepth + 1, 7);
                    Increment(level);
                    // numeration 可局部覆盖，并向下继承。
                    var effective = section.Numeration ?? inheritedStyle;
                    section.EffectiveStyle = effective;
                    section.NumberLevel = level;
                    section.NumberCounters = (int[])_counters.Clone();
                    section.AssignedNumber = NumberingFormatter.FormatHeadingNumber(_counters, level, effective);

                    _structure.Toc.Add(new TocEntry
                    {
                        Level = level,
                        Number = section.AssignedNumber,
                        Title = section.Title ?? string.Empty,
                        AnchorId = section.Id,
                        IsSection = true,
                        IsNumbered = effective != NumberingStyle.None,
                        Source = section,
                    });

                    AssignNumbers(section.Children, level, effective);
                    continue;
                }

                case HeadingBlock heading:
                {
                    var level = Math.Clamp(heading.Level, 1, 7);
                    Increment(level);
                    heading.AssignedNumber = NumberingFormatter.FormatHeadingNumber(_counters, level, inheritedStyle);
                    _structure.Toc.Add(new TocEntry
                    {
                        Level = level,
                        Number = heading.AssignedNumber,
                        Title = heading.PlainText,
                        AnchorId = null,
                        IsSection = false,
                        IsNumbered = inheritedStyle != NumberingStyle.None,
                        Source = heading,
                    });
                    break;
                }

                case TableBlock table:
                    // 编号进入导出文件与目录，必须跨区域稳定（同 NumberingFormatter）。
                    table.AssignedNumber = (++_tableCounter).ToString(CultureInfo.InvariantCulture);
                    break;

                case FigureBlock figure:
                    figure.AssignedNumber = (++_figureCounter).ToString(CultureInfo.InvariantCulture);
                    break;

                case EquationBlock equation:
                    equation.AssignedNumber = (++_equationCounter).ToString(CultureInfo.InvariantCulture);
                    break;

                // 【COMP 不支持】计算块不参与编号计算，也不进入目录。
                case ComputationBlock:
                    break;
            }

            CollectFootnotes(block);

            if (block is SectionBlock)
            {
                // 已在上面递归处理。
                continue;
            }

            AssignNumbers(ChildrenOf(block), sectionDepth, inheritedStyle);
        }
    }

    private static List<PrtBlock> ChildrenOf(PrtBlock block) => PrtTree.GetChildren(block).ToList();

    private void Increment(int level)
    {
        _counters[level]++;
        for (var i = level + 1; i < _counters.Length; i++)
        {
            _counters[i] = 0;
        }
    }

    // ─────────────────────────── 脚注编号 ───────────────────────────

    private void CollectFootnotes(PrtBlock block)
    {
        foreach (var list in OwnInlineLists(block))
        {
            foreach (var inline in list)
            {
                if (inline is FootnoteInline footnote)
                {
                    footnote.Number = _structure.RegisterFootnote(footnote.Content);
                }
            }
        }
    }

    /// <summary>取某块「自身」的行内列表（不含后代块），用于按物理顺序编号脚注。</summary>
    private static IEnumerable<List<PrtInline>> OwnInlineLists(PrtBlock block)
    {
        switch (block)
        {
            case HeadingBlock h:
                yield return h.Inlines;
                break;
            case ParagraphBlock p:
                yield return p.Inlines;
                break;
            case ListItemBlock li:
                yield return li.Inlines;
                break;
            case TableBlock t:
                foreach (var cell in PrtTree.EnumerateCells(t.Table)) yield return cell.Inlines;
                break;
            case PipeTableBlock pt:
                foreach (var cell in PrtTree.EnumerateCells(pt.Table)) yield return cell.Inlines;
                break;
        }
    }

    // ─────────────────────────── 第 3 步：引用解析 ───────────────────────────

    private void ResolveReferences(DocumentBlock document)
    {
        foreach (var list in PrtTree.EnumerateInlineLists(document))
        {
            PrtTree.VisitInlines(list, inline =>
            {
                switch (inline)
                {
                    case RefInline reference:
                        ResolveReference(reference);
                        break;
                    case TermLinkInline term:
                        ResolveTerm(term);
                        break;
                }
            });
        }
    }

    private void ResolveReference(RefInline reference)
    {
        var target = _registry.Resolve(reference.TargetId);
        if (target is null || target.Block is null)
        {
            ParsingHelpers.Report(
                _diagnostics,
                _options.Strict,
                PrtDiagnosticCodes.UnregisteredReference,
                $"引用未登记的目标「{reference.TargetId}」，已按降级输出",
                reference.Line,
                reference.Column);
            reference.ResolvedText = reference.DisplayText ?? reference.TargetId;
            return;
        }

        reference.ResolvedTarget = target.Block;
        reference.ResolvedText = reference.DisplayText
                                  ?? ReferenceRegistry.DefaultReferenceText(target, _defaultStyle);
    }

    private void ResolveTerm(TermLinkInline term)
    {
        var target = _registry.Resolve(term.Key);
        if (target is null)
        {
            ParsingHelpers.Report(
                _diagnostics,
                _options.Strict,
                PrtDiagnosticCodes.UnregisteredReference,
                $"术语「{term.Key}」未在前置注册表登记，已按降级输出",
                term.Line,
                term.Column);
            term.IsRegistered = false;
            term.ResolvedText = term.DisplayText ?? term.Key;
            return;
        }

        term.IsRegistered = true;
        term.Definition = target.Definition;
        term.ResolvedText = term.DisplayText
                            ?? target.DisplayName
                            ?? (target.Block?.Caption ?? target.Key);
    }
}
