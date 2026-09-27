using System.IO;
using System.Runtime.InteropServices;

namespace Prt.Uninstaller.Platform;

/// <summary>
/// .lnk 快捷方式读写。
/// WPF 没有内置的快捷方式 API，而本仓库 nuget.config 清空了全部包源（离线约束），
/// 因此不能引入 IWshRuntimeLibrary 之类的互操作包——这里走 WScript.Shell COM，
/// 用动态调用拿 CreateShortcut / TargetPath / Save，编译期零依赖、运行期靠系统自带的
/// Windows Script Host。
/// </summary>
internal static class ShortcutFactory
{
    private const string WScriptShellProgId = "WScript.Shell";

    /// <summary>创建快捷方式；已存在同名文件时覆盖。</summary>
    public static void Create(
        string shortcutPath,
        string targetPath,
        string? arguments = null,
        string? workingDirectory = null,
        string? description = null,
        string? iconPath = null,
        int iconIndex = 0)
    {
        var shellType = Type.GetTypeFromProgID(WScriptShellProgId);
        if (shellType is null)
        {
            throw new PlatformNotSupportedException(
                "无法创建快捷方式：系统未提供 Windows Script Host（WScript.Shell）。");
        }

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(shortcutPath);
        try
        {
            link.TargetPath = targetPath;

            if (!string.IsNullOrWhiteSpace(arguments))
            {
                link.Arguments = arguments;
            }

            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                link.WorkingDirectory = workingDirectory;
            }

            if (!string.IsNullOrWhiteSpace(description))
            {
                link.Description = description;
            }

            if (!string.IsNullOrWhiteSpace(iconPath))
            {
                link.IconLocation = iconPath + "," + iconIndex;
            }

            link.Save();
        }
        finally
        {
            Release(link);
            Release(shell);
        }
    }

    public static void EnsureDeleted(string shortcutPath)
    {
        if (File.Exists(shortcutPath))
        {
            File.Delete(shortcutPath);
        }
    }

    /// <summary>读取快捷方式的指向目标（卸载时用来确认某个快捷方式是不是本程序建的）。</summary>
    public static string? ReadTarget(string shortcutPath)
    {
        if (!File.Exists(shortcutPath))
        {
            return null;
        }

        var shellType = Type.GetTypeFromProgID(WScriptShellProgId);
        if (shellType is null)
        {
            return null;
        }

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(shortcutPath);
        try
        {
            return (string?)link.TargetPath;
        }
        catch (Exception ex) when (ex is COMException or PlatformNotSupportedException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            Release(link);
            Release(shell);
        }
    }

    private static void Release(object? comObject)
    {
        if (comObject is null)
        {
            return;
        }

        try
        {
            Marshal.FinalReleaseComObject(comObject);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            // COM 引用释放失败不影响主流程：进程退出时系统会回收。
        }
    }
}
