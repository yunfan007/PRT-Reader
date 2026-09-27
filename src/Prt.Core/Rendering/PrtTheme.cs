using Prt.Core.Syntax;

namespace Prt.Core.Rendering;

/// <summary>语义角色的命名与中文标签（用于 HTML 类名与降级前缀，标准 8.3 / 13）。</summary>
public static class SemanticNaming
{
    /// <summary>语义块的英文类名（用于 HTML/CSS 与主题变量名）。</summary>
    public static string CssName(SemanticKind kind) => kind switch
    {
        SemanticKind.Note => "note",
        SemanticKind.Warning => "warning",
        SemanticKind.Danger => "danger",
        SemanticKind.Attention => "attention",
        SemanticKind.Example => "example",
        SemanticKind.Definition => "definition",
        SemanticKind.Quote => "quote",
        SemanticKind.Comment => "comment",
        SemanticKind.Info => "info",
        SemanticKind.Tip => "tip",
        SemanticKind.Result => "result",
        SemanticKind.Task => "task",
        _ => "note",
    };

    /// <summary>降级输出使用的中文前缀标签（标准 13：如「[注意] …」）。</summary>
    public static string Label(SemanticKind kind) => kind switch
    {
        SemanticKind.Note => "备注",
        SemanticKind.Warning => "警告",
        SemanticKind.Danger => "危险",
        SemanticKind.Attention => "注意",
        SemanticKind.Example => "示例",
        SemanticKind.Definition => "定义",
        SemanticKind.Quote => "引用",
        SemanticKind.Comment => "批注",
        SemanticKind.Info => "信息",
        SemanticKind.Tip => "提示",
        SemanticKind.Result => "结果",
        SemanticKind.Task => "待办",
        _ => "提示",
    };

    /// <summary>全部语义块类型（用于主题变量枚举）。</summary>
    public static readonly SemanticKind[] All =
    {
        SemanticKind.Note, SemanticKind.Warning, SemanticKind.Danger, SemanticKind.Attention,
        SemanticKind.Example, SemanticKind.Definition, SemanticKind.Quote, SemanticKind.Comment,
        SemanticKind.Info, SemanticKind.Tip, SemanticKind.Result, SemanticKind.Task,
    };
}

/// <summary>
/// 主题（《PRT 标准》12.2 与附录 C）。主题只覆盖预设样式变量，不允许任意 CSS；
/// 主题只改外观，不改语义。
/// </summary>
public sealed class PrtTheme
{
    private readonly Dictionary<string, string> _variables = new(StringComparer.OrdinalIgnoreCase);

    public PrtTheme(string name)
    {
        Name = name;
    }

    /// <summary>主题名。</summary>
    public string Name { get; }

    /// <summary>全部样式变量。</summary>
    public IReadOnlyDictionary<string, string> Variables => _variables;

    /// <summary>读取变量，缺失时返回回退值。</summary>
    public string Get(string key, string fallback = "")
        => _variables.TryGetValue(key, out var value) ? value : fallback;

    /// <summary>写入变量（`::: theme` 覆盖入口）。</summary>
    public void Set(string key, string value) => _variables[key] = value;

    /// <summary>内置主题列表（标准 12.2）。</summary>
    public static readonly string[] BuiltInNames = { "default", "dark", "print", "accessible" };

    /// <summary>取语义块底色（附录 C：`--callout-&lt;类型&gt;-bg`）。</summary>
    public string CalloutBackground(SemanticKind kind)
        => Get($"--callout-{SemanticNaming.CssName(kind)}-bg", Get("--page-surface"));

    /// <summary>取语义块侧边色（附录 C：`--callout-&lt;类型&gt;-border`）。</summary>
    public string CalloutBorder(SemanticKind kind)
        => Get($"--callout-{SemanticNaming.CssName(kind)}-border", Get("--page-border"));

    /// <summary>构造内置主题；未知名称回退为 `default` 并报告回退名。</summary>
    public static PrtTheme BuiltIn(string? name, out string resolvedName)
    {
        var target = (name ?? "default").Trim().ToLowerInvariant();
        PrtTheme theme;
        switch (target)
        {
            case "dark":
                theme = CreateDark();
                resolvedName = "dark";
                break;
            case "print":
                theme = CreatePrint();
                resolvedName = "print";
                break;
            case "accessible":
                theme = CreateAccessible();
                resolvedName = "accessible";
                break;
            default:
                theme = CreateDefault();
                resolvedName = "default";
                break;
        }
        return theme;
    }

