using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace Prt.Uninstaller.Platform;

/// <summary>
/// 「Windows 设置 → 应用」（旧称添加/删除程序，ARP）登记。
///
/// 这个列表**不由安装器自己决定显示与否**，它是系统按约定去扫描下面这棵子树生成的：
///   HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\&lt;子键&gt;
/// 里只要有 DisplayName 与 UninstallString，该程序就会出现在「应用」页；两者缺一，
/// 条目要么不出现，要么点了卸载直接报错。所以这不是"锦上添花的元数据"，而是**必须写**。
///
/// 按用户安装（HKCU）即可，不需要管理员；系统会把 HKCU 与 HKLM 两处的条目合并展示。
/// 字段依据微软 ARP 约定，缺哪个不会报错，缺关键的（DisplayName / UninstallString /
/// Publisher / DisplayVersion / EstimatedSize）会让列表项显示不全或被判定为"脏条目"。
/// </summary>
public static class InstallEntry
{
    private const string ArpRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>
    /// 固定子键名。若每次安装生成一个新 GUID，覆盖安装就会留下旧条目；
    /// 而 GUID 写死又怕将来想改名时清不掉——故写入前按 DisplayName 全量兜底清一遍，
    /// 「写固定键 + 按名清旧」两道保险合起来才既幂等又不留残骸。
    /// </summary>
    private const string AppId = "4C0D3A71-6E1F-4B6C-8E4D-2F7A9B5C1E83";

    private const string DisplayName = "PRT 阅读器";
    private const string Publisher = "Perisc 俱乐部";

    /// <summary>
    /// 写入「应用」列表条目。便携运行时装在别的盘时不计入已装体积，故单独排除。
    /// <paramref name="installDate"/> 由安装侧取好（yyyyMMdd）：本类型所在的卸载器工程引用的是
    /// 安装器已构建的 DLL，在这里另取一次系统时间只会多一处不受门禁保护的取时点。
    /// </summary>
    public static bool Register(string installDir, string version, string? portableRoot, string installDate)
    {
        try
        {
            ClearByDisplayName();

            var uninstaller = "\"" + Path.Combine(installDir, "uninstall.exe") + "\" /quiet";
            var icon = "\"" + Path.Combine(installDir, FileAssociation.LauncherExeName) + "\",0";

            using var key = Registry.CurrentUser.CreateSubKey(ArpRoot + "\\" + AppId, true);
            key.SetValue("DisplayName", DisplayName, RegistryValueKind.String);
            key.SetValue("UninstallString", uninstaller, RegistryValueKind.String);
            key.SetValue("UninstallDisplayName", "卸载 " + DisplayName, RegistryValueKind.String);
            key.SetValue("DisplayVersion", version, RegistryValueKind.String);
            key.SetValue("Publisher", Publisher, RegistryValueKind.String);
            key.SetValue("InstallLocation", installDir, RegistryValueKind.String);
            key.SetValue("DisplayIcon", icon, RegistryValueKind.String);
            key.SetValue("InstallDate", installDate, RegistryValueKind.String);
            key.SetValue("EstimatedSize", (uint)MeasureKilobytes(installDir, portableRoot), RegistryValueKind.DWord);
            key.SetValue("InstallationType", "User", RegistryValueKind.String);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("Comments", "按用户安装（免管理员）。卸载请运行安装目录下的 uninstall.exe。", RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or ObjectDisposedException
                                      or System.Security.SecurityException)
        {
            // 注册表读不到（沙箱、受限策略）不该让安装失败，只让这条登记缺席。
            return false;
        }
    }

    /// <summary>卸载：按显示名清掉全部同名单条目，返回是否真删到了东西。</summary>
    public static bool Unregister()
    {
        return ClearByDisplayName();
    }

    /// <summary>
    /// 清掉所有 DisplayName 命中本程序的条目。按名不按键名，是为了兼容历史上用过的其它子键名。
    /// </summary>
    private static bool ClearByDisplayName()
    {
        try
        {
            using var arp = Registry.CurrentUser.OpenSubKey(ArpRoot, true);
            if (arp is null)
            {
                return false;
            }

            var removed = false;
            foreach (var name in arp.GetSubKeyNames())
            {
                // 短语句拿到句柄即刻释放，再删整棵子树，避免"自己开着自己"的冲突。
                var displayName = arp.OpenSubKey(name)?.GetValue("DisplayName") as string;
                if (displayName == DisplayName)
                {
                    arp.DeleteSubKeyTree(name, false);
                    removed = true;
                }
            }

            return removed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// 安装目录体积（KB）。跳过便携运行时目录：它多半装在别的盘，
    /// 算进"已装体积"会让用户看到一行与实际占用不符的数字。
    /// </summary>
    private static long MeasureKilobytes(string installDir, string? portableRoot)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories))
            {
                if (portableRoot is not null &&
                    file.StartsWith(portableRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                total += new FileInfo(file).Length;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        // 向上取整，且至少 1：不足 1 MB 的安装目录整除后会是 0，
        // 而 0 在「应用」列表里会显示成一片空白，看起来像没写这个字段。
        var kilobytes = (total + 1023) / 1024;
        return kilobytes > 0 ? kilobytes : 1;
    }
}
