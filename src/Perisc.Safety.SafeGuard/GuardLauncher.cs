using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Perisc.Safety.SafeGuard;

/// <summary>
/// 模式 A 的启动器（PSS 标准 3.4、7.1）：校验主程序运行体哈希 → 通过后才加载并启动。
/// <para>
/// 启动受控是反绕过的第一等强度来源：程序<b>无法自己启动自己</b>，
/// 启动权在宿主启动器手里，被篡改的运行体没有启动路径。
/// </para>
/// </summary>
internal static class GuardLauncher
{
    /// <summary>
    /// 程序标识的注入变量名。
    /// <para>
    /// 启动器用它把自己的 programId 交给子进程——<b>这个字面量必须与主程序侧的同名常量一致</b>
    /// （<c>Prt.App/Services/SafetyBridge.cs</c>）。两处各写一份而不是让主程序引用启动器程序集：
    /// 启动器刻意只依赖 BCL，不引用被它监护的任何程序集。
    /// </para>
    /// </summary>
    public const string ProgramIdVariable = "PERISC_SAFEGUARD_PROGRAM_ID";

    /// <summary>计算运行体哈希；返回证据格式 <c>sha256:&lt;64 位十六进制&gt;</c>（6.2.3）。</summary>
    public static string ComputeHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(stream, digest);
        return "sha256:" + Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>
    /// 启动主程序 DLL（子进程），并把它纳入作业对象。
    /// </summary>
    /// <param name="assemblyPath">主程序 DLL 的路径（模式 A：主程序编译为 DLL）。</param>
    /// <param name="programArgs">透传给主程序的命令行参数。</param>
    /// <param name="programId">本次启动采用的程序标识，随环境注入子进程。</param>
    /// <param name="job">承载后代进程树的作业对象。</param>
    /// <param name="graphical">
    /// 是否图形路径（双击启动）。它决定要不要给 <b>dotnet 宿主子进程</b>设 <c>CreateNoWindow</c>：
    /// <para>
    /// 宿主 <c>dotnet.exe</c> 本身是<b>控制台子系统</b>程序。本启动器是 GUI 子系统、双击时没有控制台，
    /// 于是宿主作为「父进程无控制台的控制台程序」会被系统分配一个<b>新的可见控制台窗口</b>——
    /// 黑框没有消失，只是从启动器转移到了宿主（2026-09-26 实测：Windows Terminal 弹出标题为
    /// <c>C:\Program Files\dotnet\dotnet.exe</c> 的窗口）。图形路径必须传 <c>true</c> 压掉它。
    /// </para>
    /// <para>
    /// 命令行路径必须传 <c>false</c>：此时启动器已附上父控制台，不给宿主设 <c>CreateNoWindow</c>
    /// 让它继承同一控制台，主程序的输出才能照常到达用户终端。
    /// </para>
    /// </param>
    /// <returns>启动后的子进程；失败抛异常（由调用方记为启动失败，不静默继续）。</returns>
    public static Process Launch(string assemblyPath, IReadOnlyList<string> programArgs, string programId, GuardJob job,
        bool graphical)
    {
        var host = ResolveDotnetHost();
        var startInfo = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            // 见 <param name="graphical">：双击路径压掉宿主自己的控制台窗口；
            // 命令行路径保持默认，让宿主继承已附上的父控制台。
            CreateNoWindow = graphical,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(assemblyPath)) ?? Environment.CurrentDirectory,
        };
        // 程序标识由启动器注入，不由主程序自报：双击启动没有命令行参数可传，
        // 若两侧各自取名（启动器按 DLL 名、主程序按自己的常量），同一次启动会在审计与白名单里
        // 留下两种标识，可追溯性随之断裂。启动权在启动器，标识权也一并放这里。
        startInfo.Environment[ProgramIdVariable] = programId;
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(Path.GetFullPath(assemblyPath));
        foreach (var argument in programArgs)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("启动主程序失败：未获得进程句柄。");

        // 纳入作业必须在进程创建之后尽快完成：晚一步就可能漏掉它已经拉起的后代。
        job.Assign(process.Id);
        return process;
    }

    /// <summary>
    /// 解析 dotnet 宿主的绝对路径（<b>平台特定实现</b>：Windows 上为 <c>dotnet.exe</c>）。
    /// <para>
    /// 解析顺序：① 命令行 <c>--dotnet</c>；② 环境变量 <c>DOTNET_HOST_PATH</c>；
    /// ③ 由当前运行时目录上溯到 dotnet 安装根；④ PATH 中的 <c>dotnet</c>。
    /// 之所以要三级兜底：模式 A 的主程序是 DLL，没有自己的可执行入口，
    /// 必须由某个宿主把它跑起来；找不到宿主时宁可明确失败，也不该悄悄换成"直接启动 DLL"。
    /// </para>
    /// </summary>
    public static string ResolveDotnetHost(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            if (!File.Exists(explicitPath))
            {
                throw new FileNotFoundException("指定的 dotnet 宿主不存在：" + explicitPath);
            }
            return explicitPath;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar);
        // …\shared\Microsoft.NETCore.App\<版本>\ → 上溯三级得到 dotnet 安装根
        var candidate = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtimeDirectory)));
        if (!string.IsNullOrEmpty(candidate))
        {
            var executable = Path.Combine(candidate, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(executable))
            {
                return executable;
            }
        }

        var fromPath = FindOnPath(OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (fromPath is not null)
        {
            return fromPath;
        }

        throw new FileNotFoundException(
            "找不到 dotnet 宿主。请用 --dotnet 指定，或设置环境变量 DOTNET_HOST_PATH。");
    }

    private static string? FindOnPath(string fileName)
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();
        foreach (var directory in paths)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }
            var full = Path.Combine(directory.Trim().Trim('"'), fileName);
            if (File.Exists(full))
            {
                return full;
            }
        }
        return null;
    }
}
