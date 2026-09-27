using System.Windows.Markup;
using Prt.App.Services;

namespace Prt.App.Theming;

/// <summary>
/// XAML 取词标记扩展：<c>Header="{loc:Loc menu.file}"</c>。
/// <para>
/// 在 XAML 加载期解析为当前语言的文案，因此语言切换在**重启后**完全生效。
/// 需要运行时刷新的位置（状态栏等）请直接调用 <see cref="Localizer.T(string)"/>。
/// </para>
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    /// <summary>词条键。</summary>
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Localizer.T(Key);
}
