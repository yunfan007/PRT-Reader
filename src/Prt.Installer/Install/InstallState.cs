using System.IO;
using Microsoft.Win32;

namespace Prt.Installer.Install;

/// <summary>
/// 安装信息登记（HKCU\Software\PRT Reader）——**只读**侧。
/// 卸载器靠它知道装到哪、装了哪些组件、便携运行时是不是自己写的——
/// 没有这份登记就只能靠"猜"，猜错就会把用户自己改过的东西删掉。
///
/// 写入侧在 InstallPlan 末尾（谁组装谁记账），因此本文件不依赖 InstallOptions /
/// InstallOutcome，卸载器可以只链这几个查询函数而不必把整个安装执行体拖进来。
/// </summary>
internal sealed class InstallState
{
    private const string KeyPath = @"Software\PRT Reader";

    public static InstallStateSnapshot? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        if (key is null)
        {
            return null;
        }

        var installDir = key.GetValue("InstallDir") as string;
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            return null;
        }

        return new InstallStateSnapshot
        {
            InstallDirectory = installDir,
            Version = key.GetValue("Version") as string ?? string.Empty,
            AssociateFiles = Flag(key, "AssociateFiles"),
            ShellMenus = Flag(key, "ShellMenus"),
            DesktopShortcut = Flag(key, "DesktopShortcut"),
            IncludeSamples = Flag(key, "IncludeSamples"),
            PortableRuntime = Flag(key, "PortableRuntime"),
            PortableRuntimeRoot = key.GetValue("PortableRuntimeRoot") as string,
        };
    }

    public static void Delete()
    {
        using var root = Registry.CurrentUser.OpenSubKey(@"Software", true);
        root?.DeleteSubKeyTree("PRT Reader", false);
    }

    private static bool Flag(RegistryKey key, string name)
    {
        return (key.GetValue(name) as int? ?? 0) != 0;
    }
}
