namespace Prt.App.Services;

/// <summary>
/// 一课教程。正文用极短的条目式说明；<see cref="Snippet"/> 是可直接插入编辑器的示例，
/// 同时也是该课「效果预览」的渲染来源。
/// </summary>
/// <param name="TitleZh">中文标题。</param>
/// <param name="TitleEn">英文标题。</param>
/// <param name="BodyZh">中文正文（多行文本，逐行渲染）。</param>
/// <param name="BodyEn">英文正文。</param>
/// <param name="Snippet">本课示例（PRT 写法；同时用于预览演示）。</param>
/// <param name="IntentionalDiagnostic">
/// 本课示例**故意**含错误（用来教怎么看诊断）。自检对这类课程反过来要求「必须产生诊断」，
/// 其余课程则要求「零错误」。
/// </param>
internal sealed record TutorialLesson(
    string TitleZh,
    string TitleEn,
    string BodyZh,
    string BodyEn,
    string Snippet,
    bool IntentionalDiagnostic = false)
{
    /// <summary>按当前语言取标题。</summary>
    public string Title => Localizer.IsChinese ? TitleZh : TitleEn;

    /// <summary>按当前语言取正文。</summary>
    public string Body => Localizer.IsChinese ? BodyZh : BodyEn;
}

/// <summary>
/// 内置互动教程的课程内容。
/// <para>
/// **刻意不含 PRTA（受限计算 / 白名单函数 / 运行块）内容**：本编辑器声明只实现 CORE + EXT，
/// 不支持 COMP，教程里讲这些只会让人对着写不出来的东西发懵。全部课程都对应编辑器**实际支持**
/// 的写法，且每课示例都会被自检逐条解析一遍（见「教程各课示例均可解析」）。
/// </para>
/// </summary>
internal static class TutorialLessons
{
    /// <summary>全部课程，按学习顺序排列。</summary>
    public static IReadOnlyList<TutorialLesson> All { get; } =
    [
        new(
            "第 1 课｜写下第一份文档",
            "Lesson 1 · Your first document",
            """
            一份文档就是一段纯文本，用「写法记号」表达结构，不需要鼠标点样式按钮。
            两个约定先记住：
            · 第一行固定是 <DocType=PRT>，告诉程序「这是一份 PRT 文档」。
            · 保存时选择「UTF-8 带 BOM」，中文在任何机器上都不会乱码。
            下面这段就是最小的一份文档——点「插入到编辑器」，它就会出现在你的文档里。
            """,
            """
            A document is just plain text; structure comes from a few writeable marks, not from style buttons.
            Two conventions to remember first:
            · The very first line is always <DocType=PRT>, telling the app "this is a PRT document".
            · Save as UTF-8 with BOM so non-ASCII text never turns into garbage.
            The snippet below is a minimal document — press "Insert into the editor" to try it.
            """,
            """
            # 我的第一份文档

            这段是正文。左边写，右边立刻能看到效果。

            - 一行以 `#` 开头就是标题
            - 空行分段
            """),

        new(
            "第 2 课｜标题与自动编号",
            "Lesson 2 · Headings and automatic numbering",
            """
            一个 `#` 是一级标题，两个 `##` 是二级，最多六级。
            标题会自动编号，不需要自己写「1.1」；改结构后编号自动重排。
            章节还可以用 `::: section` 包起来，同样参与编号。
            """,
            """
            One `#` is a level-1 heading, `##` level 2, up to six levels.
            Headings are numbered automatically — never type "1.1" yourself; reordering renumbers for you.
            A section can also be wrapped with `::: section`, which joins the same numbering.
            """,
            """
            # 一级标题

            ## 二级标题

            正文段落。

            ## 另一个二级标题

            ::: section 自定义章节
            章节里的内容。
            ::: end section
            """),

        new(
            "第 3 课｜行内格式",
            "Lesson 3 · Inline marks",
            """
            最常用的几种，都是「前后成对加记号」：
            **加粗**、*斜体*、~~删除线~~、==高亮==、`行内代码`。
            按键提示写作 [[kbd:Ctrl+S]]，会显示成一个小按键样式。
            记号必须成对出现；落单的记号会原样显示出来。
            """,
            """
            The common ones pair a mark before and after the text:
            **bold**, *italic*, ~~strikethrough~~, ==highlight==, `inline code`.
            A key hint is written [[kbd:Ctrl+S]] and shows up as a little key cap.
            Marks must come in pairs; a stray mark is shown as-is.
            """,
            """
            这一段里 **加粗**、*斜体*、~~删除线~~、==高亮==、`行内代码` 都齐了。

            保存请按 [[kbd:Ctrl+S]]，查找请按 [[kbd:Ctrl+F]]。
            """),

        new(
            "第 4 课｜列表与任务清单",
            "Lesson 4 · Lists and task items",
            """
            行首写 `-` 是无序列表，写 `1.` 是有序列表；缩进两格表示下一级。
            任务清单写成 `- [ ]` 与 `- [x]`，在预览里可以直接点勾选，
            点一下会**改回源文本**，不会只改预览。
            """,
            """
            Start a line with `-` for a bullet list, `1.` for a numbered list; indent to nest.
            Task items are `- [ ]` and `- [x]`. In the preview you can click the box directly —
            the click writes back to the **source text**, not just the preview.
            """,
            """
            - 第一项
            - 第二项
              - 缩进两格是下一级

            1. 先做这个
            2. 再做那个

            - [x] 已经做完的事
            - [ ] 还没做的事
            """),

        new(
            "第 5 课｜引用、分隔与脚注",
            "Lesson 5 · Quotes, rules and footnotes",
            """
            行首 `>` 是引用块，适合放别人的话或需要强调的一段。
            单独一行三个减号 `---` 画一条分隔线。
            脚注写成 `[^1]`，说明写在 `[^1]: 说明` 里。
            """,
            """
            A line starting with `>` is a quote — handy for someone else's words.
            Three dashes `---` on their own line draw a horizontal rule.
            Footnotes are written `[^1]`, with the text in `[^1]: ...`.
            """,
            """
            正文一句。

            > 引用：把别人的话放进来，视觉上会和正文分开。

            ---

            这里有个脚注[^1]。

            [^1]: 脚注的说明写在这里。
            """),

        new(
            "第 6 课｜语义框（13 类）",
            "Lesson 6 · Callouts (13 kinds)",
            """
            用 `::: note` 包起来就是一块带底色的提示框，结尾写 `::: end note`。
            常用种类：note（提示）、tip（技巧）、warning（警告）、danger（危险）、
            important（重要）、example（示例）、quote（引文）等，共 13 类。
            中间可以嵌套普通写法，语义框里也能放列表和代码。
            """,
            """
            Wrapping text in `::: note` … `::: end note` makes a tinted callout box.
            Common kinds: note, tip, warning, danger, important, example, quote — 13 in total.
            You can nest normal marks inside; lists and code work too.
            """,
            """
            ::: note
            这是一个提示框。适合放「顺带一提」的信息。
            ::: end note

            ::: warning
            这是一个警告框。适合放「容易踩坑」的地方。
            ::: end warning

            ::: tip
            技巧框里也可以写 **加粗** 和列表：
            - 第一点
            - 第二点
            ::: end tip
            """),

        new(
            "第 7 课｜表格",
            "Lesson 7 · Tables",
            """
            竖线 `|` 分列，第二行的 `:--` 决定对齐：
            `:--` 左对齐、`:-:` 居中、`--:` 右对齐。
            表头与正文之间必须有那一行分隔。
            跨行跨列用 `++` / `^^` 记号，不建议链式合并。
            """,
            """
            Columns are separated by `|`; the second row of `:--` decides alignment:
            `:--` left, `:-:` centre, `--:` right.
            That separator row is required between the header and the body.
            Spans use `++` / `^^` marks; chained merges are not recommended.
            """,
            """
            | 项目 | 数量 | 备注 |
            | :-- | --: | :-: |
            | 铅笔 | 3 | 左对齐 / 右对齐 / 居中 |
            | 橡皮 | 1 | 数字列右对齐更整齐 |
            """),

        new(
            "第 8 课｜代码块",
            "Lesson 8 · Code blocks",
            """
            三个反引号开一段代码，末尾再用三个反引号收尾；
            开头的反引号后面写语言名（如 ```csharp），代码会按该语言高亮。
            行内短代码用一对反引号。
            代码块里的内容是**原样显示**的，里面的 `*`、`_` 不会被当成格式记号。
            """,
            """
            Three backticks open a code block and three close it;
            put a language name after the opening fence (e.g. ```csharp) to highlight it.
            For a short piece inline, use a single pair of backticks.
            Inside a code block everything is **shown verbatim** — `*` and `_` are not format marks there.
            """,
            """
            行内写法：`Console.WriteLine("hi");`

            ```csharp
            public static int Add(int a, int b)
            {
                return a + b;   // 这里的 * 和 _ 不会被当成格式记号
            }
            ```
            """),

        new(
            "第 9 课｜图片、链接与路径",
            "Lesson 9 · Images, links and paths",
            """
            图片写成 `![说明](图片路径)`，链接写成 `[文字](网址)`。
            相对路径以**文档所在目录**为基准——把图片和文档放同一个文件夹最省心。
            图片不存在时不会报错，只显示成占位文字。
            """,
            """
            An image is `![alt](path)`, a link is `[text](url)`.
            Relative paths are resolved against the **document's own folder** — keeping images
            next to the document is the least surprising choice.
            A missing image is not an error; it shows as placeholder text.
            """,
            """
            ![一朵花](flower.png)

            参考 [PRT 项目主页](https://example.com/prt)。

            图片找不到时只会显示占位文字，不影响文档打开。
            """),

        new(
            "第 10 课｜交叉引用与目录",
            "Lesson 10 · Cross references and contents",
            """
            给表格或章节起个 id，别处就能指过来：
            `::: table id="t1"` 定义之后，正文里用 `[[ref:t1]]` 引用它，点一下会跳过去。
            `[[contents]]` 会按当前结构生成一份目录，编号自动跟着变。
            引用不存在的 id 不算错误，只提示一句。
            """,
            """
            Give a table or section an id and you can point at it from anywhere:
            after `::: table id="t1"`, write `[[ref:t1]]` in the text — clicking jumps there.
            `[[contents]]` builds a table of contents from the current structure, renumbered automatically.
            Referencing a missing id is not an error, just a hint.
            """,
            """
            [[contents]]

            ::: table id="t1" caption="季度数据"
            | 季度 | 收入 |
            | :-- | --: |
            | 一季度 | 120 |
            | 二季度 | 150 |
            ::: end table

            见 [[ref:t1]]，那里列出了全部季度。
            """),

        new(
            "第 11 课｜整篇的外观：文档信息与主题",
            "Lesson 11 · Document info and themes",
            """
            `::: meta` 写文档自己的信息：标题、副标题、作者、日期、语言、用哪套主题。
            这些信息会出现在预览顶部，也会写进导出的 HTML。
            主题有两种用法：整篇用现成的一套（default / dark / print / accessible），
            或者在文档里用 `::: theme` 自己改几个颜色——用命名色，不要写十六进制色值。
            小注：标准 v2.1 新增的 `::: style`（整篇统一字体 / 磅数 / 底纹 / 水印）编辑器还在跟进，
            现在照抄它会报「未知块类型」；先用 `::: text` 逐块设置对齐与字号。
            """,
            """
            `::: meta` holds the document's own information: title, subtitle, author, date, language, theme.
            It shows at the top of the preview and travels into exported HTML.
            Themes work two ways: pick a built-in set for the whole document (default / dark / print / accessible),
            or adjust a few colors with `::: theme` — use the named colors, never hex values.
            A note: the v2.1 `::: style` block (document-wide font, size, shading, watermark) is still being
            implemented by the editor; copying it today reports "unknown block type". Use `::: text` per block for now.
            """,
            """
            ::: meta
            title: 我的文档
            subtitle: 记录一点东西
            author: 我
            date: 2026-09-18
            lang: zh-CN
            ::: end meta

            ::: theme
            --page-bg: white
            --heading: navy
            ::: end theme

            # 正文从这里开始

            主题里的命名色会覆盖默认配色。
            """),

        new(
            "第 12 课｜看懂报错",
            "Lesson 12 · Reading diagnostics",
            """
            报错长这样：`文件:行:列: 严重度 代码: 说明`。
            例：`mydoc.prt:12:5: 错误 PRT0002: 未知的块类型`。
            先看行号定位那一行，再看代码：错误必须改，警告可以留意，提示只是提醒。
            在诊断面板里双击一条，编辑器会直接跳到对应位置。
            """,
            """
            Diagnostics look like `file:line:column: severity code: message`.
            Example: `mydoc.prt:12:5: error PRT0002: unknown block type`.
            Jump to the line first, then read the code: errors must be fixed, warnings are worth a look,
            hints are just notes. Double-click an entry in the diagnostics panel to jump there.
            """,
            """
            ::: warnng
            这一行故意把 warning 拼错了，右侧诊断栏会报「未知的块类型」。
            ::: end warnng

            改回 ::: warning 就好了。
            """,
            IntentionalDiagnostic: true),


        new(
            "第 13 课｜三种视图与常用快捷键",
            "Lesson 13 · Three views and handy shortcuts",
            """
            工具栏左侧可以切换三种看法：
            源码（只写）、分栏（左写右看）、视觉化（只看效果）。
            光标移到哪儿，另一边就跳到哪儿；在预览里点一下，编辑器也会跟过去。
            常用键：Ctrl+S 保存、Ctrl+F 查找、Ctrl+H 替换、Ctrl+P 打印、Ctrl+, 设置、F1 回到本教程。
            右键还有「这一行」菜单，可以一键把当前行变成标题、列表、引用或代码块。
            """,
            """
            The toolbar switches between three views:
            source only, split (write left, watch right), and visual only.
            Move the caret and the other side follows; click in the preview and the editor follows back.
            Handy keys: Ctrl+S save, Ctrl+F find, Ctrl+H replace, Ctrl+P print, Ctrl+, settings, F1 this tutorial.
            The right-click menu also has a "this line" section: turn the current line into a heading,
            list item, quote or code block in one click.
            """,
            """
            # 边写边看

            试着把光标在这几行之间移动，观察右侧高亮跟着走。

            - 这一行可以右键改成标题
            - 也可以改成引用或代码块
            """),
    ];
}
