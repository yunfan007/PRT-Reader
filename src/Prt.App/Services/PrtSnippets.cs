using System.Text;

namespace Prt.App.Services;

/// <summary>插入片段的形态，决定「包裹选区」与「空行插入」的行为差异。</summary>
internal enum SnippetShape
{
    /// <summary>围栏 / 整块：开始行 + 内容 + 结束行，自成一段。</summary>
    Block,

    /// <summary>行前缀：给选区每一行加前缀（引用块、列表项）。</summary>
    LinePrefix,

    /// <summary>行内包裹：选区前后各加一段标记（加粗、斜体、行内代码等）。</summary>
    InlineWrap,
}

/// <summary>
/// 可插入的 PRT 块片段（对应《PRT 标准》8.3 语义块、8.5 结构块与 8.2 基础块）。
/// </summary>
/// <param name="Id">稳定标识。</param>
/// <param name="Group">所属分组（用于右键菜单二级菜单）。</param>
/// <param name="Label">菜单显示名。</param>
/// <param name="Gesture">右侧灰字提示（围栏类型名）。</param>
/// <param name="OpenBlock">开始行 / 行前缀；可为空。</param>
/// <param name="CloseBlock">结束行；可为空。开始与结束同为空时视为「纯内容块」。</param>
/// <param name="Body">默认内容（每行以 <c>\n</c> 分隔，实际换行符在生成时按文档统一）。</param>
/// <param name="SelectTarget">插入后自动选中、便于直接覆盖输入的文本片段。</param>
internal sealed record PrtSnippet(
    string Id,
    string Group,
    string Label,
    string Gesture,
    string OpenBlock = "",
    string CloseBlock = "",
    string Body = "",
    string? SelectTarget = null,
    SnippetShape Shape = SnippetShape.Block);

/// <summary>一次文本编辑：把 <see cref="Start"/>/<see cref="Length"/> 区间替换为 <see cref="Text"/>，并给出之后的选区。</summary>
/// <param name="Start">替换起点（原文索引）。</param>
/// <param name="Length">替换长度（原文）。</param>
/// <param name="Text">替换后的文本。</param>
/// <param name="SelectionStart">编辑后应建立的选区起点（新文本索引）。</param>
/// <param name="SelectionLength">编辑后应建立的选区长度。</param>
internal readonly record struct EditResult(int Start, int Length, string Text, int SelectionStart, int SelectionLength);

/// <summary>
/// 右键菜单「插入框 / 选中整行」背后的纯文本逻辑。
/// <para>
/// 全部为无副作用的静态函数，只做字符串换算，不接触任何 WPF 控件，便于自检覆盖。
/// 换行符一律按调用方传入的文本自适应（编辑器为 <c>\r\n</c>，纯文本文件可能为 <c>\n</c>）。
/// </para>
/// </summary>
internal static class PrtSnippets
{
    /// <summary>分组名：语义框（标准 8.3，13 个类型名）。</summary>
    public const string GroupSemantic = "语义框";

    /// <summary>分组名：结构块（标准 8.5）。</summary>
    public const string GroupStructure = "结构块";

    /// <summary>分组名：基础块（标准 8.2，Markdown 沿用语义）。</summary>
    public const string GroupBasic = "基础块";

    /// <summary>分组名：行内格式（标准 7 章文字效果）。</summary>
    public const string GroupInline = "行内格式";

    /// <summary>分组名：计算与指令（标准 11 章与 7.3 指令）。</summary>
    public const string GroupCompute = "计算与指令";

