namespace Prt.App.Services;

/// <summary>结业测试的一道题（单选）。选项用语言中立的短串，题目与解析分中英两套。</summary>
/// <param name="PromptZh">中文题干。</param>
/// <param name="PromptEn">英文题干。</param>
/// <param name="Options">选项（顺序即界面顺序）。</param>
/// <param name="CorrectIndex">正确选项下标。</param>
/// <param name="WhyZh">中文解析。</param>
/// <param name="WhyEn">英文解析。</param>
internal sealed record QuizQuestion(
    string PromptZh,
    string PromptEn,
    IReadOnlyList<string> Options,
    int CorrectIndex,
    string WhyZh,
    string WhyEn)
{
    /// <summary>按当前语言取题干。</summary>
    public string Prompt => Localizer.IsChinese ? PromptZh : PromptEn;

    /// <summary>按当前语言取解析。</summary>
    public string Why => Localizer.IsChinese ? WhyZh : WhyEn;
}

/// <summary>单题批改结果。</summary>
internal sealed record QuizItemResult(QuizQuestion Question, int AnswerIndex, bool Correct)
{
    /// <summary>是否未作答（-1）。</summary>
    public bool Unanswered => AnswerIndex < 0;
}

/// <summary>一次结业测试的成绩。</summary>
internal sealed record QuizResult(IReadOnlyList<QuizItemResult> Items)
{
    /// <summary>答对题数。</summary>
    public int Correct => Items.Count(i => i.Correct);

    /// <summary>题目总数。</summary>
    public int Total => Items.Count;

    /// <summary>得分（百分制，四舍五入）。</summary>
    public int Score => Total == 0 ? 0 : (int)Math.Round(Correct * 100d / Total, MidpointRounding.AwayFromZero);

    /// <summary>是否通过。</summary>
    public bool Passed => Correct >= TutorialQuiz.PassCount;
}

/// <summary>
/// 结业测试题库与批改。
/// <para>
/// 题目覆盖各课的「必须记住的那一条」，全部为单选；批改是纯算术，可被自检直接验证
/// （满分、零分、通过线、未作答都当作错）。
/// </para>
/// </summary>
internal static class TutorialQuiz
{
    /// <summary>通过线：答对题目数的下限（总题数的 80%，向上取整）。</summary>
    public static int PassCount => (int)Math.Ceiling(All.Count * 0.8);