    /// <summary>
    /// 解析文档主题：内置主题名 + 文档内 `::: theme` 块的叠加覆盖
    /// （同名多块后者优先；未命名块仅作即时覆盖，标准 12.2）。
    /// </summary>
    public static PrtTheme Resolve(string? requestedName, IEnumerable<ThemeBlock> themeBlocks, out string resolvedName)
    {
        var blocks = themeBlocks.ToList();

        // 文档内定义的主题名优先于 meta.theme（`meta.theme` 可引用 `::: theme` 声明的名称）。
        string? declaredName = null;
        foreach (var block in blocks)
        {
            if (!string.IsNullOrWhiteSpace(block.Name))
            {
                declaredName = block.Name!.Trim();
            }
        }

        var effectiveName = requestedName;
        if (!string.IsNullOrWhiteSpace(effectiveName)
            && declaredName is not null
            && effectiveName.Trim().Equals(declaredName, StringComparison.Ordinal))
        {
            // 使用文档内声明的主题：以其 base 为底。
            var baseTheme = blocks.Last(b => !string.IsNullOrWhiteSpace(b.Name)).Base;
            var theme = BuiltIn(baseTheme, out _);
            Apply(theme, blocks.Where(b => !string.IsNullOrWhiteSpace(b.Name)));
            resolvedName = declaredName!;
            return theme;
        }

        var result = BuiltIn(effectiveName, out var builtInName);
        Apply(result, blocks);
        resolvedName = declaredName ?? builtInName;
        return result;
    }

    private static void Apply(PrtTheme theme, IEnumerable<ThemeBlock> blocks)
    {
        foreach (var block in blocks)
        {
            foreach (var pair in block.Variables)
            {
                var key = pair.Key;
                var value = ResolveValue(pair.Value);
                theme.Set(key, value);
            }
        }
    }