    private static readonly PrtSnippet[] Catalog =
    [
        // ───────────────────────── 语义块（标准 8.3） ─────────────────────────
        new("sem.note", GroupSemantic, "提示", "note", "::: note", "::: end note"),
        new("sem.info", GroupSemantic, "信息", "info", "::: info", "::: end info"),
        new("sem.tip", GroupSemantic, "技巧", "tip", "::: tip", "::: end tip"),
        new("sem.warning", GroupSemantic, "警告", "warning", "::: warning", "::: end warning"),
        new("sem.danger", GroupSemantic, "危险", "danger", "::: danger", "::: end danger"),
        new("sem.attention", GroupSemantic, "注意", "attention", "::: attention", "::: end attention"),
        new("sem.example", GroupSemantic, "示例", "example", "::: example", "::: end example"),
        new("sem.definition", GroupSemantic, "定义", "definition", "::: definition 术语", "::: end definition", SelectTarget: "术语"),
        new("sem.quote", GroupSemantic, "引用（语义）", "quote", "::: quote", "::: end quote"),
        new("sem.comment", GroupSemantic, "注释", "comment", "::: comment", "::: end comment"),
        new("sem.result", GroupSemantic, "结果", "result", "::: result", "::: end result"),
        new("sem.task", GroupSemantic, "任务清单", "task", "::: task", "::: end task", Body: "- [ ] "),
        new("sem.todo", GroupSemantic, "任务清单（别名 todo）", "todo", "::: todo", "::: end todo", Body: "- [ ] "),

        // ───────────────────────── 结构块（标准 8.5） ─────────────────────────
        new("str.section", GroupStructure, "章节（可嵌套、自动编号）", "section",
            "::: section id=\"sec-1\" title=\"新章节\"", "::: end section", SelectTarget: "新章节"),
        new("str.summary", GroupStructure, "摘要", "summary", "::: summary", "::: end summary"),
        new("str.figure", GroupStructure, "图（带编号与题注）", "figure",
            "::: figure id=\"fig-1\" caption=\"图题\"", "::: end figure",
            Body: "![](assets/图片.png)", SelectTarget: "assets/图片.png"),
        new("str.equation", GroupStructure, "公式（带编号）", "equation",
            "::: equation id=\"eq-1\" caption=\"公式\"", "::: end equation",
            Body: "E = mc^2", SelectTarget: "E = mc^2"),
        new("str.table", GroupStructure, "完整表格（带表题编号）", "table",
            "::: table id=\"t-1\" caption=\"表题\"", "::: end table",
            Body: "| 列一 | 列二 |\n| :-- | :-- |\n| 单元格 | 单元格 |"),
        new("str.collapse", GroupStructure, "折叠区域", "collapse",
            "::: collapse title=\"折叠标题\" default-open=false", "::: end collapse", SelectTarget: "折叠标题"),
        new("str.tabs", GroupStructure, "选项卡", "tabs",
            "::: tabs\n::: tab name=\"标签一\"", "::: end tab\n::: end tabs", SelectTarget: "标签一"),
        new("str.columns", GroupStructure, "分栏（两栏）", "columns",
            "::: columns cols=2\n::: column", "::: end column\n::: column\n\n::: end column\n::: end columns"),

        // ───────────────────────── 基础块（标准 8.2） ─────────────────────────
        new("base.quote", GroupBasic, "引用块", ">", "> ", Shape: SnippetShape.LinePrefix),
        new("base.ul", GroupBasic, "无序列表", "-", "- ", Shape: SnippetShape.LinePrefix),
        new("base.ol", GroupBasic, "有序列表", "1.", "1. ", Shape: SnippetShape.LinePrefix),
        new("base.check", GroupBasic, "任务清单项", "[ ]", "- [ ] ", Shape: SnippetShape.LinePrefix),
        new("base.code", GroupBasic, "代码块", "```", "```", "```"),
        new("base.pipe", GroupBasic, "管道表格", "|", Body: "| 列一 | 列二 |\n| :-- | :-- |\n| 单元格 | 单元格 |"),
        new("base.rule", GroupBasic, "分隔线", "---", Body: "---"),

        // ───────────────────────── 行内格式（标准 7 章） ─────────────────────────
        new("in.bold", GroupInline, "加粗", "**", "**", "**", Shape: SnippetShape.InlineWrap),
        new("in.italic", GroupInline, "斜体", "*", "*", "*", Shape: SnippetShape.InlineWrap),
        new("in.strike", GroupInline, "删除线", "~~", "~~", "~~", Shape: SnippetShape.InlineWrap),
        new("in.code", GroupInline, "行内代码", "`", "`", "`", Shape: SnippetShape.InlineWrap),
        new("in.highlight", GroupInline, "高亮", "==", "==", "==", Shape: SnippetShape.InlineWrap),
        new("in.term", GroupInline, "术语链接", "{ }", "{", "}", Shape: SnippetShape.InlineWrap),
        new("in.link", GroupInline, "链接", "[]()", "[", "](https://)", Shape: SnippetShape.InlineWrap),

        // ───────────────────────── 计算与指令（标准 11 章、7.3） ─────────────────────────
        new("cmp.set", GroupCompute, "赋值（set）", "::: set",
            "::: set 名称 = 表达式", Shape: SnippetShape.Block),
        new("cmp.value", GroupCompute, "填值（{{ }}）", "{{ }}", "{{ ", " }}", Shape: SnippetShape.InlineWrap),
        new("cmp.if", GroupCompute, "条件（if）", "::: if",
            "::: if 条件", "::: end if", Body: "内容", Shape: SnippetShape.Block),
        new("cmp.loop", GroupCompute, "循环（loop）", "::: loop",
            "::: loop 次数", "::: end loop", Body: "内容", Shape: SnippetShape.Block),
        new("cmp.run", GroupCompute, "受限运行（run）", "::: run",
            "::: run lang=\"prt\" id=\"示例\" sig=\"()->Integer\"", "::: end run",
            Body: "LOAD r0, 1\nRETURN r0", Shape: SnippetShape.Block),
        new("dir.contents", GroupCompute, "目录（[[contents]]）", "[[contents]]", Body: "[[contents]]"),
        new("dir.ref", GroupCompute, "定义 + 登记 + 引用（可解析的完整示例）", "[[ref:]]",
            Body: "::: table id=\"t1\" caption=\"表题\"\n| 列一 | 列二 |\n| :-- | :-- |\n| 单元格 | 单元格 |\n::: end table\n\n::: refs\n表一 = t1\n::: end refs\n\n引用：[[ref:t1]]",
            SelectTarget: "t1"),
    ];

