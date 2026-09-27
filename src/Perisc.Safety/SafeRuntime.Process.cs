using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Perisc.Safety;

/// <summary>
/// 模块四 SRT · 进程与系统（PROC）：起子进程 / 结束进程 / 加载原生库 / 计划任务与自启。
/// 每个函数完成「申报 → 裁决 → 执行 → 审计」四步，返回结构化结果，不抛异常。
/// </summary>
public sealed class SafeProcess
{
    /// <summary>启动子进程。取代 <c>Process.Start</c>（PROC-01）。</summary>
    public SafeResult<Process?> Start(string fileName, string arguments = "", string? reason = null)
        => SafeExecutor.Run("PROC-01", fileName, Reason(reason, "启动子进程 " + Path.GetFileName(fileName)),
            () => Process.Start(new ProcessStartInfo(fileName, arguments)), MatchMode.Exact);

    /// <summary>启动子进程（可控启动信息）。取代 <c>Process.Start(ProcessStartInfo)</c>（PROC-01）。</summary>
    public SafeResult<Process?> Start(ProcessStartInfo startInfo, string? reason = null)
        => SafeExecutor.Run("PROC-01", startInfo?.FileName ?? string.Empty,
            Reason(reason, "启动子进程 " + Path.GetFileName(startInfo?.FileName ?? string.Empty)),
            () => Process.Start(startInfo!), MatchMode.Exact);

    /// <summary>结束指定进程。取代 <c>Process.Kill</c>（PROC-02）。</summary>
    public SafeResult Kill(int processId, string? reason = null)
        => SafeExecutor.Run("PROC-02", "pid:" + processId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Reason(reason, "结束进程"),
            () =>
            {
                using var process = Process.GetProcessById(processId);
                process.Kill();
            }, MatchMode.Exact);

    /// <summary>按进程名结束其它进程。取代 <c>Process.GetProcessesByName</c> + <c>Kill</c>（PROC-02）。</summary>
    public SafeResult<int> KillByName(string processName, string? reason = null)
        => SafeExecutor.Run("PROC-02", "process:" + processName, Reason(reason, "结束进程 " + processName),
            () =>
            {
                var killed = 0;
                foreach (var candidate in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        candidate.Kill();
                        killed++;
                    }
                    finally
                    {
                        candidate.Dispose();
                    }
                }
                return killed;
            }, MatchMode.Exact);

    /// <summary>
    /// 加载原生库。取代 <c>NativeLibrary.Load</c> 与 <c>[DllImport]</c> 动态装载路径（PROC-04）。
    /// 只提供装载，不提供调用——与本层「不增强」一致（3.8）。
    /// </summary>
    public SafeResult<IntPtr> LoadNativeLibrary(string libraryPath, string? reason = null)
        => SafeExecutor.Run("PROC-04", libraryPath, Reason(reason, "加载原生库"),
            () => NativeLibrary.Load(libraryPath), MatchMode.Exact);

    /// <summary>
    /// 创建计划任务（PROC-06）。
    /// <para><b>平台特定实现</b>：经 Windows 的 <c>schtasks.exe</c> 创建；非 Windows 上该命令不存在，
    /// 调用会由执行侧抛出并由本函数转成 <see cref="PssCode.Internal"/> 返回（不执行成功也不静默成功）。</para>
    /// <para><b>为什么用 <c>ArgumentList</c> 而不是拼一个命令串</b>：任务名与命令行都来自调用方，
    /// 手工拼接（再手工去掉引号）等于自己实现一遍 Windows 的命令行转义规则——拼错一次就是一次
    /// 参数注入。交给 <c>ArgumentList</c> 由框架按平台规则转义，既不拼串也不猜引号（D-24）。</para>
    /// </summary>
    public SafeResult CreateScheduledTask(string taskName, string commandLine, string? reason = null)
        => SafeExecutor.Run("PROC-06", "task:" + taskName, Reason(reason, "创建计划任务 " + taskName),
            () =>
            {
                var startInfo = new ProcessStartInfo("schtasks.exe");
                startInfo.ArgumentList.Add("/Create");
                startInfo.ArgumentList.Add("/F");
                startInfo.ArgumentList.Add("/TN");
                startInfo.ArgumentList.Add(taskName);
                startInfo.ArgumentList.Add("/TR");
                startInfo.ArgumentList.Add(commandLine);
                startInfo.ArgumentList.Add("/SC");
                startInfo.ArgumentList.Add("ONCE");
                startInfo.ArgumentList.Add("/ST");
                startInfo.ArgumentList.Add("00:00");
                using var process = Process.Start(startInfo);
                process?.WaitForExit(5000);
            }, MatchMode.Exact);

    /// <summary>
    /// 设置开机自启（PROC-07）。
    /// <para><b>平台特定实现</b>：写入 Windows 注册表 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>；
    /// 仅当前用户、不需要管理员权限。非 Windows 上注册表 API 不可用，按执行失败返回。</para>
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public SafeResult SetStartupRun(string entryName, string commandLine, string? reason = null)
        => SafeExecutor.Run("PROC-07", "startup:" + entryName, Reason(reason, "设置开机自启 " + entryName),
            () =>
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key is null)
                {
                    throw new InvalidOperationException("无法打开自启动注册表项（HKCU Run）。");
                }
                key.SetValue(entryName, commandLine);
            }, MatchMode.Exact);

    private static string Reason(string? reason, string fallback)
        => string.IsNullOrWhiteSpace(reason) ? fallback : reason;
}

