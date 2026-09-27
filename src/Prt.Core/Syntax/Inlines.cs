namespace Prt.Core.Syntax;

/// <summary>纯文本字面量。</summary>
public sealed class TextInline : PrtInline
{
    public TextInline(string text) => Text = text;

    public string Text { get; }
}

/// <summary>强调：加粗 / 斜体 / 加粗斜体（标准 7.1）。</summary>
public sealed class EmphasisInline : PrtInline
{
    public bool Bold { get; set; }

    public bool Italic { get; set; }

    public List<PrtInline> Children { get; } = new();
}

/// <summary>删除线 `~~…~~`（标准 7.1）。</summary>
public sealed class StrikeInline : PrtInline
{
    public List<PrtInline> Children { get; } = new();
}

/// <summary>行内代码 `` `…` ``（标准 7.1）。内容不解析任何其他标记。</summary>
public sealed class CodeInline : PrtInline
{
    public CodeInline(string text) => Text = text;

    public string Text { get; }
}

/// <summary>链接 `[文字](url)` 与图片 `![alt](src)`（标准 7.1）。</summary>
public sealed class LinkInline : PrtInline
{
    public bool IsImage { get; set; }

    public string Url { get; set; } = string.Empty;

    /// <summary>图片替代文本（IsImage 为 true 时使用）。</summary>
    public string Alt { get; set; } = string.Empty;

    /// <summary>链接显示文字（IsImage 为 false 时使用）。</summary>
    public List<PrtInline> Children { get; } = new();
}

/// <summary>高亮 `==…==`（标准 7.2）。</summary>
public sealed class HighlightInline : PrtInline
{
    public List<PrtInline> Children { get; } = new();
}

/// <summary>下划线 `+…+`（标准 7.2）。</summary>
public sealed class UnderlineInline : PrtInline
{
    public List<PrtInline> Children { get; } = new();
}

/// <summary>上标 `^…^`（标准 7.2）。</summary>
public sealed class SuperscriptInline : PrtInline
{
    public List<PrtInline> Children { get; } = new();
}

/// <summary>下标 `~…~`（标准 7.2）。</summary>
public sealed class SubscriptInline : PrtInline
{
    public List<PrtInline> Children { get; } = new();
}

/// <summary>
/// 脚注 `^[注文]`（标准 7.2，EXT）。编号与脚注区排序在结构解析阶段按物理出现顺序确定。
/// </summary>
public sealed class FootnoteInline : PrtInline
{
    public string Content { get; set; } = string.Empty;

    /// <summary>结构解析阶段填充的脚注序号（从 1 起）；0 表示尚未分配。</summary>
    public int Number { get; set; }
}

/// <summary>键盘按键 `[[kbd:键名]]`（标准 7.3，CORE）。</summary>
public sealed class KbdInline : PrtInline
{
    public string Keys { get; set; } = string.Empty;
}

/// <summary>目录 `[[contents]]`（标准 9.3，CORE）。</summary>
public sealed class ContentsInline : PrtInline
{
    /// <summary>最大层级；null 表示全部层级。</summary>
    public int? Depth { get; set; }

    /// <summary>目录模式：all（默认）或 numbered。</summary>
    public string Mode { get; set; } = "all";
}

/// <summary>交叉引用 `[[ref:目标ID|显示名]]`（标准 9.4，EXT）。</summary>
public sealed class RefInline : PrtInline
{
    public string TargetId { get; set; } = string.Empty;

    /// <summary>自定义显示文本；为空时使用目标的自动编号。</summary>
    public string? DisplayText { get; set; }

    /// <summary>结构解析阶段填充的最终显示文本。</summary>
    public string? ResolvedText { get; set; }

    /// <summary>结构解析阶段填充的目标块（可为空表示未登记）。</summary>
    public IReferenceable? ResolvedTarget { get; set; }
}

/// <summary>术语链接 `{术语}` 或 `{术语|显示名}`（标准 7.4，EXT）。</summary>
public sealed class TermLinkInline : PrtInline
{
    public string Key { get; set; } = string.Empty;

    public string? DisplayText { get; set; }

    /// <summary>结构解析阶段填充的最终显示文本。</summary>
    public string? ResolvedText { get; set; }

    /// <summary>悬停显示的定义文本（来自 glossary，可选）。</summary>
    public string? Definition { get; set; }

    /// <summary>是否已登记为有效目标。</summary>
    public bool IsRegistered { get; set; }
}

/// <summary>前景色 `[[color:命名色]]…[[/color]]`（标准 7.5，CORE）。</summary>
public sealed class ColorInline : PrtInline
{
    public NamedColor Color { get; set; }

    public List<PrtInline> Children { get; } = new();
}

/// <summary>背景色 `[[bg:命名色]]…[[/bg]]`（标准 7.5，CORE）。</summary>
public sealed class BackgroundColorInline : PrtInline
{
    public NamedColor Color { get; set; }

    public List<PrtInline> Children { get; } = new();
}

/// <summary>字号 `[[size:档位]]…[[/size]]`（标准 7.5，CORE）。</summary>
public sealed class SizeInline : PrtInline
{
    public FontSizeScale Size { get; set; }

    public List<PrtInline> Children { get; } = new();
}

/// <summary>
/// 内联插值 `{{ 表达式 }}`（标准 11.1，COMP）。
/// 【COMP 不支持】本实现不进行求值，渲染时按标准第 13 章降级为原文 `{{ … }}`。
/// </summary>
public sealed class InterpolationInline : PrtInline
{
    public InterpolationInline(string raw)
    {
        Raw = raw;
    }

    /// <summary>花括号内部的原始表达式文本（含原有空白）。</summary>
    public string Raw { get; }

    /// <summary>降级输出文本：`{{` + 原始内容 + `}}`。</summary>
    public string LiteralText => "{{" + Raw + "}}";
}

/// <summary>
/// 日期字面量 `@YYYY-MM-DD[...]`（标准 11.2.2，COMP）。
/// 【COMP 不支持】不解析为日期值，按 6.2/6.4 节「字面输出」处理。
/// </summary>
public sealed class DateLiteralInline : PrtInline
{
    public DateLiteralInline(string text) => Text = text;

    public string Text { get; }
}

/// <summary>软换行（同一段落内的换行）。</summary>
public sealed class LineBreakInline : PrtInline
{
}

/// <summary>
/// 无法识别、按字面输出的行内片段（降级路径）。例如未登记的指令、写坏的成对标记。
/// </summary>
public sealed class LiteralInline : PrtInline
{
    public LiteralInline(string text) => Text = text;

    public string Text { get; }
}