    /// <summary>全部片段。</summary>
    public static IReadOnlyList<PrtSnippet> All => Catalog;

    /// <summary>取某分组的片段。</summary>
    public static IReadOnlyList<PrtSnippet> InGroup(string group)
        => Catalog.Where(snippet => snippet.Group == group).ToArray();

    /// <summary>按标识取片段（找不到返回 null）。</summary>
    public static PrtSnippet? ById(string id)
        => Catalog.FirstOrDefault(snippet => snippet.Id == id);

    // ─────────────────────────────── 位置换算 ───────────────────────────────

    /// <summary>推断文本使用的主换行符（含 <c>\r\n</c> 时用它，否则用 <c>\n</c>）。</summary>
    public static string NewlineOf(string text)
        => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>求 <paramref name="index"/> 所在逻辑行的起止（不含行尾换行符）。</summary>
    public static (int Start, int End) LineBounds(string text, int index)
    {
        index = Math.Clamp(index, 0, text.Length);

        var start = index;
        while (start > 0 && text[start - 1] is not ('\n' or '\r'))
        {
            start--;
        }

        var end = index;
        while (end < text.Length && text[end] is not ('\n' or '\r'))
        {
            end++;
        }

        return (start, end);
    }

    /// <summary>把 `起始 + 长度` 的选区扩张到完整行（不含末行换行符）。</summary>
    public static (int Start, int Length) ExpandToWholeLines(string text, int start, int length)
    {
        start = Math.Clamp(start, 0, text.Length);
        var end = Math.Clamp(start + length, 0, text.Length);

        // 选区恰好吞掉行尾换行符时，不应把下一行也视作被选中。
        if (end > start && text[end - 1] == '\n')
        {
            end--;
        }

        var (lineStart, _) = LineBounds(text, start);
        var (_, lineEnd) = LineBounds(text, end);
        return (lineStart, lineEnd - lineStart);
    }

