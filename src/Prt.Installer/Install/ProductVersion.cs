using System.Reflection;

namespace Prt.Installer.Install;

/// <summary>安装器与已安装版本号的唯一来源（卸载器按同样口径读，避免两处漂移）。</summary>
public static class ProductVersion
{
    public static string Current { get; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                                            ?? "1.0.0";
}
