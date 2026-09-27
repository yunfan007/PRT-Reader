using System.Globalization;

namespace Prt.App.Services;

/// <summary>
/// 界面语言（本地化）：以「键 → 文案」表驱动，内置中文与英文两套词条。
/// <para>
/// 设计取舍：不引入 .resx（纯代码表更便于审阅与自检）；语言切换在**重启后**完全生效，
/// 因为菜单 / 标题栏等由 XAML 以 <see cref="Theming.LocExtension"/> 在加载期解析。
/// 切换语言时状态栏会明确提示重启。
/// </para>
/// <para>
/// 查词顺序：当前语言表 → 中文表（兜底）→ 键本身（便于一眼看出漏翻）。
/// </para>
/// </summary>
public static class Localizer
{
    /// <summary>语言偏好：跟随系统。</summary>
    public const string SystemDefault = "system";

    /// <summary>简体中文。</summary>
    public const string Chinese = "zh";

    /// <summary>英文。</summary>
    public const string English = "en";

    private static string _language = Chinese;

    /// <summary>当前生效的语言（<c>zh</c> / <c>en</c>）。</summary>
    public static string Language => _language;

    /// <summary>当前是否中文界面。</summary>
    public static bool IsChinese => _language == Chinese;

    /// <summary>语言切换后触发（供状态栏提示、需要即时刷新的少数位置使用）。</summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>
    /// 应用语言偏好（<c>system</c> / <c>zh</c> / <c>en</c>）。未知取值按「跟随系统」处理。
    /// </summary>
    public static void Apply(string? preference)
    {
        var resolved = ResolveLanguage(preference);
        var changed = resolved != _language;
        _language = resolved;
        if (changed)
        {
            LanguageChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>把语言偏好解析为具体语言；<c>system</c> 或未知值按系统界面语言判定。</summary>
    public static string ResolveLanguage(string? preference)
    {
        if (string.Equals(preference, Chinese, StringComparison.OrdinalIgnoreCase))
        {
            return Chinese;
        }

        if (string.Equals(preference, English, StringComparison.OrdinalIgnoreCase))
        {
            return English;
        }

        var name = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return string.Equals(name, "zh", StringComparison.OrdinalIgnoreCase) ? Chinese : English;
    }

    /// <summary>取词条。</summary>
    public static string T(string key) => Lookup(key);

    /// <summary>取词条并格式化（词条里用 <c>{0}</c> 之类的占位符）。</summary>
    public static string T(string key, params object?[] args)
    {
        var format = Lookup(key);
        return args.Length == 0
            ? format
            : string.Format(CultureInfo.CurrentCulture, format, args);
    }

    /// <summary>词条是否存在（自检用）。</summary>
    internal static bool Has(string key) => Strings.Zh.ContainsKey(key);

    private static string Lookup(string key)
    {
        var table = _language == English ? Strings.En : Strings.Zh;
        if (table.TryGetValue(key, out var text))
        {
            return text;
        }

        // 英文表缺条时回退中文，保证界面不会出现裸键。
        return Strings.Zh.TryGetValue(key, out var fallback) ? fallback : key;
    }
}