    /// <summary>题库。</summary>
    public static IReadOnlyList<QuizQuestion> All { get; } =
    [
        new(
            "一份 PRT 文档的第一行必须是哪一行？",
            "Which line must come first in a PRT document?",
            ["# 标题", "<DocType=PRT>", "::: meta", "不用特别写"],
            1,
            "标准要求第一行写 <DocType=PRT>（半角、区分大小写、单独占一行），缺了它会被判 E_DOCHEAD。",
            "The very first line must be <DocType=PRT> (half-width, case-sensitive, on its own line); missing it is reported as E_DOCHEAD."),

        new(
            "保存文档时应该选哪种编码？",
            "Which encoding should you save with?",
            ["GBK", "UTF-16", "UTF-8 带 BOM", "ANSI"],
            2,
            "UTF-8 附 BOM 是标准规定；缺 BOM 会收到 W_ENCODING 警告，不是 UTF-8 则是致命错误。",
            "UTF-8 with BOM is what the standard requires; a missing BOM raises W_ENCODING, a non-UTF-8 file is fatal."),

        new(
            "二级标题怎么写？",
            "How do you write a level-2 heading?",
            ["**标题**", ":: 标题", "# 标题", "## 标题"],
            3,
            "标题级别由 # 的个数决定：一个 # 一级，两个 ## 二级，最多六级。",
            "The number of # sets the level: one for level 1, two for level 2, up to six."),

        new(
            "把一段文字加粗，用哪一对标记？",
            "Which pair of marks makes text bold?",
            ["==文字==", "*文字*", "~~文字~~", "**文字**"],
            3,
            "**加粗**、*斜体*、~~删除线~~、==高亮==；标记要紧贴文字并成对出现。",
            "**bold**, *italic*, ~~strikethrough~~, ==highlight==; the marks hug the text and must be paired."),

        new(
            "一个「未完成」的任务清单项怎么写？",
            "How do you write an open task-list item?",
            ["- 事项", "[ ] 事项", "- [ ] 事项", "- ( ) 事项"],
            2,
            "任务项是「- [ ]」（未完成）或「- [x]」（已完成）；方括号里的空格不能少。",
            "A task item is \"- [ ]\" (open) or \"- [x]\" (done); the space inside the brackets matters."),

        new(
            "引用行以什么开头？",
            "A quote line starts with which character?",
            ["| ", "::: ", "> ", "- "],
            2,
            "引用以「> 」开头（> 后有一个空格）；「|」是表格，「:::」是盒子。",
            "A quote starts with \"> \". \"|\" is a table and \":::\" is a box."),

        new(
            "语义框（比如 ::: warning）应该怎么收尾？",
            "How do you close a semantic box such as ::: warning?",
            ["::: warning", ":::", "::: close warning", "::: end warning"],
            3,
            "盒子成对出现：::: warning 开始、::: end warning 结束；名字写错会报「未知的块类型」。",
            "Boxes come in pairs: ::: warning opens and ::: end warning closes; a misspelled name reports \"unknown block type\"."),

        new(
            "表格对齐行里，「右对齐」怎么写？",
            "In a table's alignment row, how do you write \"right aligned\"?",
            [":--", ":-:", ":--:", "--:"],
            3,
            ":-- 左对齐、--: 右对齐、:-: 居中（两端的冒号表示对齐方向）。",
            ":-- left, --: right, :-: centre — the colons mark the alignment direction."),

        new(
            "围栏代码块用什么符号围起来？",
            "Which marks fence a code block?",
            ["四个空格的缩进", "三个反引号 ```", "三个单引号 '''", "三个波浪线 ~~~"],
            1,
            "上下各一行三个反引号，上面那行可以带语言名（如 ```text）。",
            "Three backticks on their own line above and below; the opening line may carry a language name such as ```text."),

        new(
            "已经用 id=\"t1\" 命名的表格，别处要引用它该写什么？",
            "A table was named with id=\"t1\" — how do you reference it elsewhere?",
            ["[t1]", "{t1}", "[[ref:t1]]", "@t1"],
            2,
            "先给对象起名（id=\"t1\"），再用 [[ref:t1]] 指过来；只写引用而不写定义会报找不到目标。",
            "Name it first (id=\"t1\"), then point at it with [[ref:t1]]; a reference with no definition reports a missing target."),

        new(
            "诊断 `mydoc.prt:12:5: 错误 PRT0002: 未知的块类型` 里的 12 是什么意思？",
            "In the diagnostic `mydoc.prt:12:5: error PRT0002: unknown block type`, what does 12 mean?",
            ["列号", "错误代码", "行号", "文档编号"],
            2,
            "诊断格式是「文件:行:列: 严重度 代码: 说明」——先看行号定位，再看代码决定要不要改。",
            "The format is \"file:line:column: severity code: message\" — locate the line first, then read the code."),

        new(
            "想打开这个内置教程，按哪个键？",
            "Which key opens this built-in tutorial?",
            ["Ctrl+T", "F1", "Ctrl+H", "F2"],
            1,
            "F1 打开教程；Ctrl+H 是替换、Ctrl+P 是打印、Ctrl+, 是设置。",
            "F1 opens the tutorial; Ctrl+H is replace, Ctrl+P is print, Ctrl+, is settings."),
    ];

    /// <summary>批改一份答卷（<paramref name="answers"/> 与题库等长；-1 表示未作答）。</summary>
    public static QuizResult Grade(IReadOnlyList<int> answers)
    {
        var items = new List<QuizItemResult>(All.Count);
        for (var i = 0; i < All.Count; i++)
        {
            var answer = i < answers.Count ? answers[i] : -1;
            items.Add(new QuizItemResult(All[i], answer, answer == All[i].CorrectIndex));
        }

        return new QuizResult(items);
    }
}
