using Prt.Core.Syntax;

namespace Prt.Core.Structure;

/// <summary>引用目标（标准 9.4 的引用注册表条目）。</summary>
public sealed class ReferenceTarget
{
    public ReferenceTarget(string key, ReferenceTargetKind kind)
    {
        Key = key;
        Kind = kind;
    }

    /// <summary>注册键（目标 id、术语名或别名）。</summary>
    public string Key { get; }

    /// <summary>目标种类。</summary>
    public ReferenceTargetKind Kind { get; }

    /// <summary>目标块（别名条目可能为空，需经 <see cref="SourceId"/> 二次解析）。</summary>
    public IReferenceable? Block { get; set; }

    /// <summary>别名指向的目标 id。</summary>
    public string? SourceId { get; set; }

    /// <summary>注册显示名（来自 `::: refs` 的 `| 默认显示名`）。</summary>
    public string? DisplayName { get; set; }

    /// <summary>术语定义（来自 `meta.glossary`），用于 `{术语}` 的悬停浮层。</summary>
    public string? Definition { get; set; }

    /// <summary>该键是否为显式登记的别名。</summary>
    public bool IsAlias => SourceId is not null && Block is null;
}

/// <summary>
/// 引用注册表（标准 9.4）。三条注册途径：具名块的 id、`meta.glossary` 术语、`::: refs` 显式别名。
/// 注册表在结构解析阶段一次性合并，与文本先后顺序无关。
/// </summary>
public sealed class ReferenceRegistry
{
    private readonly Dictionary<string, ReferenceTarget> _targets = new(StringComparer.Ordinal);

    /// <summary>全部注册键。</summary>
    public IReadOnlyDictionary<string, ReferenceTarget> Targets => _targets;

    /// <summary>注册具名块（section / table / figure / equation）。</summary>
    public bool RegisterBlock(string key, IReferenceable block, ReferenceTargetKind kind)
    {
        if (_targets.ContainsKey(key))
        {
            return false;
        }
        _targets[key] = new ReferenceTarget(key, kind) { Block = block };
        return true;
    }

    /// <summary>注册术语（`meta.glossary`）。</summary>
    public void RegisterTerm(string term, string definition)
    {
        _targets[term] = new ReferenceTarget(term, ReferenceTargetKind.Term) { Definition = definition };
    }

    /// <summary>注册别名（`::: refs`）。同一别名重复声明时以最后一条为准（标准 9.4）。</summary>
    public void RegisterAlias(string alias, string targetId, string? displayName, out bool replaced)
    {
        replaced = _targets.ContainsKey(alias);
        _targets[alias] = new ReferenceTarget(alias, ReferenceTargetKind.Alias)
        {
            SourceId = targetId,
            DisplayName = displayName,
        };
    }

    /// <summary>解析键；别名会沿 <see cref="ReferenceTarget.SourceId"/> 二次解析到目标块。</summary>
    public ReferenceTarget? Resolve(string key)
    {
        if (!_targets.TryGetValue(key, out var target))
        {
            return null;
        }
        if (target.Block is not null || target.SourceId is null)
        {
            return target;
        }
        if (_targets.TryGetValue(target.SourceId, out var resolved) && resolved.Block is not null)
        {
            // 用别名条目覆盖显示名，但指向真实目标块。
            return new ReferenceTarget(target.Key, resolved.Kind)
            {
                Block = resolved.Block,
                SourceId = resolved.Key,
                DisplayName = target.DisplayName,
                Definition = resolved.Definition,
            };
        }
        return target;
    }

    /// <summary>取某目标的默认引用文本（标准 9.4 映射表）。</summary>
    public static string DefaultReferenceText(ReferenceTarget target, NumberingStyle style)
    {
        var block = target.Block;
        if (block is null)
        {
            return target.DisplayName ?? target.Key;
        }

        switch (target.Kind)
        {
            case ReferenceTargetKind.Table:
                return style == NumberingStyle.None
                    ? (block.Caption ?? block.Id ?? target.Key)
                    : "表" + (block.AssignedNumber ?? "?");

            case ReferenceTargetKind.Figure:
                return style == NumberingStyle.None
                    ? (block.Caption ?? block.Id ?? target.Key)
                    : "图" + (block.AssignedNumber ?? "?");

            case ReferenceTargetKind.Equation:
                return style == NumberingStyle.None
                    ? (block.Id ?? target.Key)
                    : "式(" + (block.AssignedNumber ?? "?") + ")";

            case ReferenceTargetKind.Section:
                if (style == NumberingStyle.None)
                {
                    return block.Caption ?? block.Id ?? target.Key;
                }
                if (block is SectionBlock section && section.NumberCounters is not null)
                {
                    return NumberingFormatter.FormatSectionReference(
                        section.NumberCounters,
                        section.NumberLevel,
                        section.EffectiveStyle,
                        section.Title);
                }
                return block.AssignedNumber ?? block.Caption ?? block.Id ?? target.Key;

            default:
                return block.AssignedNumber ?? block.Caption ?? block.Id ?? target.Key;
        }
    }
}
