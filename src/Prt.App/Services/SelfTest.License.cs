using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shell;
using Perisc.Safety;
using Prt.App.Models;
using Prt.App.Rendering;
using Prt.App.Views;
using Prt.Core;
using Prt.Core.Diagnostics;
using Prt.Core.Export;
using Prt.Core.Rendering;
using Prt.Core.Syntax;

namespace Prt.App.Services;

/// <summary>自检用例组：授权存储 / 激活与关于窗口 / 时钟与到期判定（<see cref="SelfTest"/> 的 partial）。</summary>
internal static partial class SelfTest
{
    /// <summary>自检用例组：授权存储 / 激活与关于窗口 / 时钟与到期判定。</summary>
    private static void RunLicenseCases(CaseRunner runner)
    {
        // 30. 授权存储位置：纯便携。回归点——历史上授权在 %APPDATA% 有回退副本，
        //     于是本机任一副本激活就会把其余副本一并点亮；这里把「只认程序同级」钉死。
        runner.Check("授权存储为纯便携（不读写 %APPDATA%）", () => LicenseCase30());

        // 31. 激活窗口版面：正文可滚动、高度封顶、操作区固定。
        //     回归点——旧版窗口内容一长就把输入框与按钮挤出屏幕，且没有任何滚动途径。
        runner.Check("激活窗口：正文可滚动、操作区固定且高度封顶", () => LicenseCase31());

        // 32. 关于窗口：独立窗口取代原来的 MessageBox，且三个动作确实透传给调用方。
        runner.Check("关于窗口可实例化且动作可注入", () => LicenseCase32());

        // 33. 时钟：6.1.3「时钟回拨不得延长许可」的底座。
        //     回归点——改造前各层各读各的系统时间，回拨保护只覆盖安全模块内部，
        //     激活到期判定完全在保护之外：把系统时间往回调就能给过期授权续命。
        runner.Check("时钟回拨不延长许可（6.1.3）", () => LicenseCase33());

        // 34. 授权到期判定：固定时钟下可复现，且回拨不能给过期授权续命。
        //     回归点——IsExpired 原先直接比 DateTime.Today：边界不可复现，且不受回拨保护。
        runner.Check("授权到期判定：固定时钟可复现且回拨不续命", () => LicenseCase34());
    }

    /// <summary>用例 30：授权存储为纯便携（不读写 %APPDATA%）</summary>
    private static string LicenseCase30()
    {
            var baseDir = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
            var licensePath = Path.GetFullPath(Activation.StoragePath);
            var counterPath = Path.GetFullPath(DeviceActivation.StoragePath);

            foreach (var path in new[] { licensePath, counterPath })
            {
                if (!string.Equals(Path.GetDirectoryName(path), baseDir, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"授权相关文件不在程序同级目录：{path}");
                }
            }

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                var appDataRoot = Path.GetFullPath(appData);
                foreach (var path in new[] { licensePath, counterPath })
                {
                    if (path.StartsWith(appDataRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("授权存储仍指向 %APPDATA%（同机副本会互相串扰）：" + path);
                    }
                }
            }

            return $"授权 {Path.GetFileName(licensePath)}、计数 {Path.GetFileName(counterPath)} 均在程序目录";
    }

    /// <summary>用例 31：激活窗口：正文可滚动、操作区固定且高度封顶</summary>
    private static string LicenseCase31()
    {
            var window = new ActivationWindow();
            var workArea = SystemParameters.WorkArea.Height;
            if (window.MaxHeight <= 0 || window.MaxHeight > workArea)
            {
                throw new InvalidOperationException(
                    $"窗口高度上界未钳到屏幕工作区：MaxHeight={window.MaxHeight:0}，工作区={workArea:0}");
            }

            if (window.CodeBox.VerticalScrollBarVisibility != ScrollBarVisibility.Auto)
            {
                throw new InvalidOperationException("激活码输入框不会随内容增高出现滚动条");
            }

            if (window.Content is not FrameworkElement frame)
            {
                throw new InvalidOperationException("激活窗口没有可视内容");
            }

            // 造一段远超窗口高度的内容，验证「内容长」时确实产生可滚动范围而不是被裁掉。
            // 注意顺序：ScrollViewer 的模板（含承载正文的 ScrollContentPresenter）在首次布局时才实例化，
            // 在此之前正文尚未进入 ScrollViewer 的可视化子树，向上找不到该祖先。
            window.StatusText.Text = new string('测', 600) + new string('\n', 40);
            frame.Measure(new Size(window.Width, window.MaxHeight));
            frame.Arrange(new Rect(0, 0, window.Width, window.MaxHeight));
            frame.UpdateLayout();

            var scroller = FindVisualAncestor<ScrollViewer>(window.StatusText);
            if (scroller is null || scroller.VerticalScrollBarVisibility != ScrollBarVisibility.Auto)
            {
                throw new InvalidOperationException("授权摘要不在可滚动容器内，或滚动条未启用");
            }

            if (scroller.ScrollableHeight <= 0)
            {
                throw new InvalidOperationException(
                    $"超长内容未产生滚动范围（视口 {scroller.ViewportHeight:0}，内容 {scroller.ExtentHeight:0}）");
            }

            // 结果提示与按钮必须在滚动区之外：失败原因任何时候都要看得见。
            foreach (var control in new FrameworkElement[] { window.ActivateButton, window.MessageText })
            {
                if (FindVisualAncestor<ScrollViewer>(control) is not null)
                {
                    throw new InvalidOperationException("操作区落在滚动区内——信息过长时会被滚出可视区");
                }
            }

            return $"高度上界 {window.MaxHeight:0}（工作区 {workArea:0}），超长内容可滚动 {scroller.ScrollableHeight:0} 像素";
    }

