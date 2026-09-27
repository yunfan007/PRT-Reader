using Prt.Core.Syntax;

namespace Prt.Core.Structure;

/// <summary>术语表条目（`meta.glossary`，标准 9.1 / 7.4）。</summary>
public sealed class GlossaryEntry
{
    public GlossaryEntry(string term, string definition)
    {
        Term = term;
        Definition = definition;
    }

    /// <summary>术语名，同时作为引用注册表的键。</summary>
    public string Term { get; }

    /// <summary>术语定义文本，用于 `{术语}` 的悬停浮层。</summary>
    public string Definition { get; }
}

/// <summary>`::: refs` 块中的一条登记项（标准 9.4）。</summary>
public sealed class RefEntry
{
    /// <summary>别名，成为新的注册键。</summary>
    public string Alias { get; set; } = string.Empty;

    /// <summary>目标 ID（可含转义 `\|`）。</summary>
    public string TargetId { get; set; } = string.Empty;

    /// <summary>默认显示名；为空时 `[[ref]]` 显示目标自动编号、`{…}` 显示别名本身。</summary>
    public string? DefaultDisplay { get; set; }

    public int Line { get; set; } = 1;

    public int Column { get; set; } = 1;
}

/// <summary>
/// 文档元数据（`::: meta`，标准 9.1）。同时作为计算变量的初始来源——
/// 但本实现不支持 COMP，故仅用于渲染元信息（标题、语言、主题、编号样式等）。
/// </summary>
public sealed class PrtMetadata
{
    public string? Title { get; set; }

    public string? Subtitle { get; set; }

    public List<string> Authors { get; } = new();

    public string? Version { get; set; }

    /// <summary>日期文本（YYYY-MM-DD）。</summary>
    public string? Date { get; set; }

    /// <summary>时区标识（仅显示用途，标准 2.2 / 9.1）。</summary>
    public string? Timezone { get; set; }

    /// <summary>语言标签，如 zh-CN（标准 6.1）。</summary>
    public string? Language { get; set; }

    /// <summary>主题名（内置或自定义）。</summary>
    public string? Theme { get; set; }

    public List<string> Tags { get; } = new();

    /// <summary>是否生成文档级目录。</summary>
    public bool? Toc { get; set; }

    /// <summary>章节编号样式（标准 9.2），null 表示使用默认 mixed。</summary>
    public NumberingStyle? Numeration { get; set; }

    /// <summary>打印页面尺寸，仅对打印/PDF 目标生效。</summary>
    public string? Page { get; set; }

    /// <summary>打印页边距，仅对打印/PDF 目标生效。</summary>
    public string? Margin { get; set; }

    /// <summary>严格模式开关；null 表示未声明（按渲染器配置）。</summary>
    public bool? Strict { get; set; }

    /// <summary>术语表。</summary>
    public List<GlossaryEntry> Glossary { get; } = new();

    /// <summary>自定义键（以 `custom.` 前缀扩展，标准简明版 9.1）。</summary>
    public Dictionary<string, string> Custom { get; } = new(StringComparer.Ordinal);
}
