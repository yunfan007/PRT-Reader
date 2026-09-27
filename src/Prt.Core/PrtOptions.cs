namespace Prt.Core;

/// <summary>
/// 解析与渲染选项。
/// </summary>
public sealed class PrtOptions
{
    /// <summary>
    /// 错误处理模式（标准 14.4）。默认为严格模式：语法错误、未知块类型、非法颜色、
    /// 未闭合围栏、引用不存在目标等均为致命错误。
    /// </summary>
    public bool Strict { get; set; } = true;

    /// <summary>
    /// 渲染器配置层面的模式显式覆盖（标准 14.4：`meta` 的 `strict` 键与渲染器配置同时存在时，
    /// 以渲染器配置为准）。为 null 时表示未显式配置，改用 `meta.strict`，再退化为 <see cref="Strict"/>。
    /// </summary>
    public bool? StrictOverride { get; set; }

    /// <summary>
    /// 受限计算开关（标准 11.7）。受限计算默认关闭，只在显式启用后才执行。
    /// <para>
    /// 【COMP 模块不支持】本实现仅声明 CORE + EXT 等级，不具备受限计算能力，
    /// 因此该值恒被忽略：无论设置为 true 或 false，`{{ }}` 插值一律按原文输出，
    /// `:::` set / if / loop / run 一律按第 13 章降级处理，绝不求值。
    /// 保留此属性仅为与本标准接口对齐。
    /// </para>
    /// </summary>
    public bool EnableComputation { get; set; }

    /// <summary>围栏最大嵌套深度，用于抵御恶意深嵌套导致拒绝服务（标准 16.2）。</summary>
    public int MaxFenceDepth { get; set; } = 64;

    /// <summary>表格最大列数，防止超宽表导致资源失控。</summary>
    public int MaxTableColumns { get; set; } = 128;

    /// <summary>宽松模式下的告警是否记录（严格模式恒记录）。</summary>
    public bool RecordWarnings { get; set; } = true;

    /// <summary>渲染目标（影响导出与降级路径，标准第 13 章）。</summary>
    public PrtRenderTarget Target { get; set; } = PrtRenderTarget.Html;

    /// <summary>复制一份选项，供解析内部按有效严格模式派生使用。</summary>
    public PrtOptions Clone() => new()
    {
        Strict = Strict,
        StrictOverride = StrictOverride,
        EnableComputation = EnableComputation,
        MaxFenceDepth = MaxFenceDepth,
        MaxTableColumns = MaxTableColumns,
        RecordWarnings = RecordWarnings,
        Target = Target,
    };
}

/// <summary>输出目标（标准第 13 章）。本实现提供 HTML、纯文本、Markdown 三种导出。</summary>
public enum PrtRenderTarget
{
    Html,
    PlainText,
    Markdown,
}

/// <summary>
/// 本实现的符合性能力声明。
/// <para>
/// 依据《PRT 标准》14.3 节：声明 EXT 等级表示已实现全部 CORE 与 EXT 项；
/// <b>明确不实现 COMP 等级</b>——即不具备变量、条件、循环、受限运行与附录 B 白名单函数能力，
/// 相关结构一律按第 13 章降级处理，不报错、不丢失内容。
/// </para>
/// </summary>
public static class PrtCapabilities
{
    /// <summary>本实现声明的符合等级。</summary>
    public const PrtConformanceLevel Level = PrtConformanceLevel.Ext;

    /// <summary>
    /// 【硬性标注】是否实现 COMP（受限计算）。本实现恒为 false。
    /// 代码中所有涉及 COMP 的分支都必须先判断此常量。
    /// </summary>
    public const bool SupportsComputation = false;

    /// <summary>
    /// 声明所依据的规范版本。
    /// <para>
    /// <b>为什么停在 2.1（别再顺手改成当前标准版本）</b>：仓库内的《PRT 标准》已到 v4.0，
    /// 但 v4.0 采「唯一计算原则」，废除了 <c>::: set</c> / <c>::: if</c> / <c>::: loop</c> /
    /// <c>{{ }}</c>，改用 <c>::: run</c>（PRTA）与 <c>[[output:ID]]</c>。声明本实现符合 v4.0，
    /// 必须先**逐项对照** v4.0 的 CORE / EXT 项集（附录 E）与第 13 章的降级行为，逐条核定后
    /// 才能改这个常量。该项核定是登记在案的遗留工作，见《部署文档》第十节第 1 项与
    /// 《符合性声明》第一节的「已知滞后」——在那里销账之前，这里保持不变。
    /// </para>
    /// </summary>
    public const string SpecificationVersion = "2.1";

    /// <summary>符合性声明文本（标准 14.6 模板；按 2.1 的构造集列出未实现项）。</summary>
    public static string ComplianceStatement =>
        "本渲染器符合 PRT v" + SpecificationVersion + " EXT 等级。\n" +
        "已实现：全部 CORE 与 EXT 项。\n" +
        "未实现：COMP（受限计算）——变量求值、条件、循环、受限运行（run）、附录 B 白名单函数、定点数运算与沙箱。\n" +
        "已知限制：COMP 结构按标准第 13 章降级处理（插值输出原文、计算块输出源码）；不支持 PDF/DOCX/EPUB 输出目标。\n" +
        "符合性测试套件：《PRT 标准》附录 D（D.1 / D.4 / D.5 的 CORE 与 EXT 用例）。";
}
