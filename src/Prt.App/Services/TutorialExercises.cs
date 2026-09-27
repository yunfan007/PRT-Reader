using System.Text.RegularExpressions;
using Prt.Core;
using Prt.Core.Diagnostics;

namespace Prt.App.Services;

/// <summary>练习的一条判定要求：描述 + 对「学习者写的文本」的可执行检查。</summary>
/// <param name="DescriptionZh">中文描述。</param>
/// <param name="DescriptionEn">英文描述。</param>
/// <param name="Test">检查函数；输入为学习者当前编辑器里的全文。</param>
internal sealed record ExerciseRequirement(
    string DescriptionZh,
    string DescriptionEn,
    Func<string, bool> Test)
{
    /// <summary>按当前语言取描述。</summary>
    public string Description => Localizer.IsChinese ? DescriptionZh : DescriptionEn;
}

/// <summary>单条判定的结果（供界面逐条打勾）。</summary>
internal sealed record ExerciseCheckResult(string Description, bool Passed);

/// <summary>一次练习判定的汇总。</summary>
internal sealed record ExerciseResult(IReadOnlyList<ExerciseCheckResult> Checks)
{
    /// <summary>通过条数。</summary>
    public int Passed => Checks.Count(c => c.Passed);

    /// <summary>总条数。</summary>
    public int Total => Checks.Count;

    /// <summary>是否全部通过（练习完成）。</summary>
    public bool AllPassed => Checks.Count > 0 && Checks.All(c => c.Passed);
}

