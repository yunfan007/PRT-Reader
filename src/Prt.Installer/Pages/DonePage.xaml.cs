using System.Windows;
using System.Windows.Controls;
using Prt.Installer.Install;

namespace Prt.Installer.Pages;

/// <summary>完成页：把这次到底装了什么讲清楚，特别是「要不要刷新外壳」这种极易被忽略的后继动作。</summary>
public partial class DonePage : UserControl
{
    public DonePage()
    {
        InitializeComponent();
    }

    public void Show(InstallOutcome outcome, InstallOptions options)
    {
        TitleText.Text = "PRT 阅读器 " + outcome.Version + " 安装完成";
        SummaryText.Text =
            "安装位置：" + outcome.InstallDirectory + "\n" +
            "文件关联：.prt / .psr / .mdp" + (options.AssociateFiles ? "（已关联）" : "（未关联）") + "\n" +
            "运行时：" + (outcome.PortableRuntime ? "已携带便携运行时" : "使用系统运行时") + "\n" +
            "卸载器：安装目录下 uninstall.exe";

        RebootBanner.Visibility = outcome.NeedsShellRefresh ? Visibility.Visible : Visibility.Collapsed;
    }
}
