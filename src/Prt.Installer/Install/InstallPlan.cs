using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using Prt.Uninstaller.Platform;

namespace Prt.Installer.Install;

/// <summary>
/// 安装执行体。整条链路只写 HKCU / %LOCALAPPDATA%，不弹 UAC、不改系统目录。
/// 每一步都可在中途失败并抛出，调用方负责把"装到一半"回滚或留痕（见 InstallPlan.RunAsync）。
/// </summary>
public static class InstallPlan
{
    public static async Task<InstallOutcome> RunAsync(
        InstallOptions options,
        Action<string> log,
        Action<string> phase,
        CancellationToken cancellationToken)
    {
        await Task.Yield();

        var installDir = options.InstallDirectory;
        Directory.CreateDirectory(installDir);
        phase("准备安装目录");
        log("安装位置：" + installDir);
        if (HasExistingInstallation(installDir))
        {
            log("该位置已有安装，将覆盖同名文件。");
        }

        // 1) 阅读器本体（samples / 帮助 / 安全行为清单等子目录一并释放）
        phase("释放阅读器文件");
        var readerEntries = EmbeddedFiles.List(EmbeddedFiles.ReaderPrefix);
        foreach (var entry in readerEntries)
        {
            EmbeddedFiles.ExtractOne(EmbeddedFiles.ReaderPrefix, entry, installDir);
            cancellationToken.ThrowIfCancellationRequested();
        }

        log("已释放 " + readerEntries.Count + " 个阅读器文件");

        // 2) 卸载器：随安装落到安装目录，卸载时它负责自删
        var uninstallerPath = Path.Combine(installDir, "uninstall.exe");
        phase("释放卸载器");
        EmbeddedFiles.ExtractOne(EmbeddedFiles.UninstallerPrefix, "uninstall.exe", installDir);
        log("已放置卸载器 uninstall.exe");

        // 3) 运行时：能用系统的就不带，省 160 MB，也避免 DOTNET_ROOT 的全局副作用
        var wantPortable = DecideRuntimeMode(options) == RuntimeMode.AlwaysPortable
                           || (DecideRuntimeMode(options) == RuntimeMode.Automatic
                               && !RuntimeLocator.HasSufficientRuntime());

        var outcome = new InstallOutcome
        {
            InstallDirectory = installDir,
            LauncherPath = Path.Combine(installDir, FileAssociation.LauncherExeName),
            Version = ProductVersion.Current,
            InstalledAt = InstallClock.Now().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        };

        if (wantPortable)
        {
            var portableRoot = Path.Combine(installDir, "dotnet");
            phase("释放 .NET 运行时");
            ExtractPortableRuntime(installDir, portableRoot, log);
            RuntimeLocator.BindPortableRoot(portableRoot);
            outcome = outcome with
            {
                PortableRuntime = true,
                PortableRuntimeRoot = portableRoot,
                NeedsShellRefresh = true,
            };
            log("已安装便携运行时，并写入用户级 DOTNET_ROOT → " + portableRoot);
            log("（该环境变量要刷新资源管理器 / 注销重登后，双击文件才会用它）");
        }
        else
        {
            log("检测到系统已有 .NET 运行时，直接复用（安装包更小、无副作用）");
        }

        // 4) 文件关联与右键菜单
        if (options.AssociateFiles)
        {
            phase("配置文件关联");
            FileAssociation.Register(installDir, options.ShellMenus);
            log("已关联 .prt / .psr / .mdp" + (options.ShellMenus ? "，并加入右键菜单与「打开方式」" : string.Empty));
        }

        // 5) 快捷方式
        phase("创建开始菜单与桌面快捷方式");
        var startMenuDir = FileAssociation.StartMenuDirectory(options.ProductName);
        Directory.CreateDirectory(startMenuDir);
        ShortcutFactory.Create(
            Path.Combine(startMenuDir, "PRT 阅读器.lnk"),
            outcome.LauncherPath,
            workingDirectory: installDir,
            description: "PRT 阅读器（PRT / PSR / MDP）",
            iconPath: outcome.LauncherPath);
        ShortcutFactory.Create(
            Path.Combine(startMenuDir, "卸载 PRT 阅读器.lnk"),
            uninstallerPath,
            description: "卸载 PRT 阅读器",
            iconPath: outcome.LauncherPath);
        log("开始菜单项已创建");

        if (options.DesktopShortcut)
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            ShortcutFactory.Create(
                Path.Combine(desktop, "PRT 阅读器.lnk"),
                outcome.LauncherPath,
                workingDirectory: installDir,
                description: "PRT 阅读器（PRT / PSR / MDP）",
                iconPath: outcome.LauncherPath);
            log("桌面快捷方式已创建");
        }

