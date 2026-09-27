namespace Prt.Installer.Install;

/// <summary>
/// 安装结果：完成页与「启动阅读器」都要用它。
/// 独立成文件——卸载器只关心 PortableRuntime 那两个字段，不该为了类型可见性把整个
/// InstallPlan.RunAsync 一起链进来。
/// </summary>
public sealed record InstallOutcome
{
    public string InstallDirectory { get; init; } = string.Empty;
    public string LauncherPath { get; init; } = string.Empty;
    public string Version { get; init; } = "1.0.0";
    public string InstalledAt { get; init; } = string.Empty;

    /// <summary>本次是否释放了便携运行时。</summary>
    public bool PortableRuntime { get; init; }

    /// <summary>便携运行时的实际落地目录（例如 &lt;安装目录&gt;\dotnet）。</summary>
    public string PortableRuntimeRoot { get; init; } = string.Empty;

    /// <summary>是否写了用户级 DOTNET_ROOT（写了的那种情况需要刷新资源管理器才生效）。</summary>
    public bool NeedsShellRefresh { get; init; }
}