    /// <summary>求「选中整行」应选中的区间（含行尾换行符，便于整体剪切 / 复制）。</summary>
    public static (int Start, int Length) WholeLineRange(string text, int index)
    {
        var (start, end) = LineBounds(text, index);
        while (end < text.Length && text[end] is '\r' or '\n')
        {
            end++;
        }

        return (start, end - start);
    }

    // ─────────────────────────────── 生成编辑 ───────────────────────────────

    /// <summary>
    /// 行内包裹：给选区（或光标处）加上成对标记，编辑后仍选中原文字，便于连续调整。
    /// </summary>
    public static EditResult WrapInline(string text, int start, int length, PrtSnippet snippet)
    {
        start = Math.Clamp(start, 0, text.Length);
        length = Math.Clamp(length, 0, text.Length - start);

        var open = snippet.OpenBlock;
        var close = string.IsNullOrEmpty(snippet.CloseBlock) ? open : snippet.CloseBlock;

        if (length == 0)
        {
            // 空选区：插入成对标记并把光标置于中间（可直接接着输入）。
            var pair = open + close;
            return new EditResult(start, 0, pair, start + open.Length, 0);
        }

        var selected = text.Substring(start, length);
        return new EditResult(start, length, open + selected + close, start + open.Length, selected.Length);
    }

    /// <summary>
    /// 有选区时：把选中内容（扩张到整行）包进片段。
    /// </summary>
    public static EditResult WrapSelection(string text, int selectionStart, int selectionLength, PrtSnippet snippet)
    {
        var newline = NewlineOf(text);

        // 行内包裹：选区前后各加标记；无选区时插入一对标记并把光标放中间。
        if (snippet.Shape == SnippetShape.InlineWrap)
        {
            return WrapInline(text, selectionStart, selectionLength, snippet);
        }

        if (snippet.Shape == SnippetShape.LinePrefix)
        {
            return ApplyLinePrefix(text, selectionStart, selectionLength, snippet.OpenBlock);
        }

        var (start, length) = ExpandToWholeLines(text, selectionStart, selectionLength);
        var content = text.Substring(start, length);
        var indented = Indent(content, "  ", newline);
        var block = ComposeBlock(snippet, indented, newline);

        var bodyStart = start + snippet.OpenBlock.Replace("\n", newline, StringComparison.Ordinal).Length + newline.Length;
        return string.IsNullOrEmpty(snippet.OpenBlock)
            ? new EditResult(start, length, block, start + block.Length, 0)
            : new EditResult(start, length, block, bodyStart, indented.Length);
    }

    /// <summary>
    /// 无选区时：在光标所在处插入片段骨架。
    /// 当前行为空白行则替换该行，否则另起一行插入，保证围栏总是从行首开始。
    /// </summary>
    public static EditResult InsertSnippet(string text, int caret, PrtSnippet snippet)
    {
        var newline = NewlineOf(text);

        if (snippet.Shape == SnippetShape.InlineWrap)
        {
            return WrapInline(text, caret, 0, snippet);
        }

        if (snippet.Shape == SnippetShape.LinePrefix)
        {
            return ApplyLinePrefix(text, caret, 0, snippet.OpenBlock);
        }

        var (lineStart, lineEnd) = LineBounds(text, caret);
        var blankLine = text.AsSpan(lineStart, lineEnd - lineStart).Trim().Length == 0;

        // 行尾终止符之后的第一个下标（行尾换行符可能为 \n 或 \r\n）。
        var afterLine = lineEnd;
        while (afterLine < text.Length && text[afterLine] is '\r' or '\n')
        {
            afterLine++;
        }

        var hasNewline = afterLine > lineEnd;

        int replaceStart;
        int replaceLength;
        string prefix;
        string suffix;

        if (blankLine)
        {
            replaceStart = lineStart;
            replaceLength = lineEnd - lineStart;
            prefix = string.Empty;
            suffix = string.Empty;
        }
        else if (hasNewline)
        {
            // 吃掉原行尾的换行符，另起一段后补回一个换行，保证围栏自成一整段。
            replaceStart = afterLine;
            replaceLength = 0;
            prefix = string.Empty;
            suffix = newline;
        }
        else
        {
            replaceStart = lineEnd;
            replaceLength = 0;
            prefix = newline;
            suffix = string.Empty;
        }

        var body = snippet.Body.Replace("\n", newline, StringComparison.Ordinal);
        var block = ComposeBlock(snippet, body, newline);
        var inserted = prefix + block + suffix;
        var blockStart = replaceStart + prefix.Length;

        return new EditResult(replaceStart, replaceLength, inserted, SelectionStart(snippet, block, blockStart, newline), SelectionLength(snippet, block));
    }

