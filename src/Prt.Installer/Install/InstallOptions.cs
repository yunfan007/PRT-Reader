namespace Prt.Installer.Install;

/// <summary>运行时的携带策略：决定安装包要不要吃 160 MB 便携运行时。</summary>
public enum RuntimeMode
{
    /// <summary>系统有可用的 .NET 8 桌面运行时就复用，没有才释放便携运行时并写 DOTNET_ROOT。</summary>
    Automatic,

    /// <summary>始终释放便携运行时（面向确定要离网交付的场景）。</summary>
    AlwaysPortable,

    /// <summary>只使用系统运行时，绝不携带（安装包最小，但目标机必须先有 .NET）。</summary>
    SystemOnly,
}

/// <summary>一次 installation 的全部可选输入。默认即推荐口径。</summary>
public sealed class InstallOptions
{
    public const string DefaultRelativeInstallDirectory = @"%LOCALAPPDATA%\PRT Reader";

    /// <summary>默认安装位置：按用户安装，落在 %LOCALAPPDATA% 下，不需要管理员权限。</summary>
    public static string DefaultInstallDirectory()
    {
        return Environment.ExpandEnvironmentVariables(DefaultRelativeInstallDirectory);
    }

    /// <summary>目标安装目录（用户可在安装选项页改）。</summary>
    public string InstallDirectory { get; set; } = DefaultInstallDirectory();

    /// <summary>关联 .prt / .psr / .mdp 三类文件。</summary>
    public bool AssociateFiles { get; set; } = true;

    /// <summary>资源管理器右键菜单与「打开方式」列表项。</summary>
    public bool ShellMenus { get; set; } = true;

    /// <summary>桌面快捷方式。</summary>
    public bool DesktopShortcut { get; set; } = true;

    /// <summary>随装示例文档与帮助。</summary>
    public bool IncludeSamples { get; set; } = true;

    public RuntimeMode Runtime { get; set; } = RuntimeMode.Automatic;

    public string ProductName { get; } = "PRT 阅读器";
}
