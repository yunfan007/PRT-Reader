using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Prt.Uninstaller.Platform;

/// <summary>
/// 文件关联 / 「打开方式」 / 资源管理器右键菜单 —— 全部写在 HKCU 之下，按用户安装，免管理员。
///
/// 注册表布局（HKCU\Software\Classes 简写为 Classes）：
///   Classes\&lt;扩展名&gt;                                → 指向统一 ProgID
///   Classes\PRTReader.Document                      → 文档类型名 + 图标 + open 动词
///   Classes\Applications\&lt;启动器 exe&gt;                → 「打开方式」列表项
///   Classes\&lt;扩展名&gt;\shell\PRTReader.Open           → 右键菜单条目
///   Microsoft\Windows\CurrentVersion\Explorer\FileExts\&lt;扩展名&gt;\OpenWithList\&lt;启动器 exe&gt;
///        → 让「打开方式」在该扩展下出现本程序（只写 Applications 是不够的）
///
/// 卸载时按同一份清单反向删除；安装前先探测是否"别人已经把它占了自己"，避免覆盖别人后拿不回来。
/// </summary>
internal static class FileAssociation
{
    /// <summary>PRT 阅读器的启动器文件名，同时作为「打开方式」里的程序名。</summary>
    public const string LauncherExeName = "Perisc.Safety.SafeGuard.exe";

    /// <summary>统一 ProgID：三类扩展共用，避免同一批文件被登记成三个互不相干的"类型"。</summary>
    private const string ProgId = "PRTReader.Document";

    private static readonly string[] Extensions = { ".prt", ".psr", ".mdp" };

    private const string ClassesRoot = @"Software\Classes";
    private const string FileExtsRoot = @"Microsoft\Windows\CurrentVersion\Explorer\FileExts";

    /// <summary>安装位置：把三类扩展都关联到阅读器。</summary>
    public static void Register(string installDir, bool shellMenus)
    {
        var launcher = Path.Combine(installDir, LauncherExeName);
        // SafeGuard 把 -- 之后的参数透传给主程序（Prt.App.dll），因此文件路径必须放在 -- 后面。
        var command = "\"" + launcher + "\" -- \"%1\"";

        using var classes = Registry.CurrentUser.OpenSubKey(ClassesRoot, true) ??
                            Registry.CurrentUser.CreateSubKey(ClassesRoot, true);

        foreach (var extension in Extensions)
        {
            // 扩展 → ProgID。
            using (var ext = classes.CreateSubKey(extension))
            {
                ext.SetValue(string.Empty, ProgId);
            }

            if (!shellMenus)
            {
                continue;
            }

            // 右键菜单：仅在这三类文件上出现，不污染其他类型的右键菜单。
            using (var verb = classes.CreateSubKey(extension + @"\shell\PRTReader.Open"))
            {
                verb.SetValue(string.Empty, "用 PRT 阅读器打开");
            }

            using (var verbCommand = classes.CreateSubKey(extension + @"\shell\PRTReader.Open\command"))
            {
                verbCommand.SetValue(string.Empty, command);
            }

            // 「打开方式」列表：把本程序挂进该扩展的 OpenWithList。
            using (var openWith = Registry.CurrentUser.CreateSubKey(
                       FileExtsRoot + extension + @"\OpenWithList\" + LauncherExeName))
            {
                openWith.SetValue(string.Empty, string.Empty);
            }
        }

        // ProgID 本体：类型名、图标、默认动词。
        using (var document = classes.CreateSubKey(ProgId))
        {
            document.SetValue(string.Empty, "PRT 文档（PRT / PSR / MDP）");
            document.SetValue("FriendlyTypeName", "PRT 文档");
        }

        using (var icon = classes.CreateSubKey(ProgId + @"\DefaultIcon"))
        {
            icon.SetValue(string.Empty, "\"" + launcher + "\",0");
        }

        using (var open = classes.CreateSubKey(ProgId + @"\shell\open\command"))
        {
            open.SetValue(string.Empty, command);
        }

        // 「打开方式」里的程序项（名与启动器文件名一致，上面 OpenWithList 已按名挂钩）。
        using (var application = classes.CreateSubKey(@"Applications\" + LauncherExeName))
        {
            application.SetValue(string.Empty, "PRT 阅读器");
        }

        using (var applicationIcon = classes.CreateSubKey(@"Applications\" + LauncherExeName + @"\DefaultIcon"))
        {
            applicationIcon.SetValue(string.Empty, "\"" + launcher + "\",0");
        }

        using (var applicationOpen = classes.CreateSubKey(
                   @"Applications\" + LauncherExeName + @"\shell\open\command"))
        {
            applicationOpen.SetValue(string.Empty, command);
        }

        // 让资源管理器立刻刷新「打开方式」列表，不必等下一次explorer重启。
        SHChangeNotify();
    }

    /// <summary>卸载：按同一份清单反向清理，未注册过的键删了也无害。</summary>
    public static void Unregister()
    {
        foreach (var extension in Extensions)
        {
            TryDeleteSubTree(ClassesRoot, extension + @"\shell\PRTReader.Open");
            TryDeleteSubTree(FileExtsRoot, extension + @"\OpenWithList\" + LauncherExeName);
        }

        TryDeleteSubTree(ClassesRoot, "Applications\\" + LauncherExeName);
        TryDeleteSubTree(ClassesRoot, ProgId);
        SHChangeNotify();
    }

    /// <summary>是否已有其他程序把某个扩展占为己有（用于安装前提示，不阻止安装）。</summary>
    public static bool IsProgIdTakenByOther(string extension)
    {
        using var classes = Registry.CurrentUser.OpenSubKey(ClassesRoot);
        using var ext = classes?.OpenSubKey(extension);
        var value = ext?.GetValue(string.Empty) as string;
        return !string.IsNullOrEmpty(value) &&
               !string.Equals(value, ProgId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>开始菜单程序组路径（卸载器与安装器共用同一处，避免两处口径漂移）。</summary>
    public static string StartMenuDirectory(string programName)
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs", programName);
    }

    private static void TryDeleteSubTree(string root, string subKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(root, true);
        key?.DeleteSubKeyTree(subKey, false);
    }

    /// <summary>通知外壳程序刷新图标与关联缓存。</summary>
    private static void SHChangeNotify()
    {
        NativeMethods.SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero);
    }

    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_FLUSH = 0x0004;

    private static class NativeMethods
    {
        // CA5392：不标注搜索路径的 P/Invoke 会被当成潜在的 dll 侧载面。
        // shell32.dll 是系统组件，限定到系统目录（外加程序集目录以防单文件发布后
        // 解压目录恰好落在非 system 路径）既满足规则也不牺牲可移植性。
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories | DllImportSearchPath.System32)]
        internal static extern void SHChangeNotify(uint wEvent, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
    }
}
