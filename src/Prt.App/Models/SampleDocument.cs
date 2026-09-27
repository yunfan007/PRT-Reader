using System.IO;
using Perisc.Safety;

namespace Prt.App.Models;

/// <summary>
/// 示例内容提供者：新建文档时的起始内容，以及「打开示例文档」的定位逻辑。
/// </summary>
internal static class SampleDocument
{
    /// <summary>示例文档相对于程序目录的位置。</summary>
    private const string SampleRelativePath = "samples";

    /// <summary>随程序分发的示例文件名。</summary>
    private const string SampleFileName = "sample.prt";

    /// <summary>
    /// 新建文档的起始内容。刻意保持简短，仅用于说明用法与提示可用的能力边界。
    /// </summary>
    public const string Welcome = """
# 未命名文档

这是一份 PRT 文档。左边写、右边看，两边会自动对照。

- **照着看**：光标移到哪儿，右边就跳到哪儿并高亮；点右边的任意位置，左边也会跳过去
- **换视图**：工具栏或「视图」菜单里可切换「源码 / 分栏 / 视觉化」三种看法
- **随手写的**：`**加粗**`、`*斜体*`、`~~删除线~~`、`==高亮==`、`行内代码`、[[kbd:Ctrl+S]]
- **成块的**：`::: note`（提示）、`::: warning`（警告）、`::: tip`（技巧）等
- **互相关联**：给表格加上 `::: table id="t1"`，别处就能用 `[[ref:t1]]` 指过来
- **自动编号**：`#` 标题和 `::: section` 会自动编号，`[[contents]]` 生成目录
- **右键试试**：右栏空白处点右键，可插入各种块、套用行内格式，也能直接改文字

::: tip
常用快捷键：Ctrl+F 查找、Ctrl+H 替换、Ctrl+S 保存、F1 打开教程。
菜单「设置」里可以调整配色、字体与编辑习惯。
::: end tip
""";

    /// <summary>
    /// 定位随程序分发的示例文档，唯一候选为 <c>&lt;程序目录&gt;\samples\sample.prt</c>。
    /// <para>
    /// 「随附」的语义就是「与可执行文件同级」，故只在程序目录下查这一处：
    /// 不向上回溯上级目录（那会把安装位置之外的无关同名文件当作随附资源读进来），
    /// 也不保留旧文件名作为回退（多一个候选就多一个读到不该读的文件的口子）。
    /// </para>
    /// <para>
    /// 开发期同样成立——csproj 已把该文件复制到输出目录的 <c>samples\</c>，
    /// 无需再从源码树回溯查找。
    /// </para>
    /// </summary>
    public static string? LocateFeatureTour()
    {
        var path = Path.Combine(AppContext.BaseDirectory, SampleRelativePath, SampleFileName);
        return Exists(path) ? path : null;
    }

    /// <summary>
    /// 探测候选路径是否存在（经 SRT 申报）。
    /// <para>
    /// 未获许可时返回 false——本方法只用于「判断随附文档在不在位」，
    /// 把拒绝当成"这个位置没有"是安全且不误导的降级：界面会说找不到文档，而不会谎称已找到。
    /// </para>
    /// </summary>
    private static bool Exists(string path)
        => SafeRuntime.File.Exists(path, "定位程序随附文档").Value == true;

    /// <summary>内置教程文件名。</summary>
    private const string TutorialFileName = "PRT-教程.md";

    /// <summary>内置作者指南文件名。</summary>
    private const string AuthorGuideFileName = "PRT-作者指南.md";

    /// <summary>内置教程/指南在程序目录下的子目录。</summary>
    private const string HelpRelativePath = "帮助";

    /// <summary>定位随程序分发的《PRT 教程》。</summary>
    public static string? LocateTutorial() => LocateBundled(TutorialFileName);

    /// <summary>定位随程序分发的《PRT 作者指南》。</summary>
    public static string? LocateAuthorGuide() => LocateBundled(AuthorGuideFileName);

    /// <summary>
    /// 定位随程序分发的教程/指南，唯一候选为 <c>&lt;程序目录&gt;\帮助\&lt;文件名&gt;</c>。
    /// <para>
    /// 与示例文档同一口径：只看程序目录，不再回溯源码树的 <c>Standard\PRT</c> 目录。
    /// 开发期依赖 csproj 的 <c>CopyToOutputDirectory</c> 把两份文档复制到输出目录的 <c>帮助\</c>。
    /// </para>
    /// <para>
    /// 这里也不追加「入口程序集所在目录」兜底：单文件发布（PublishSingleFile）下
    /// <c>Assembly.Location</c> 恒为空字符串，编译器按 IL3000 告警；而该目录与
    /// <c>AppContext.BaseDirectory</c> 本就是同一个目录，追加不产生新信息。
    /// </para>
    /// </summary>
    private static string? LocateBundled(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, HelpRelativePath, fileName);
        return Exists(path) ? path : null;
    }
}
