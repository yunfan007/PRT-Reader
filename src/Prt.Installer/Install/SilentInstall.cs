using System.IO;
using Prt.Uninstaller.Platform;

namespace Prt.Installer.Install;

/// <summary>
/// 静默安装：`PRT-Installer.exe /install [开关…]`，全程不弹窗口。
///
/// 存在的理由有两个，缺一不可：
///   1) **可验证**——向导只有人能点，机器没法替人点一遍"下一步"。没有这条通道，
///      "装完之后对不对"就永远只能靠人工回忆。
///   2) **可批量部署**——《部署文档》里要写"给三十台机器装一遍"，逐台点向导不是部署，
///       是手工劳动。
///
/// 开关一律写成 `/名=值`，缺省与向导页一致；不认识的参数写进日志而不是悄悄忽略——
/// 静默忽略会让"我明明加了参数怎么没生效"变成最难查的一类故障。
/// </summary>
public static class SilentInstall
{
    private const string SwitchPrefix = "/";
    private const string DefaultLogName = "prt-installer.log";

    public static async Task<int> RunAsync(string[] args)
    {
        var options = ParseOptions(args, out var logPath, out var unknown);

        var lines = new List<string>
        {
            "PRT 阅读器 静默安装",
            "安装位置：" + options.InstallDirectory,
            "运行时策略：" + options.Runtime,
            "文件关联：" + (options.AssociateFiles ? "开" : "关"),
        };
        foreach (var line in unknown)
        {
            lines.Add("未识别的参数：" + line);
        }

        var log = (string message) =>
        {
            lines.Add(message);
            SafeAppend(logPath, message);
        };

        log("开始安装…");
        try
        {
            var outcome = await InstallPlan.RunAsync(options, log, _ => { }, CancellationToken.None)
                .ConfigureAwait(true);        // 本进程没有 UI 上下文可回，保持默认上下文即可

            log("安装完成。安装目录：" + outcome.InstallDirectory);
            if (outcome.PortableRuntime)
            {
                log("已随装便携运行时，并写入用户级 DOTNET_ROOT → " + outcome.PortableRuntimeRoot);
            }

            SafeAppend(logPath, "RESULT: OK " + outcome.InstallDirectory + Environment.NewLine);
            return 0;
        }
        catch (Exception ex)
        {
            log("安装失败：" + ex.Message);
            SafeAppend(logPath, "RESULT: FAIL " + ex.Message + Environment.NewLine);
            return 1;
        }
    }

    private static InstallOptions ParseOptions(string[] args, out string logPath, out List<string> unknown)
    {
        logPath = Path.Combine(Path.GetTempPath(), DefaultLogName);
        unknown = new List<string>();

        var options = new InstallOptions { Runtime = RuntimeMode.Automatic };

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith(SwitchPrefix, StringComparison.Ordinal))
            {
                unknown.Add(arg);
                continue;
            }

            var body = arg.Substring(SwitchPrefix.Length);
            var separator = body.IndexOf('=', StringComparison.Ordinal);
            var name = separator < 0 ? body : body.Substring(0, separator);
            var value = separator < 0 ? "true" : body.Substring(separator + 1);

            switch (name.ToLowerInvariant())
            {
                case "dir":
                    options.InstallDirectory = Environment.ExpandEnvironmentVariables(value);
                    break;
                case "log":
                    logPath = value;
                    break;
                case "associate":
                    options.AssociateFiles = ParseBool(value);
                    break;
                case "shellmenus":
                    options.ShellMenus = ParseBool(value);
                    break;
                case "desktop":
                    options.DesktopShortcut = ParseBool(value);
                    break;
                case "samples":
                    options.IncludeSamples = ParseBool(value);
                    break;
                case "runtime":
                    options.Runtime = value.ToLowerInvariant() switch
                    {
                        "portable" or "always" => RuntimeMode.AlwaysPortable,
                        "system" or "none" => RuntimeMode.SystemOnly,
                        _ => RuntimeMode.Automatic,
                    };
                    break;
                default:
                    unknown.Add(arg);
                    break;
            }
        }

        return options;
    }

    /// <summary>`/desktop=false` 关；`/desktop`、`/desktop=true`、`/desktop=1` 开。</summary>
    private static bool ParseBool(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
    }

    private static void SafeAppend(string path, string text)
    {
        try
        {
            // 目标目录不存在时 AppendAllText 会直接抛 DirectoryNotFoundException——
            // 而这里全部 catch 掉了，于是"/log= 到一个还没建的目录"会变成"什么证据都没有地失败"。
            // 安装目录本来就是安装器自己建的，日志目录没有理由不建：先建父目录。
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(path, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 日志写不进去（只读 / 非法字符）也不该拖垮安装本身。
        }
    }
}