/// <summary>
/// 模块四 SRT · 配置与注册表（CFG）：写系统配置 / 注册表 / 文件关联 / 环境变量。
/// 每个函数完成「申报 → 裁决 → 执行 → 审计」四步，返回结构化结果，不抛异常。
/// <para><b>平台特定</b>：本类的注册表类成员依赖 Windows 注册表 API（HKCU），
/// 已按平台标注；非 Windows 上调用会按执行失败返回，不会静默成功。</para>
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class SafeConfig
{
    /// <summary>
    /// 写入注册表值（CFG-01）。
    /// <para><b>平台特定实现</b>：Windows 注册表；非 Windows 上不可用，按执行失败返回。</para>
    /// </summary>
    public SafeResult SetRegistryValue(string keyPath, string valueName, object value, string? reason = null)
        => SafeExecutor.Run("CFG-01", "registry:" + keyPath + "\\" + valueName,
            Reason(reason, "写入注册表 " + valueName),
            () =>
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath);
                if (key is null)
                {
                    throw new InvalidOperationException("无法创建注册表项：" + keyPath);
                }
                key.SetValue(valueName, value);
            }, MatchMode.PathPrefix);

    /// <summary>
    /// 修改文件关联或协议处理器（CFG-02）。
    /// <para><b>平台特定实现</b>：写入 HKCU 的 <c>Software\Classes</c>；仅当前用户生效，不需要管理员权限。</para>
    /// </summary>
    public SafeResult SetFileAssociation(string extension, string progId, string? reason = null)
        => SafeExecutor.Run("CFG-02", "assoc:" + extension, Reason(reason, "关联扩展名 " + extension),
            () =>
            {
                if (extension.Length == 0 || extension[0] != '.')
                {
                    throw new ArgumentException("扩展名必须以点开头：" + extension, nameof(extension));
                }
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + extension);
                if (key is null)
                {
                    throw new InvalidOperationException("无法创建文件关联项：" + extension);
                }
                key.SetValue(string.Empty, progId);
            }, MatchMode.Exact);

    /// <summary>持久化环境变量（CFG-03）。取代 <c>Environment.SetEnvironmentVariable</c> 的用户级持久化写法。</summary>
    public SafeResult SetEnvironmentVariable(string name, string value, string? reason = null)
        => SafeExecutor.Run("CFG-03", "env:" + name, Reason(reason, "设置环境变量 " + name),
            () => Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User),
            MatchMode.Exact);

    private static string Reason(string? reason, string fallback)
        => string.IsNullOrWhiteSpace(reason) ? fallback : reason;
}