        // 6) 「设置 → 应用」登记：系统只认 HKCU\...\Uninstall 下那棵子树，不写就永远不出现。
        //    日期从已落定的 InstalledAt 派生，不在这一处新开系统时间。
        phase("登记应用列表");
        var installDate = outcome.InstalledAt.Substring(0, 10).Replace("-", string.Empty, StringComparison.Ordinal);
        if (InstallEntry.Register(installDir, outcome.Version, outcome.PortableRuntimeRoot, installDate))
        {
            log("已登记到「设置 → 应用」（卸载走 uninstall.exe）");
        }
        else
        {
            log("应用列表登记未写入，不影响使用与卸载");
        }

        Persist(options, outcome);

        phase("安装完成");
        return outcome;
    }

    /// <summary>
    /// 把内嵌的 runtime.zip 解到 &lt;安装目录&gt;\dotnet，随后删掉压缩包。
    /// 覆盖安装时旧目录还在，故先整棵删掉再解——留着旧版本的多余文件会让 apphost
    /// 在多个运行时版本之间挑错。
    /// </summary>
    private static void ExtractPortableRuntime(string installDir, string portableRoot, Action<string> log)
    {
        var archivePath = Path.Combine(installDir, EmbeddedFiles.RuntimeArchiveName);
        EmbeddedFiles.ExtractOne(EmbeddedFiles.RuntimeArchive, EmbeddedFiles.RuntimeArchiveName, installDir);

        if (Directory.Exists(portableRoot))
        {
            Directory.Delete(portableRoot, true);
        }

        ZipFile.ExtractToDirectory(archivePath, portableRoot);
        File.Delete(archivePath);
        log("便携运行时已释放到 " + portableRoot);
    }

    /// <summary>
    /// 落盘安装登记（HKCU\Software\PRT Reader）。放在这里而不是 InstallState——
    ///  uninstall.exe 只需要登记的**读**侧，写入要用到 InstallOptions / InstallOutcome，
    /// 链到卸载器里就成了无谓的依赖。
    /// </summary>
    private static void Persist(InstallOptions options, InstallOutcome outcome)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
            @"Software\PRT Reader", true);
        key.SetValue("InstallDir", outcome.InstallDirectory, Microsoft.Win32.RegistryValueKind.String);
        key.SetValue("Version", outcome.Version, Microsoft.Win32.RegistryValueKind.String);
        key.SetValue("InstalledAt", outcome.InstalledAt, Microsoft.Win32.RegistryValueKind.String);
        key.SetValue("AssociateFiles", options.AssociateFiles ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue("ShellMenus", options.ShellMenus ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue("DesktopShortcut", options.DesktopShortcut ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue("IncludeSamples", options.IncludeSamples ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue("PortableRuntime", outcome.PortableRuntime ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue("PortableRuntimeRoot", outcome.PortableRuntimeRoot, Microsoft.Win32.RegistryValueKind.String);
    }

    private static RuntimeMode DecideRuntimeMode(InstallOptions options)
    {
        return options.Runtime;
    }

    private static bool HasExistingInstallation(string installDir)
    {
        return File.Exists(Path.Combine(installDir, FileAssociation.LauncherExeName))
               || InstallState.Read() is not null;
    }
}