/// <summary>
/// 一课配套的练习：给一段**待补全**的片段，学习者改好后点「校验我的写法」，
/// 由 <see cref="Requirements"/> 逐条判定（全部为纯函数，可被自检直接验证）。
/// </summary>
internal sealed record TutorialExercise(
    string PromptZh,
    string PromptEn,
    string StartSnippet,
    string AnswerSnippet,
    string NoteZh,
    string NoteEn,
    IReadOnlyList<ExerciseRequirement> Requirements)
{
    /// <summary>按当前语言取题目要求。</summary>
    public string Prompt => Localizer.IsChinese ? PromptZh : PromptEn;

    /// <summary>按当前语言取答案解析。</summary>
    public string Note => Localizer.IsChinese ? NoteZh : NoteEn;

    /// <summary>判定一份文本。</summary>
    public ExerciseResult Evaluate(string text)
        => new(Requirements.Select(r => new ExerciseCheckResult(r.Description, Safe(r.Test, text))).ToList());

    private static bool Safe(Func<string, bool> test, string text)
    {
        try
        {
            return test(text);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// 各课练习。与课程一一对应（第 13 课是界面与快捷键，没有文档内容，故无练习）。
/// <para>
/// 判定一律**基于写出来的文本与真实解析结果**，不做「与参考答案逐字比对」——
/// 同一件事有不止一种正确写法，教程不该把学习者的合理写法判错。
/// </para>
/// </summary>
internal static class TutorialExercises
{
    /// <summary>取第 <paramref name="lessonIndex"/> 课（0 起）的练习；没有则返回 null。</summary>
    public static TutorialExercise? For(int lessonIndex)
        => lessonIndex >= 0 && lessonIndex < All.Count ? All[lessonIndex] : null;

    /// <summary>全部练习（按课程顺序；无练习的课为 null）。</summary>
    public static IReadOnlyList<TutorialExercise?> All { get; } =
    [
        // ── 第 1 课：写下第一份文档 ──
        new(
            "把下面这段变成一份合法文档：补上第一行，并给它一个标题。",
            "Turn this into a valid document: add the very first line, and give it a heading.",
            """
            我的练习文档

            这是正文。
            """,
            """
            <DocType=PRT>
            # 我的练习文档

            这是正文。
            """,
            "第一行固定写 <DocType=PRT>（半角、区分大小写、单独占一行）；标题以「# 」开头，注意 # 后有一个空格。",
            "The first line is always <DocType=PRT> (half-width, case-sensitive, on its own line). A heading starts with \"# \" — including the space.",
            [
                Req("第一行是 <DocType=PRT>", "The first line is <DocType=PRT>", FirstLineIsDocType),
                Req("至少有一个一级标题（以「# 」开头）", "At least one level-1 heading (starts with \"# \")", t => HasLineStartingWith(t, "# ")),
                Req("能正常解析（无错误）", "Parses with no errors", ParsesCleanly),
            ]),

        // ── 第 2 课：标题与自动编号 ──
        new(
            "给这份菜谱补两个二级标题：「材料」与「做法」。",
            "Add two level-2 headings to this recipe: \"材料\" and \"做法\".",
            """
            <DocType=PRT>
            # 我的菜谱

            鸡蛋、面粉、牛奶
            打散、下锅、翻面
            """,
            """
            <DocType=PRT>
            # 我的菜谱

            ## 材料

            鸡蛋、面粉、牛奶

            ## 做法

            打散、下锅、翻面
            """,
            "标题级别靠 # 的个数：一个 # 是一级，两个 ## 是二级。注意 # 与文字之间要有空格。",
            "Heading level comes from the number of #: one for level 1, two for level 2. Keep a space between # and the text.",
            [
                Req("有二级标题「## 材料」", "Has the level-2 heading \"## 材料\"", t => HasLine(t, "## 材料")),
                Req("有二级标题「## 做法」", "Has the level-2 heading \"## 做法\"", t => HasLine(t, "## 做法")),
                Req("能正常解析（无错误）", "Parses with no errors", ParsesCleanly),
            ]),

        // ── 第 3 课：行内格式 ──
        new(
            "把「务必记住」四个字加粗、把「空行」两个字高亮。",
            "Make \"务必记住\" bold and highlight \"空行\".",
            """
            <DocType=PRT>
            # 行内格式练习

            这次要记住的重点是：务必记住空行分隔段落。
            """,
            """
            <DocType=PRT>
            # 行内格式练习

            这次要记住的重点是：**务必记住** ==空行== 分隔段落。
            """,
            "**文字** 是加粗，==文字== 是高亮，*文字* 是斜体；标记要紧贴文字、成对出现。",
            "**text** is bold, ==text== is highlight, *text* is italic; marks hug the text and must come in pairs.",
            [
                Req("用了加粗标记 **文字**", "Uses bold: **text**", t => Regex.IsMatch(t, @"\*\*[^*\r\n]+\*\*")),
                Req("用了高亮标记 ==文字==", "Uses highlight: ==text==", t => Regex.IsMatch(t, @"==[^=\r\n]+==")),
                Req("能正常解析（无错误）", "Parses with no errors", ParsesCleanly),
            ]),

        // ── 第 4 课：列表与任务清单 ──
        new(
            "把这两样东西改成任务清单（带方框的待办），并在前面加一行说明。",
            "Turn these two items into a task list (checkboxes) and add one line of text above.",
            """
            <DocType=PRT>
            # 购物清单

            苹果
            牛奶
            """,
            """
            <DocType=PRT>
            # 购物清单

            今天要买：

            - [ ] 苹果
            - [ ] 牛奶
            """,
            "无序列表用「- 」开头；任务项是「- [ ] 」（未完成）或「- [x]」（已完成）——方括号里那个空格不能少。",
            "An unordered item starts with \"- \". A task item is \"- [ ]\" (open) or \"- [x]\" (done) — the space inside the brackets matters.",
            [
                Req("两项都是任务项（- [ ] 或 - [x]）", "Both items are task items (- [ ] or - [x])",
                    t => Regex.Matches(t, @"(?m)^\s*-\s*\[[ xX]\]\s*\S").Count >= 2),
                Req("列表项不少于两行（每行以「- 」开头）", "At least two list lines (each starting with \"- \")",
                    t => Regex.Matches(t, @"(?m)^\s*-\s+\S").Count >= 2),
                Req("能正常解析（无错误）", "Parses with no errors", ParsesCleanly),
            ]),

        // ── 第 5 课：引用、分隔与脚注 ──
        new(
            "把这句话变成引用（引用行），并在它下面加一条分隔线。",
            "Turn that sentence into a quote, and add a horizontal rule below it.",
            """
            <DocType=PRT>
            # 名言

            写作是把想清楚的东西写下来。
            """,
            """
            <DocType=PRT>
            # 名言

            > 写作是把想清楚的东西写下来。

            ---
            """,
            "引用行以「> 」开头（> 后有一个空格）；分隔线是单独一行的三个减号 ---。",
            "A quote line starts with \"> \". A horizontal rule is a line of three dashes: ---.",
            [
                Req("有引用行（以「> 」开头）", "Has a quote line (starts with \"> \")", t => HasLineStartingWith(t, "> ")),
                Req("有分隔线 ---", "Has a horizontal rule: ---", t => HasLine(t, "---")),
                Req("能正常解析（无错误）", "Parses with no errors", ParsesCleanly),
            ]),

        // ── 第 6 课：语义框 ──
        new(
            "把「注意」这句话放进一个警告框（::: warning），要有开有合。",
            "Put the warning sentence inside a warning box (::: warning) — open it and close it.",
            """
            <DocType=PRT>
            # 安全提示

            不要在没有备份的情况下覆盖文件。
            """,
            """
            <DocType=PRT>
            # 安全提示

            ::: warning
            不要在没有备份的情况下覆盖文件。
            ::: end warning
            """,
            "盒子成对出现：::: warning 开始，::: end warning 结束，正文夹在中间。写错名字会报「未知的块类型」。",
            "Boxes come in pairs: ::: warning opens, ::: end warning closes, content sits between. A misspelled name reports \"unknown block type\".",
            [
                Req("有 ::: warning 开始行", "Has an opening ::: warning", t => HasLineStartingWith(t, "::: warning")),
                Req("有对应的 ::: end warning", "Has the matching ::: end warning", t => HasLineStartingWith(t, "::: end warning")),
                Req("能正常解析（无错误）", "Parses with no errors", ParsesCleanly),
            ]),

        // ── 第 7 课：表格 ──
        new(
            "把下面两行数据改成一个两列表格（含表头与对齐行）。",
            "Turn these two data lines into a two-column table (with a header and an alignment row).",
            """
            <DocType=PRT>
            # 成绩

            语文 95
            数学 88
            """,
            """
            <DocType=PRT>
            # 成绩

            | 科目 | 分数 |
            | :-- | --: |
            | 语文 | 95 |
            | 数学 | 88 |
            """,
            "表格用竖线分隔单元格；表头下面那行负责对齐（:-- 左对齐、--: 右对齐、:-: 居中）。",
            "Cells are separated by pipes. The row under the header sets alignment: :-- left, --: right, :-: centre.",
            [
                Req("至少 4 行以「|」开头", "At least 4 pipe rows", t => Regex.Matches(t, @"(?m)^\s*\|").Count >= 4),
                Req("有对齐行（含 :-- 或 --:）", "Has an alignment row (contains :-- or --:)", t => Regex.IsMatch(t, @"(?m)^\s*\|[\s:|-]*(:--|--:)")),
                Req("能正常解析（无错误）", "Parses with no errors", ParsesCleanly),
            ]),

        // ── 第 8 课：代码块 ──
        new(
            "把那段示例代码放进围栏代码块。",
            "Put that sample code into a fenced code block.",
            """
            <DocType=PRT>
            # 代码示例

            Console.WriteLine("你好");
            """,
            """
            <DocType=PRT>
            # 代码示例

            ```text
            Console.WriteLine("你好");
            ```
            """,
            "围栏是三个反引号：上面一行写 ``` 加语言名（也可以不写），下面再用一行 ``` 收尾。",
            "A fence is three backticks: an opening line with an optional language name, and a closing line of three backticks.",
            [
                Req("有围栏（出现两个 ```）", "Has a fence (two ``` marks)", t => Regex.Matches(t, "```").Count >= 2),
                Req("代码在围栏中间", "The code sits between the fences",
                    t => Regex.IsMatch(t, @"```[^\r\n]*\r?\n(?:(?!```).)*Console\.WriteLine", RegexOptions.Singleline)),
                Req("能正常解析（无错误）", "Parses with no errors", ParsesCleanly),
            ]),

        // ── 第 9 课：图片、链接与路径 ──
        new(
            "把「教程」两个字变成指向 PRT-教程.md 的链接。",
            "Turn the word \"教程\" into a link pointing at PRT-教程.md.",
            """
            <DocType=PRT>
            # 相关材料

            更多写法见 教程。
            """,
            """
            <DocType=PRT>
            # 相关材料

            更多写法见 [教程](PRT-教程.md)。
            """,
            "链接写法：[显示文字](地址)。图片在前面加一个感叹号：![说明](图片路径)。",
            "A link is [text](target). An image is the same with a leading exclamation mark: ![alt](path).",
            [
                Req("有链接 [文字](地址)", "Has a link: [text](target)",
                    t => Regex.IsMatch(t, @"\[[^\]\r\n]+\]\([^)\r\n]+\)")),
                Req("链接地址指向 PRT-教程.md", "The link target is PRT-教程.md",
                    t => Regex.IsMatch(t, @"\]\([^)\r\n]*PRT-教程\.md\)")),
                Req("能正常解析（无错误）", "Parses with no errors", ParsesCleanly),
            ]),

        // ── 第 10 课：交叉引用与目录 ──
        new(
            "给表格起个引用名（id=\"t1\"），然后在下面引用它。",
            "Give the table an id (id=\"t1\") and reference it below.",
            """
            <DocType=PRT>
            # 引用练习

            ::: table caption="成绩表"
            | 科目 | 分数 |
            | :-- | --: |
            | 语文 | 95 |
            ::: end table

            见上表。
            """,
            """
            <DocType=PRT>
            # 引用练习

            ::: table id="t1" caption="成绩表"
            | 科目 | 分数 |
            | :-- | --: |
            | 语文 | 95 |
            ::: end table

            见 [[ref:t1]]。
            """,
            "先给对象起名（id=\"t1\"），再用 [[ref:t1]] 指过来；引用与定义必须成对，只写引用会报「找不到目标」。",
            "Name the target first (id=\"t1\"), then point at it with [[ref:t1]]. A reference without its definition is reported as a missing target.",
            [
                Req("表格带 id=\"t1\"", "The table carries id=\"t1\"", t => t.Contains("id=\"t1\"", StringComparison.Ordinal)),
                Req("用了引用 [[ref:t1]]", "Uses the reference [[ref:t1]]", t => t.Contains("[[ref:t1]]", StringComparison.Ordinal)),
                Req("能正常解析（无错误）", "Parses with no errors", ParsesCleanly),
            ]),

        // ── 第 11 课：文档信息与主题 ──
        new(
            "给文档补一段信息（::: meta），写清标题与作者。",
            "Add a document info block (::: meta) with a title and an author.",
            """
            <DocType=PRT>
            # 正文从这里开始

            主题里的命名色会覆盖默认配色。
            """,
            """
            <DocType=PRT>
            ::: meta
            title: 我的文档
            subtitle: 记录一点东西
            author: 我
            date: 2026-09-18
            lang: zh-CN
            ::: end meta

            # 正文从这里开始

            主题里的命名色会覆盖默认配色。
            """,
            "::: meta 里的每行都是「键: 值」；日期写 yyyy-MM-dd，语言写 zh-CN 这类短标签。",
            "Inside ::: meta every line is \"key: value\". Dates use yyyy-MM-dd; language is a short tag such as zh-CN.",
            [
                Req("有 ::: meta 块（含结束行）", "Has a ::: meta block (with its end line)",
                    t => HasLineStartingWith(t, "::: meta") && HasLineStartingWith(t, "::: end meta")),
                Req("meta 里写了 title:", "The meta block sets title:", t => Regex.IsMatch(t, @"(?m)^\s*title\s*:\s*\S")),
                Req("meta 里写了 author:", "The meta block sets author:", t => Regex.IsMatch(t, @"(?m)^\s*author\s*:\s*\S")),
                Req("能正常解析（无错误）", "Parses with no errors", ParsesCleanly),
            ]),

        // ── 第 12 课：看懂报错 ──
        new(
            "这段示例里盒子的名字拼错了——照诊断把它改对。",
            "The box name is misspelled here — read the diagnostic and fix it.",
            """
            <DocType=PRT>
            # 改错练习

            ::: warnng
            这一行把警告框的名字拼错了，右侧诊断栏会报「未知的块类型」。
            ::: end warnng
            """,
            """
            <DocType=PRT>
            # 改错练习

            ::: warning
            名字改对以后，诊断就消失了。
            ::: end warning
            """,
            "诊断会告诉你在第几行第几列、以及错误代码；先看行号定位，再照着改。",
            "A diagnostic tells you the line and column plus an error code: locate the line first, then fix it.",
            [
                Req("不再出现拼错的 ::: warnng", "The misspelled ::: warnng is gone",
                    t => !HasLineStartingWith(t, "::: warnng")),
                Req("正确写了 ::: warning", "The box is spelled ::: warning", t => HasLineStartingWith(t, "::: warning")),
                Req("能正常解析（无错误）", "Parses with no errors", ParsesCleanly),
            ]),
    ];

    // ─────────────────────────── 判定助手 ───────────────────────────

    private static ExerciseRequirement Req(string zh, string en, Func<string, bool> test)
        => new(zh, en, test);

    /// <summary>解析一遍，要求没有任何「错误」级诊断（警告与提示不算）。</summary>
    private static bool ParsesCleanly(string text)
    {
        var document = PrtParser.Parse(text, new PrtOptions());
        return document.SortedDiagnostics.All(d => d.Severity != DiagnosticSeverity.Error);
    }

    private static bool FirstLineIsDocType(string text)
        => SplitLines(text).FirstOrDefault()?.Trim() == "<DocType=PRT>";

    private static bool HasLine(string text, string content)
        => SplitLines(text).Any(line => line.Trim() == content);

    private static bool HasLineStartingWith(string text, string prefix)
        => SplitLines(text).Any(line => line.TrimStart().StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>切行：去掉 BOM、统一换行、去掉行尾空白。</summary>
    private static IEnumerable<string> SplitLines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
               .TrimStart('\uFEFF')
               .Split('\n')
               .Select(line => line.TrimEnd());
}
