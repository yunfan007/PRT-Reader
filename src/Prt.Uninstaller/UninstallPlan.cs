using System.IO;
using Prt.Installer.Install;
using Prt.Uninstaller.Platform;

namespace Prt.Uninstaller;

/// <summary>
/// 卸载执行体：与安装严格镜像的逆序清理。
/// 每一步都可能失败（文件被占用、注册表被别的应用改过），因此**逐步记录、不因一步失败中止**，
/// 最后统一汇报——"装的时候一步到位，卸的时候留得下全尸"比"失败就什么都不做"更可靠。
/// </summary>
public sealed class UninstallPlan
{
    private readonly string _installDirectory;
    private readonly InstallStateSnapshot _state;
    private readonly Action<string> _log;

    public UninstallPlan(InstallStateSnapshot state, Action<string> log)
    {
        _state = state;
        _installDirectory = state.InstallDirectory;
        _log = log;
    }

    public UninstallOutcome Run()
    {
        var outcome = new UninstallOutcome { InstallDirectory = _installDirectory };

        outcome.ClosedRunningInstances = StopRunningInstances();
        RemoveShortcuts();
        RemoveAssociations();
        outcome.RuntimeRolledBack = RollbackRuntime();
        RemoveStartMenuEntries();
        RemoveArpEntry();
        outcome.DirectoryRemoved = RemoveDirectory();
        InstallState.Delete();

        return outcome;
    }

    /// <summary>结束仍在运行的阅读器实例，否则安装目录删不干净。</summary>
    private bool StopRunningInstances()
    {
        var killed = 0;
        foreach (var name in new[] { "Perisc.Safety.SafeGuard", "Prt.App" })
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
            {
                try
                {
                    process.Kill();
                    killed++;
                    _log("已结束运行中的实例：" + name);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    _log("无法结束进程 " + name + "：" + ex.Message);
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return killed > 0;
    }

    private void RemoveShortcuts()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        ShortcutFactory.EnsureDeleted(Path.Combine(desktop, "PRT 阅读器.lnk"));
        _log("已删除桌面快捷方式");

        var startMenu = FileAssociation.StartMenuDirectory("PRT 阅读器");
        foreach (var shortcut in Directory.Exists(startMenu)
                     ? Directory.EnumerateFiles(startMenu, "*.lnk")
                     : Array.Empty<string>())
        {
            // 只删本程序的两个条目：同目录里可能还躺着用户自己放的东西。
            var name = Path.GetFileName(shortcut);
            if (name is "PRT 阅读器.lnk" or "卸载 PRT 阅读器.lnk")
            {
                ShortcutFactory.EnsureDeleted(shortcut);
            }
        }

        _log("已删除开始菜单项");
    }

    private void RemoveAssociations()
    {
        FileAssociation.Unregister();
        _log("已清除文件关联与右键菜单项");
    }

    /// <summary>
    /// 清除「设置 → 应用」里的本程序条目。必须早于删除安装目录：
    /// 目录都没了还留着条目，列表项就会变成点不动的残骸。
    /// </summary>
    private void RemoveArpEntry()
    {
        _log(InstallEntry.Unregister()
            ? "已清除「设置 → 应用」中的本程序条目"
            : "「设置 → 应用」中没有本程序条目（无需清除）");
    }

    /// <summary>回滚 DOTNET_ROOT：只删"确实指向我们便携运行时"的那一个。</summary>
    private bool RollbackRuntime()
    {
        if (!_state.PortableRuntime)
        {
            return false;
        }

        var root = _state.PortableRuntimeRoot ?? Path.Combine(_installDirectory, "dotnet");
        var removed = RuntimeLocator.TryUnbindPortableRoot(root);
        _log(removed ? "已回滚用户级 DOTNET_ROOT" : "DOTNET_ROOT 已被改动，保留原值");
        return removed;
    }

    private static void RemoveStartMenuEntries()
    {
        var startMenu = FileAssociation.StartMenuDirectory("PRT 阅读器");
        if (Directory.Exists(startMenu))
        {
            Directory.Delete(startMenu, false);
        }
    }

    private bool RemoveDirectory()
    {
        if (!Directory.Exists(_installDirectory))
        {
            return true;
        }

        try
        {
            Directory.Delete(_installDirectory, true);
            _log("已删除安装目录：" + _installDirectory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log("安装目录未能完全删除（仍有文件被占用）：" + _installDirectory);
            return false;
        }
    }
}

public sealed class UninstallOutcome
{
    public string InstallDirectory { get; init; } = string.Empty;

    // 这三个是"执行体边走边填的进度"，不是构造时定死的初值——用普通 set，
    // 硬套 init 只会让调用方不得不用局部中转变量绕一圈。
    public bool ClosedRunningInstances { get; set; }
    public bool RuntimeRolledBack { get; set; }
    public bool DirectoryRemoved { get; set; }
}