    /// <summary>用例 32：关于窗口可实例化且动作可注入</summary>
    private static string LicenseCase32()
    {
            var window = new AboutWindow();
            var invoked = new List<string>();
            window.ShowActivationRequested = () => invoked.Add("activation");
            window.ShowComplianceRequested = () => invoked.Add("compliance");
            window.ShowShortcutsRequested = () => invoked.Add("shortcuts");

            foreach (var text in new[] { window.VersionText.Text, window.SpecText.Text, window.LicenseText.Text })
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new InvalidOperationException("关于窗口存在空文本（版本 / 符合性 / 授权），可能是词条未填充");
                }
            }

            window.ComplianceButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.ShortcutsButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.ActivationButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            if (invoked.Count != 3)
            {
                throw new InvalidOperationException(
                    $"三个按钮的落地动作未全部透传：实际触发 {invoked.Count} 次（{string.Join(", ", invoked)}）");
            }

            return $"版本 {window.VersionText.Text}；符合性 / 快捷键 / 激活与授权三个动作均已透传";
    }

    /// <summary>用例 33：时钟回拨不延长许可（6.1.3）</summary>
    private static string LicenseCase33()
    {
            var manual = new ManualClock(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.FromHours(8)));
            var clock = new MonotonicClock(() => manual.Now());

            var before = clock.Now();
            manual.Advance(TimeSpan.FromHours(2));
            var forward = clock.Now();
            if (forward - before < TimeSpan.FromHours(2))
            {
                throw new InvalidOperationException($"时钟未随取时源前进：{before:HH:mm} → {forward:HH:mm}");
            }

            // 系统时间被往回调 3 小时：单调时钟必须停在已观察到的最大值上，不得倒退。
            manual.Advance(TimeSpan.FromHours(-3));
            var afterRollback = clock.Now();
            if (afterRollback != forward)
            {
                throw new InvalidOperationException(
                    $"回拨后时刻发生倒退：{forward:yyyy-MM-dd HH:mm} → {afterRollback:yyyy-MM-dd HH:mm}");
            }

            return $"前进 2 h → {forward:HH:mm}；回拨 3 h 后仍为 {afterRollback:HH:mm}（未倒退）";
    }

    /// <summary>用例 34：授权到期判定：固定时钟可复现且回拨不续命</summary>
    private static string LicenseCase34()
    {
            if (AppClock.Source is not MonotonicClock)
            {
                throw new InvalidOperationException(
                    "宿主默认取时源不是单调时钟——6.1.3 的回拨保护在激活路径上不成立");
            }

            var manual = new ManualClock(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.FromHours(8)));
            var original = AppClock.Source;
            try
            {
                AppClock.Source = new MonotonicClock(() => manual.Now());
                var today = AppClock.Today;

                if (License("today", today).IsExpired)
                {
                    throw new InvalidOperationException("到期日=今天 被判为已过期（边界应为当天仍有效）");
                }
                if (!License("yesterday", today.AddDays(-1)).IsExpired)
                {
                    throw new InvalidOperationException("到期日=昨天 未被判为过期");
                }
                if (License("tomorrow", today.AddDays(1)).IsExpired)
                {
                    throw new InvalidOperationException("到期日=明天 被判为已过期");
                }

                var perpetual = License("perpetual", null);
                if (!perpetual.IsPerpetual || perpetual.IsExpired)
                {
                    throw new InvalidOperationException("永久授权的判定不正确");
                }

                // 回拨 30 天：已过期的授权不得复活。
                manual.Advance(TimeSpan.FromDays(-30));
                if (!License("yesterday", today.AddDays(-1)).IsExpired)
                {
                    throw new InvalidOperationException(
                        "系统时间回拨 30 天后，已过期的授权被判为未过期——6.1.3 的回拨保护失效");
                }

                return $"基准日 {today:yyyy-MM-dd}：今天 / 明天有效、昨天过期；回拨 30 天后仍判过期";
            }
            finally
            {
                AppClock.Source = original;
            }
    }
}