    /// <summary>把命名色解析为具体色值；非命名色则原样保留（主题变量取值为命名色，标准附录 C）。</summary>
    private static string ResolveValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
        if (NamedColorLookup.TryResolve(value.Trim(), out var color))
        {
            return ColorPalette.ToHex(color);
        }
        return value.Trim();
    }

    // ────────────────────────────── 四内置主题 ──────────────────────────────

    private static PrtTheme CreateDefault()
    {
        var theme = new PrtTheme("default");
        BaseCommon(theme, "#ffffff", "#f6f8fa", "#1f2328", "#57606a", "#8c959f", "#d0d7de", "#0969da");
        Callouts(theme, "#ddf4ff", "#54aeff", "#fff8c5", "#d4a72c", "#ffebe9", "#ff8182", "#f6f8fa", "#8c959f",
            "#dafbe1", "#4ac26b", "#f6f8fa", "#8c959f", "#f6f8fa", "#8c959f", "#f6f8fa", "#8c959f",
            "#ddf4ff", "#54aeff", "#ddf4ff", "#2da44e", "#f6f8fa", "#8c959f", "#ddf4ff", "#54aeff");
        return theme;
    }

    private static PrtTheme CreateDark()
    {
        var theme = new PrtTheme("dark");
        BaseCommon(theme, "#1e1e1e", "#252526", "#d4d4d4", "#a0a0a0", "#6e6e6e", "#3c3c3c", "#4daafc");
        Callouts(theme, "#0d2b45", "#4daafc", "#3b2f12", "#d4a72c", "#3d1418", "#f85149", "#2a2a2a", "#6e6e6e",
            "#12301c", "#3fb950", "#2a2a2a", "#6e6e6e", "#2a2a2a", "#6e6e6e", "#2a2a2a", "#6e6e6e",
            "#0d2b45", "#4daafc", "#12301c", "#3fb950", "#2a2a2a", "#6e6e6e", "#0d2b45", "#4daafc");
        return theme;
    }

    private static PrtTheme CreatePrint()
    {
        var theme = new PrtTheme("print");
        BaseCommon(theme, "#ffffff", "#fafafa", "#000000", "#333333", "#666666", "#999999", "#000000");
        Callouts(theme, "#f2f2f2", "#666666", "#f7f7f7", "#8a8a8a", "#f2f2f2", "#666666", "#f7f7f7", "#8a8a8a",
            "#f7f7f7", "#8a8a8a", "#f7f7f7", "#8a8a8a", "#f7f7f7", "#8a8a8a", "#f7f7f7", "#8a8a8a",
            "#f2f2f2", "#666666", "#f2f2f2", "#666666", "#f7f7f7", "#8a8a8a", "#f2f2f2", "#666666");
        theme.Set("--highlight-bg", "#e8e8e8");
        return theme;
    }

    private static PrtTheme CreateAccessible()
    {
        // 无障碍主题：正文/背景对比度 ≥ 4.5:1（黑字白底，标准 12.2）。
        var theme = new PrtTheme("accessible");
        BaseCommon(theme, "#ffffff", "#ffffff", "#000000", "#1a1a1a", "#333333", "#000000", "#0000cc");
        Callouts(theme, "#eaf3ff", "#0000cc", "#fff3d6", "#8a5a00", "#ffecec", "#b30000", "#f2f2f2", "#000000",
            "#eaf9ec", "#006400", "#f2f2f2", "#000000", "#f2f2f2", "#000000", "#f2f2f2", "#000000",
            "#eaf3ff", "#0000cc", "#eaf9ec", "#006400", "#f2f2f2", "#000000", "#eaf3ff", "#0000cc");
        theme.Set("--highlight-bg", "#fff3a3");
        return theme;
    }

    private static void BaseCommon(
        PrtTheme theme,
        string pageBg,
        string surface,
        string text,
        string textSecondary,
        string textMuted,
        string border,
        string link)
    {
        theme.Set("--page-bg", pageBg);
        theme.Set("--page-surface", surface);
        theme.Set("--page-surface-muted", surface);
        theme.Set("--page-text", text);
        theme.Set("--page-text-secondary", textSecondary);
        theme.Set("--page-text-muted", textMuted);
        theme.Set("--page-border", border);
        theme.Set("--page-brand", link);
        theme.Set("--heading-color", text);
        theme.Set("--link-color", link);
        theme.Set("--code-bg", surface);
        theme.Set("--code-text", text);
        theme.Set("--kbd-bg", surface);
        theme.Set("--kbd-text", text);
        theme.Set("--kbd-border", border);
        theme.Set("--highlight-bg", "#fff3a3");
        theme.Set("--spacing-unit", "8px");
        theme.Set("--radius-small", "4px");
        theme.Set("--radius-medium", "6px");
        theme.Set("--font-sans-serif", "\"Segoe UI\", \"Microsoft YaHei\", sans-serif");
        theme.Set("--font-mono", "Consolas, \"Courier New\", monospace");
    }

    private static void Callouts(
        PrtTheme theme,
        string noteBg, string noteBorder,
        string warningBg, string warningBorder,
        string dangerBg, string dangerBorder,
        string attentionBg, string attentionBorder,
        string exampleBg, string exampleBorder,
        string definitionBg, string definitionBorder,
        string quoteBg, string quoteBorder,
        string commentBg, string commentBorder,
        string infoBg, string infoBorder,
        string tipBg, string tipBorder,
        string resultBg, string resultBorder,
        string taskBg, string taskBorder)
    {
        void Set(SemanticKind kind, string bg, string border)
        {
            var name = SemanticNaming.CssName(kind);
            theme.Set($"--callout-{name}-bg", bg);
            theme.Set($"--callout-{name}-border", border);
        }

        Set(SemanticKind.Note, noteBg, noteBorder);
        Set(SemanticKind.Warning, warningBg, warningBorder);
        Set(SemanticKind.Danger, dangerBg, dangerBorder);
        Set(SemanticKind.Attention, attentionBg, attentionBorder);
        Set(SemanticKind.Example, exampleBg, exampleBorder);
        Set(SemanticKind.Definition, definitionBg, definitionBorder);
        Set(SemanticKind.Quote, quoteBg, quoteBorder);
        Set(SemanticKind.Comment, commentBg, commentBorder);
        Set(SemanticKind.Info, infoBg, infoBorder);
        Set(SemanticKind.Tip, tipBg, tipBorder);
        Set(SemanticKind.Result, resultBg, resultBorder);
        Set(SemanticKind.Task, taskBg, taskBorder);
    }
}

/// <summary>命名色文本查找（避免各处重复实现字符串→枚举映射）。</summary>
internal static class NamedColorLookup
{
    public static bool TryResolve(string text, out NamedColor color)
        => Parsing.ParsingHelpers.TryParseNamedColor(text, out color);
}