    /// <summary>给选区每一行加行前缀；无选区时在光标所在行的行首插入前缀。</summary>
    public static EditResult ApplyLinePrefix(string text, int selectionStart, int selectionLength, string prefix)
    {
        var newline = NewlineOf(text);

        if (selectionLength <= 0)
        {
            var (lineStart, _) = LineBounds(text, selectionStart);
            return new EditResult(lineStart, 0, prefix, lineStart + prefix.Length, 0);
        }

        var (start, length) = ExpandToWholeLines(text, selectionStart, selectionLength);
        var content = text.Substring(start, length);
        var lines = content.Split('\n');
        var builder = new StringBuilder(content.Length + lines.Length * prefix.Length);
        var index = 0;

        foreach (var raw in lines)
        {
            if (index > 0)
            {
                builder.Append(newline);
            }

            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.Length > 0)
            {
                builder.Append(prefix);
            }

            builder.Append(line);
            index++;
        }

        var replaced = builder.ToString();
        return new EditResult(start, length, replaced, start + replaced.Length, 0);
    }

    // ─────────────────────────────── 内部实现 ───────────────────────────────

    /// <summary>
    /// 组装块文本：有围栏时拼成「开始行 / 内容 / 结束行」，纯内容块（开始与结束都为空）只输出内容。
    /// </summary>
    private static string ComposeBlock(PrtSnippet snippet, string body, string newline)
    {
        var open = snippet.OpenBlock.Replace("\n", newline, StringComparison.Ordinal);
        var close = snippet.CloseBlock.Replace("\n", newline, StringComparison.Ordinal);

        if (open.Length == 0 && close.Length == 0)
        {
            return body;
        }

        return open + newline + body + newline + close;
    }

    private static int SelectionStart(PrtSnippet snippet, string block, int blockStart, string newline)
    {
        if (snippet.SelectTarget is not { Length: > 0 } target)
        {
            return blockStart + HeadLength(snippet, newline) + snippet.Body.Replace("\n", newline, StringComparison.Ordinal).Length;
        }

        var index = block.IndexOf(target, StringComparison.Ordinal);
        return index < 0 ? blockStart : blockStart + index;
    }

    private static int SelectionLength(PrtSnippet snippet, string block)
    {
        if (snippet.SelectTarget is not { Length: > 0 } target)
        {
            return 0;
        }

        return block.Contains(target, StringComparison.Ordinal) ? target.Length : 0;
    }

    /// <summary>块内「开始行 + 换行」的长度（纯内容块为 0）。</summary>
    private static int HeadLength(PrtSnippet snippet, string newline)
        => snippet.OpenBlock.Length == 0 && snippet.CloseBlock.Length == 0
            ? 0
            : snippet.OpenBlock.Replace("\n", newline, StringComparison.Ordinal).Length + newline.Length;

    /// <summary>给多行内容逐行加缩进（空行不加，避免留下尾随空白）。</summary>
    private static string Indent(string content, string indent, string newline)
    {
        var lines = content.Split('\n');
        var builder = new StringBuilder(content.Length + lines.Length * indent.Length);
        var index = 0;

        foreach (var raw in lines)
        {
            if (index > 0)
            {
                builder.Append(newline);
            }

            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.Length > 0)
            {
                builder.Append(indent);
            }

            builder.Append(line);
            index++;
        }

        return builder.ToString();
    }
}
