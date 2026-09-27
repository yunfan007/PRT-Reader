using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Prt.Installer.Install;
using Prt.Uninstaller.Platform;

namespace Prt.Installer.Pages;

/// <summary>安装选项页：安装位置 + 组件关联勾选 + 运行时策略，并给出体积预估。</summary>
public partial class OptionsPage : UserControl
{
    private InstallOutcome? _lastOutcome;

    public OptionsPage()
    {
        InitializeComponent();
    }

    /// <summary>安装成功后回填到完成页（完成页自己不再重算一遍）。</summary>
    public InstallOutcome? LastOutcome
    {
        get => _lastOutcome;
        set
        {
            _lastOutcome = value;
            if (value is not null)
            {
                LastInstallDirectory = value.InstallDirectory;
            }
        }
    }

    private string? LastInstallDirectory { get; set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (LocationBox.Text.Length == 0)
        {
            LocationBox.Text = LastInstallDirectory ?? InstallOptions.DefaultInstallDirectory();
        }

        UpdateHints();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        // Microsoft.Win32.OpenFolderDialog 的成员是 DefaultDirectory（初始位置）+ FolderName
        // （选中的结果，Multiselect=false 时就是完整路径）。没有 SelectedPath / FileName 可用。
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择安装位置",
            DefaultDirectory = LocationBox.Text,
        };

        // Page 不是 Window，只能把宿主窗口交给对话框；宿主取不到时退回无属主模态。
        var owner = Window.GetWindow(this);
        var accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);

        if (accepted == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            LocationBox.Text = dialog.FolderName;
        }

        UpdateHints();
    }

    private void UpdateHints()
    {
        var location = LocationBox.Text.Trim();
        LocationHint.Text = string.IsNullOrWhiteSpace(location)
            ? string.Empty
            : "安装目录：" + location + Path.DirectorySeparatorChar;

        var mode = ReadRuntimeMode();
        RuntimeHint.Text = mode switch
        {
            RuntimeMode.AlwaysPortable =>
                "将携带一份便携 .NET 运行时（约 160 MB）到安装目录，并写入用户级 DOTNET_ROOT。",
            RuntimeMode.SystemOnly =>
                "不会携带运行时；若系统缺少 .NET 8 桌面运行时，双击文件将提示「未安装 .NET」。",
            _ => RuntimeLocator.HasSufficientRuntime()
                ? "已检测到系统可用的 .NET 桌面运行时，本次不会携带运行时。"
                : "未检测到系统可用的 .NET 桌面运行时，将自动携带一份便携运行时。",
        };

        SizeHint.Text = "预计占用磁盘：" + DescribeSize(mode);
    }

    private RuntimeMode ReadRuntimeMode()
    {
        if (RuntimePortable.IsChecked == true)
        {
            return RuntimeMode.AlwaysPortable;
        }

        return RuntimeSystem.IsChecked == true ? RuntimeMode.SystemOnly : RuntimeMode.Automatic;
    }

    /// <summary>把界面取值打包给安装执行体。</summary>
    public InstallOptions CollectOptions()
    {
        return new InstallOptions
        {
            InstallDirectory = Environment.ExpandEnvironmentVariables(LocationBox.Text.Trim().TrimEnd('\\', '/')),
            AssociateFiles = AssociateBox.IsChecked == true,
            ShellMenus = ShellMenuBox.IsChecked == true,
            DesktopShortcut = DesktopBox.IsChecked == true,
            IncludeSamples = SamplesBox.IsChecked == true,
            Runtime = ReadRuntimeMode(),
        };
    }

    /// <summary>安装位置是否已存在同名文件（说明是覆盖安装）。</summary>
    public bool IsLocationWritable(string location)
    {
        try
        {
            // 只看能否创建/写入：目录本身已存在时试探性建一个子键，随后删掉。
            Directory.CreateDirectory(location);
            var probePath = Path.Combine(location, ".prt-installer-probe");
            File.WriteAllText(probePath, string.Empty);
            File.Delete(probePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>体积预估：只有真的会释放便携运行时时才把它算进去。</summary>
    private static string DescribeSize(RuntimeMode mode)
    {
        var megabytes = 2.0;                               // 阅读器 + 卸载器 + 文档
        if (mode != RuntimeMode.SystemOnly && (mode == RuntimeMode.AlwaysPortable
                                               || !RuntimeLocator.HasSufficientRuntime()))
        {
            megabytes += 162;
        }

        return megabytes >= 1024
            ? (megabytes / 1024).ToString("0.##", CultureInfo.InvariantCulture) + " GB"
            : megabytes.ToString("0", CultureInfo.InvariantCulture) + " MB";
    }
}
