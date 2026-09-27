using System.Diagnostics.CodeAnalysis;
using System.IO;
using Microsoft.Win32;

namespace Prt.Uninstaller.Platform;

/// <summary>
/// .NET 运行时定位与决策。
/// ────────────────────────────────────────────────────────────────────────────
/// 实测结论（本机 win-x64，SDK 10.0.112 / 运行时 8.0.31 起，详见 docs\安装说明.md）：
///   · apphost **不认** apphost 旁边的 `dotnet` 目录——把运行时摆在同目录照样跑系统那份；
///   · apphost **不认** per-user 注册表 `HKCU\SOFTWARE\dotnet\Setup\InstallLocation`；
///   · apphost **只认** `DOTNET_ROOT` 环境变量（错误输出会明说 ".NET location: …"）。
///
/// 所以"按用户安装 + 自带运行时 + 双击即启"的唯一落点是**写用户级 DOTNET_ROOT**：
/// 它进 HKCU\Environment，资源管理器重启后其派生的所有进程都带着它。
/// 代价是它对该用户下的 .NET 程序全局可见——这也是为什么这里默认走「能用系统就用系统」。
/// </summary>
internal static class RuntimeLocator
{
    /// <summary>阅读器最低需要的共享框架（net8.0-windows → Microsoft.WindowsDesktop.App）。</summary>
    private static readonly Version RequiredVersion = new(8, 0, 0);

    private const string WindowsDesktop = "Microsoft.WindowsDesktop.App";
    private const string NetCore = "Microsoft.NETCore.App";
    private const string DotnetRootVariable = "DOTNET_ROOT";

    /// <summary>
    /// 目标机是否已有满足要求的 .NET 桌面运行时（不关心装在何处）。
    /// 命中则安装器不必携带运行时，安装包小一个量级，也不必写 DOTNET_ROOT。
    /// </summary>
    public static bool HasSufficientRuntime()
    {
        return FindSatisfyingRoot(WindowsDesktop) is not null && FindSatisfyingRoot(NetCore) is not null;
    }

    /// <summary>把便携运行时写到安装目录，并让该用户后续的 apphost 找到它。</summary>
    public static void BindPortableRoot(string portableRoot)
    {
        Environment.SetEnvironmentVariable(DotnetRootVariable, portableRoot, EnvironmentVariableTarget.User);
    }

    /// <summary>
    /// 回滚：只有当 DOTNET_ROOT 仍指向本安装器写入的路径时才删（避免误删用户或别的软件设的值）。
    /// </summary>
    public static bool TryUnbindPortableRoot(string portableRoot)
    {
        var current = Environment.GetEnvironmentVariable(DotnetRootVariable, EnvironmentVariableTarget.User);
        if (string.IsNullOrEmpty(current) ||
            !string.Equals(current.TrimEnd('\\', '/'), portableRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Environment.SetEnvironmentVariable(DotnetRootVariable, null, EnvironmentVariableTarget.User);
        return true;
    }

    public static string? ReadBoundPortableRoot()
    {
        return Environment.GetEnvironmentVariable(DotnetRootVariable, EnvironmentVariableTarget.User);
    }

    /// <summary>按 hostfxr 的顺序找一份满足版本要求的共享框架根目录。</summary>
    private static string? FindSatisfyingRoot(string framework)
    {
        foreach (var root in CandidateRoots())
        {
            if (Satisfies(root, framework))
            {
                return root;
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        // 当前进程已继承到的 DOTNET_ROOT（可能是本安装器先前写的，也可能来自系统环境）。
        var inherited = Environment.GetEnvironmentVariable(DotnetRootVariable);
        if (!string.IsNullOrWhiteSpace(inherited))
        {
            yield return inherited;
        }

        var registryRoot = ReadInstallLocationFromRegistry();
        if (!string.IsNullOrWhiteSpace(registryRoot))
        {
            yield return registryRoot;
        }

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
    }

    /// <summary>
    /// .NET 安装根目录：per-user 的 InstallLocation 优先于机器级（与 hostfxr 的查找顺序同向）。
    /// 只用于"猜系统 dotnet 在哪"，因此读不到时静默跳过，不影响主判定。
    /// </summary>
    private static string? ReadInstallLocationFromRegistry()
    {
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.CurrentUser, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey("SOFTWARE\\dotnet");
            var value = key?.GetValue("InstallLocation") as string;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>该根目录下存在满足最低版本要求的具体版本文件夹。</summary>
    private static bool Satisfies(string root, string framework)
    {
        var sharedDir = Path.Combine(root, "shared", framework);
        if (!Directory.Exists(sharedDir))
        {
            return false;
        }

        foreach (var dir in Directory.EnumerateDirectories(sharedDir))
        {
            var name = Path.GetFileName(dir);
            if (Version.TryParse(name, out var version) && version >= RequiredVersion)
            {
                return true;
            }
        }

        return false;
    }
}
