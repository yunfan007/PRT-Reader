namespace Prt.Installer.Install;

/// <summary>
/// 从 HKCU\Software\PRT Reader 读回的安装快照。
/// 卸载器唯一的"记忆"——卸载器没有旁证可查，它只能相信这份登记；
/// 因此 PortableRuntimeRoot 必须原样存、原样读，绝不在读的时候做路径规范化
/// （规范化一旦和写的时候口径不同，回滚就会指错目录）。
/// </summary>
public sealed class InstallStateSnapshot
{
    public string InstallDirectory { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public bool AssociateFiles { get; init; }
    public bool ShellMenus { get; init; }
    public bool DesktopShortcut { get; init; }
    public bool IncludeSamples { get; init; }
    public bool PortableRuntime { get; init; }
    public string? PortableRuntimeRoot { get; init; }
}
