using System.Globalization;
using System.IO;
using System.Linq;
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
using TocEntry = Prt.Core.Structure.TocEntry;

namespace Prt.App.Services;

/// <summary>自检用例组：主窗口外壳 / 标签生命周期 / 内置资源 / 打印（<see cref="SelfTest"/> 的 partial）。</summary>
internal static partial class SelfTest
{
    /// <summary>自检用例组：主窗口外壳 / 标签生命周期 / 内置资源 / 打印。</summary>
    private static async Task RunShellCasesAsync(CaseRunner runner)
    {
        // 14. 设置项取值收敛：手工编辑坏 settings.json 时不能让界面崩，非法值一律回退。
        runner.Check("设置项取值收敛（非法值回退、越界钳制）", () => ShellCase14());

        // 15. 词条表中英一致：漏翻会以裸键出现在界面上，这里逐键核对。
        runner.Check("界面词条中英一致", () => ShellCase15());

        // 12. 主窗口外壳：自绘标题栏配置与「插入」菜单同源，且菜单模板部件齐全。
        runner.Check("主窗口外壳与菜单模板", () => ShellCase12());

        // 13. 示例文件可关闭：从随程序分发的 samples 目录打开后，
        //     主窗口必须能找到该标签并把它从 TabControl 中移除（覆盖「示例文件打开后没法关闭」的回归）。
        //     打开文档要过安全申报（异步），所以本用例是异步的——顺带覆盖了整条异步申报链。
        await runner.CheckAsync("示例文件标签可关闭", () => ShellCase13());

        // 14. 内置教程可定位：随程序分发的《PRT 教程》与《PRT 作者指南》必须能被找见。
        //     覆盖「标准文档改名 / 移入 Standard\PRT 后路径失效、菜单静默失败」的回归
        //     （与 13 同类：随程序分发的资源一旦不随包发布，菜单就会无声失效）。
        runner.Check("内置教程与作者指南可定位", () => ShellCase14b());

        // 15. 打印分页：打印用文档能独立构建、能算出页数、装饰后仍能取出每一页。
        //     覆盖「打印预览空白 / 页眉页脚装饰把页面吃掉」这类问题。
        //     打印构建依赖解析结果，而解析已移入后台渐进管线，因此本用例须等待 ReparseAsync 完成。
        await runner.CheckAsync("打印分页与页眉页脚", () => ShellCase15b());

        // 16. 大文档渐进渲染：超过渐进阈值的文档走「后台解析 + 分批上屏」管线。
        //     覆盖「打开大文件界面卡死」的回归：等待完整管线结束后，全部块必须都已渲染、
        //     锚点表完整、进度条收起。
        await runner.CheckAsync("大文档虚拟渲染就绪且进度条收起", () => ShellCase16());

        // 17. 超大文档侧边栏大纲：数千条目录在虚拟化绑定下可构建，且按规模分级折叠。
        //     覆盖「自动目录文档打开必未响应」中侧边栏全量重建 TreeViewItem 的回归。
        runner.Check("超大文档侧边栏大纲可构建且分级折叠", () => ShellCase17());

        // 54. 就地编辑（滚动锁定式）：双击块进入编辑态 → 预览滚动锁定 → 提交写回源码（撤销栈保留）
        //     → 解锁滚动并重解析；Esc 取消不动源码。覆盖「虚拟化与就地编辑并存」的核心机制回归。
        await runner.CheckAsync("就地编辑提交写回与取消（滚动锁定随编辑态启停）", () => ShellCase54());
    }

    /// <summary>用例 14：设置项取值收敛（非法值回退、越界钳制）</summary>
    private static string ShellCase14()
    {
            var settings = new AppSettings
            {
                PreviewTheme = "bogus",
                DefaultViewMode = "wat",
                Language = "fr",
                Zoom = 99d,
                SplashImage = "   ",
            };

            settings.Normalize();

            if (settings.PreviewTheme != AppSettings.PreviewThemeFollow
                || settings.DefaultViewMode != AppSettings.ViewModeSplit
                || settings.Language != Localizer.SystemDefault
                || Math.Abs(settings.Zoom - 2d) > 0.001
                || settings.SplashImage is not null)
            {
                throw new InvalidOperationException(
                    $"收敛结果不符：主题={settings.PreviewTheme} 视图={settings.DefaultViewMode} 语言={settings.Language} 缩放={settings.Zoom} 启动图={settings.SplashImage}");
            }

            return "非法取值全部回退默认，缩放钳制到 2.0，空白启动图名清空";
    }

    /// <summary>用例 15：界面词条中英一致</summary>
    private static string ShellCase15()
    {
            var zh = Strings.Zh.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
            var en = Strings.En.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
            var missing = zh.Except(en, StringComparer.Ordinal).ToArray();
            var extra = en.Except(zh, StringComparer.Ordinal).ToArray();

            if (missing.Length > 0 || extra.Length > 0)
            {
                throw new InvalidOperationException(
                    $"英文缺 {missing.Length} 条（{string.Join(", ", missing.Take(5))}）；中文缺 {extra.Length} 条（{string.Join(", ", extra.Take(5))}）");
            }

            return $"中英各 {zh.Length} 条，键集合一致";
    }

    /// <summary>用例 12：主窗口外壳与菜单模板</summary>
    private static string ShellCase12()
    {
            var window = new MainWindow();

            var chrome = WindowChrome.GetWindowChrome(window);
            if (chrome is null)
            {
                throw new InvalidOperationException("主窗口未启用自绘标题栏（WindowChrome 缺失）");
            }

            if (Math.Abs(chrome.CaptionHeight - 44d) > 0.5)
            {
                throw new InvalidOperationException($"标题栏拖拽区高度异常：{chrome.CaptionHeight}");
            }

            var expected = PrtSnippets.InGroup(PrtSnippets.GroupSemantic).Count;
            var actual = window.InsertSemanticMenuItem.Items.Count;
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    $"「插入 → 语义框」菜单条目数与片段目录不一致：菜单 {actual} 项，目录 {expected} 项");
            }

            // 外壳必须能完成一次真实布局：否则会出现「界面全灰 / 控件零尺寸」这类只能靠肉眼发现的问题。
            var frame = window.RootFrame;
            frame.Measure(new Size(1360, 860));
            frame.Arrange(new Rect(0, 0, 1360, 860));
            frame.UpdateLayout();

            if (frame.ActualWidth < 1000 || frame.ActualHeight < 600)
            {
                throw new InvalidOperationException($"外壳布局尺寸异常：{frame.ActualWidth:0}×{frame.ActualHeight:0}");
            }

            if (window.CaptionCloseButton.ActualWidth < 40 || window.CaptionCloseButton.ActualHeight < 30)
            {
                throw new InvalidOperationException(
                    $"标题栏按钮未正确布局：{window.CaptionCloseButton.ActualWidth:0}×{window.CaptionCloseButton.ActualHeight:0}");
            }

            if (window.OutlineTree.ActualWidth <= 0 || window.MinimizeButton.ActualWidth <= 0)
            {
                throw new InvalidOperationException("侧栏或标题栏按钮尺寸为零");
            }

            // 滚动条模板必须给出 PART_Track 与 Thumb，否则界面能显示但滚动会失效。
            // 必须显式套用具名样式并把控件挂到可视树上，模板才会被实例化。
            var scrollHost = new Border { Width = 200, Height = 80 };
            var bar = new ScrollBar
            {
                Orientation = Orientation.Vertical,
                Height = 60,
                Style = (Style)Application.Current.FindResource("Prt.ScrollBar"),
            };
            scrollHost.Child = bar;
            scrollHost.Measure(new Size(200, 80));
            scrollHost.Arrange(new Rect(0, 0, 200, 80));
            scrollHost.UpdateLayout();

            if (bar.Template?.FindName("PART_Track", bar) is not Track track || track.Thumb is null)
            {
                throw new InvalidOperationException("滚动条模板缺少 PART_Track / Thumb");
            }

            // 菜单项模板必须实例化出 PART_Popup，否则子菜单无法展开。
            var probe = new MenuItem { Header = "插入框" };
            probe.Items.Add(new MenuItem { Header = "提示", InputGestureText = "note" });
            probe.Items.Add(new Separator());
            probe.Items.Add(new MenuItem { Header = "结构块" });

            var host = new Border
            {
                Width = 640,
                Height = 240,
                Child = new Menu { Items = { probe } },
            };
            host.Measure(new Size(640, 240));
            host.Arrange(new Rect(0, 0, 640, 240));
            host.UpdateLayout();

            if (probe.Template?.FindName("PART_Popup", probe) is not Popup)
            {
                throw new InvalidOperationException("菜单项模板未实例化 PART_Popup");
            }

            return $"标题栏拖拽区 {chrome.CaptionHeight:0}px，「插入 → 语义框」{expected} 项，菜单模板部件齐全";
    }

    /// <summary>用例 13：示例文件标签可关闭</summary>
    private static async Task<string> ShellCase13()
    {
            var samplePath = SampleDocument.LocateFeatureTour()
                             ?? throw new InvalidOperationException("未找到随程序分发的示例文档，无法验证关闭路径");

            var window = new MainWindow();
            // 窗口不会被 Show，Loaded 不会触发；显式补上"启动收尾"（会话恢复）这一步，
            // 否则标签列表还停在"等恢复"的状态上。
            await window.CompleteStartupForTestAsync();

            var initialTabCount = window.Tabs.Items.Count;
            if (initialTabCount < 1)
            {
                throw new InvalidOperationException("主窗口启动后应有至少一个标签（新建文档或恢复的会话文档）");
            }

            // 模拟「文件 → 打开示例文档」菜单点击。
            await window.OpenSampleForTestAsync();
            var tabCountAfterOpen = window.Tabs.Items.Count;
            if (tabCountAfterOpen != initialTabCount + 1)
            {
                throw new InvalidOperationException(
                    $"打开示例文档后标签数应为 {initialTabCount + 1}，实际 {tabCountAfterOpen}");
            }

            // 验证刚打开的标签正是示例文件。
            var sampleEntry = window.FindTabByPathForTest(samplePath)
                               ?? throw new InvalidOperationException("示例文件未注册到主窗口的标签列表");
            if (sampleEntry.View.Tab.IsDirty)
            {
                throw new InvalidOperationException("刚打开的示例文档不应是脏的（无任何修改）");
            }

            // 通过与 TabItem 头部关闭按钮相同的方式关闭：CloseTab(model)。
            // 关闭路径必须保证 _entries 同步移除、Tabs.Items 同步移除、保留至少一个标签。
            window.CloseTabForTest(sampleEntry.View.Tab);
            if (window.Tabs.Items.Count != initialTabCount)
            {
                throw new InvalidOperationException(
                    $"关闭后标签数应回到初始值 {initialTabCount}，实际 {window.Tabs.Items.Count}");
            }

            if (window.FindTabByPathForTest(samplePath) is not null)
            {
                throw new InvalidOperationException("示例文件标签未从主窗口中移除");
            }

            return $"打开 → 关闭链路通畅：标签数 {initialTabCount} → {tabCountAfterOpen} → {window.Tabs.Items.Count}";
    }

    /// <summary>用例 14：内置教程与作者指南可定位</summary>
    private static string ShellCase14b()
    {
            var tutorial = SampleDocument.LocateTutorial()
                           ?? throw new InvalidOperationException("未找到随程序分发的《PRT 教程》文件");
            var guide = SampleDocument.LocateAuthorGuide()
                        ?? throw new InvalidOperationException("未找到随程序分发的《PRT 作者指南》文件");

            var tutorialText = FileService.ReadAllText(tutorial);
            if (tutorialText.Length < 512)
            {
                throw new InvalidOperationException(
                    $"《PRT 教程》内容过短（{tutorialText.Length} 字符），疑似分发时未完整复制");
            }

            var guideText = FileService.ReadAllText(guide);
            if (guideText.Length < 512)
            {
                throw new InvalidOperationException(
                    $"《PRT 作者指南》内容过短（{guideText.Length} 字符），疑似分发时未完整复制");
            }

            return $"教程 {tutorialText.Length} 字符、作者指南 {guideText.Length} 字符，定位自 {tutorial}";
    }

    /// <summary>用例 15：打印分页与页眉页脚（解析已入后台管线，先等待 ReparseAsync 完成）</summary>
    private static async Task<string> ShellCase15b()
    {
            var samplePath = SampleDocument.LocateFeatureTour();
            var text = samplePath is null ? SampleDocument.Welcome : FileService.ReadAllText(samplePath);

            var view = new DocumentView(new DocumentTab { Text = text, UntitledIndex = 1 });
            view.ConfigureDefaults(null, null);
            await view.ReparseAsync();

            var flow = view.BuildPrintableDocument(monochrome: true)
                       ?? throw new InvalidOperationException("打印用文档未能构建");

            var inner = ((IDocumentPaginatorSource)flow).DocumentPaginator;
            var paper = new Size(793.70, 1122.52);
            inner.PageSize = paper;
            inner.ComputePageCount();

            if (inner.PageCount < 1)
            {
                throw new InvalidOperationException("分页结果为 0 页");
            }

            var decorated = new HeaderFooterPaginator(inner, "示例文档", true, true, Brushes.Black, 9d);
            decorated.PageSize = paper;
            decorated.ComputePageCount();

            var page = decorated.GetPage(0);
            if (ReferenceEquals(page, DocumentPage.Missing))
            {
                throw new InvalidOperationException("装饰后取不到第 1 页");
            }

            var preview = new PrintPreviewWindow("示例文档", _ => flow);
            preview.Rebuild();
            if (preview.PageCount < 1)
            {
                throw new InvalidOperationException("打印预览窗口未算出页数");
            }

            return $"A4 分页 {inner.PageCount} 页，装饰后第 1 页尺寸 {page.Size.Width:0}×{page.Size.Height:0}";
    }

    /// <summary>用例 16：大文档虚拟渲染（后台解析 + 虚拟拆项 + 锚点映射 + 进度条收起；M2a 起替代渐进管线用例）</summary>
    private static async Task<string> ShellCase16()
    {
            // 造一篇工作量远超渐进阈值（ProgressiveBlockThreshold = 300）的文档：
            // 自动目录 + 800 节（标题+正文）+ 200 条脚注——覆盖「自动目录和大量引用仍卡死」的回归：
            // 虚拟化拆项后这些仍必须一次拆完（模型侧），元素则只按视口实例化。
            var builder = new StringBuilder();
            builder.Append("# 渐进渲染测试\n\n[[contents]]\n\n");
            for (var i = 1; i <= 800; i++)
            {
                var footnote = i % 4 == 0 ? $"^[第 {i} 节的脚注说明，用于覆盖脚注区分批构建。]" : string.Empty;
                builder.Append("### 第 ").Append(i).Append(" 节\n\n这是渐进渲染验证的第 ")
                       .Append(i).Append(" 段正文").Append(footnote).Append("。\n\n");
            }

            var view = new DocumentView(new DocumentTab { Text = builder.ToString(), UntitledIndex = 1 });
            view.ConfigureDefaults(null, null);

            await view.ReparseAsync();

            var document = view.Document
                           ?? throw new InvalidOperationException("渲染管线结束后解析结果为空");
            var topLevel = document.Root.Children.Count;
            if (topLevel <= DocumentView.ProgressiveBlockThreshold)
            {
                throw new InvalidOperationException(
                    $"测试文档顶层块 {topLevel} 未超过渐进阈值 {DocumentView.ProgressiveBlockThreshold}，用例失去意义");
            }

            if (document.Structure.Footnotes.Count != 200)
            {
                throw new InvalidOperationException(
                    $"测试文档脚注数异常：{document.Structure.Footnotes.Count}（应为 200），用例失去意义");
            }

            // 项列表全量就绪（虚拟化的意义：项是模型对象，拆项一次完成；元素按视口实例化）。
            // 本文无 ::: meta 头 → 项数 = 顶层块数 + 脚注区 1 项。
            var itemCount = view.DebugItemCount;
            if (itemCount < topLevel || itemCount > topLevel + 2)
            {
                throw new InvalidOperationException(
                    $"虚拟拆项不完整：列表项 {itemCount}，顶层块 {topLevel}");
            }

            // 自动目录：[[contents]] 段落升级为独立目录项，目录条目数必须与结构一致。
            if (view.Toc.Count != document.Structure.Toc.Count || view.Toc.Count == 0)
            {
                throw new InvalidOperationException(
                    $"自动目录不完整：大纲 {view.Toc.Count} 条（应为 {document.Structure.Toc.Count} 条）");
            }

            // 锚点映射：模型侧一次构建即全量（含后代块），至少覆盖全部顶层块。
            var anchors = view.DebugAnchorCount;
            if (anchors < topLevel)
            {
                throw new InvalidOperationException($"锚点映射不完整：{anchors} 条（顶层块 {topLevel}）");
            }

            if (view.RenderProgress.Visibility != Visibility.Collapsed)
            {
                throw new InvalidOperationException("渲染完成后进度条未收起");
            }

            return $"顶层块 {topLevel}、目录 {view.Toc.Count} 条、脚注 {document.Structure.Footnotes.Count} 条、"
                 + $"列表项 {itemCount}、锚点映射 {anchors}，虚拟渲染就绪";
    }

    /// <summary>用例 17：超大文档侧边栏大纲（虚拟化绑定节点构建 + 分级折叠策略）</summary>
    private static string ShellCase17()
    {
            // 造 5000 条目录：层级 1..6 循环起伏，覆盖多层嵌套与回退。
            var entries = new List<TocEntry>();
            for (var i = 0; i < 5000; i++)
            {
                entries.Add(new TocEntry
                {
                    Level = (i % 6) + 1,
                    Number = $"{i + 1}",
                    Title = $"大纲条目 {i + 1}",
                    IsSection = i % 2 == 0,
                    Source = null,
                });
            }

            var nodes = MainWindow.BuildOutlineNodes(entries);
            var total = CountNodes(nodes);
            if (total != 5000)
            {
                throw new InvalidOperationException($"大纲节点数不对：{total}（应为 5000）");
            }

            // 超过展开上限：仅前两级默认展开，深层折叠（保持超大文档大纲可用）。
            foreach (var node in WalkNodes(nodes))
            {
                var expected = node.Level <= 2;
                if (node.IsExpanded != expected)
                {
                    throw new InvalidOperationException(
                        $"分级折叠策略不符：Level {node.Level} 的 IsExpanded={node.IsExpanded}（应为 {expected}）");
                }
            }

            // 小文档（10 条）保持原有「全部展开」行为。
            var small = MainWindow.BuildOutlineNodes(entries.Take(10).ToList());
            if (small.Any(n => !n.IsExpanded) || WalkNodes(small).Any(n => !n.IsExpanded))
            {
                throw new InvalidOperationException("小文档大纲未全部展开，行为回归");
            }

            // 空文档：产出唯一占位节点（渲染为禁用）。
            var empty = MainWindow.BuildOutlineNodes(new List<TocEntry>());
            if (empty.Count != 0)
            {
                throw new InvalidOperationException("空目录应产出空根集合");
            }

            return $"5000 条大纲节点构建完成（根 {nodes.Count}），分级折叠与小文档全展开策略均正确";
    }

    /// <summary>
    /// 用例 54：就地编辑（滚动锁定式）——双击进入 → 预览滚动锁定 → 提交写回源码 → 解锁并重解析；
    /// Esc 取消不动源码。虚拟化与就地编辑并存的核心机制回归。
    /// </summary>
    private static async Task<string> ShellCase54()
    {
            var text = "# 编辑测试\n\n第一段原文。\n\n第二段原文。\n";
            var view = new DocumentView(new DocumentTab { Text = text, UntitledIndex = 1 });
            view.ConfigureDefaults(null, null);
            view.LoadText(text);

            // 容器实现需要真实布局：宿进一个屏幕外窗口（虚拟化面板按视口实例化视口内的项）。
            var window = new Window
            {
                Content = view,
                Width = 900,
                Height = 600,
                Left = -3000,
                ShowActivated = false,
                ShowInTaskbar = false,
            };
            window.Show();
            try
            {
                await view.ReparseAsync();
                await Task.Delay(80);   // 排空挂载后的首帧布局
                view.UpdateLayout();

                var document = view.Document
                               ?? throw new InvalidOperationException("解析结果为空");
                var paragraph = document.Root.Children.OfType<ParagraphBlock>().FirstOrDefault()
                                ?? throw new InvalidOperationException("测试文档缺少段落块");

                if (!view.BeginInPlaceEdit(paragraph))
                {
                    throw new InvalidOperationException("未能进入就地编辑态（容器未实现）");
                }

                if (!view.IsInPlaceEditing || !view.IsPreviewScrollLocked)
                {
                    throw new InvalidOperationException("编辑态下预览滚动未被锁定");
                }

                // 提交：写回源码、解锁滚动、退出编辑态。
                view.InPlaceEditText = "第一段已改。\n\n";
                view.CommitInPlaceEdit();
                await view.ReparseAsync();

                if (!view.Text.Contains("第一段已改", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("提交未写回源码");
                }

                if (view.Text.Contains("第一段原文", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("提交后旧文本残留");
                }

                if (view.IsInPlaceEditing || view.IsPreviewScrollLocked)
                {
                    throw new InvalidOperationException("提交后编辑态 / 滚动锁定未解除");
                }

                var committed = view.Document
                                ?? throw new InvalidOperationException("提交后的解析结果为空");
                var committedText = string.Concat(committed.Root.Children
                    .OfType<ParagraphBlock>().SelectMany(b => b.Inlines.OfType<TextInline>()).Select(t => t.Text));
                if (!committedText.Contains("第一段已改", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("提交后预览未按新源码重新解析");
                }

                // 取消：源码不动。
                var second = committed.Root.Children.OfType<ParagraphBlock>().LastOrDefault()
                             ?? throw new InvalidOperationException("测试文档缺少第二段落块");
                if (!view.BeginInPlaceEdit(second))
                {
                    throw new InvalidOperationException("第二次未能进入就地编辑态");
                }

                view.InPlaceEditText = "这段不该生效。\n\n";
                view.CancelInPlaceEdit();
                await view.ReparseAsync();

                if (view.Text.Contains("这段不该生效", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("取消路径写回了源码");
                }

                if (view.IsInPlaceEditing || view.IsPreviewScrollLocked)
                {
                    throw new InvalidOperationException("取消后编辑态 / 滚动锁定未解除");
                }

                return "进入 / 提交写回 / 取消全链路正确，滚动锁定随编辑态启停";
            }
            finally
            {
                window.Close();
            }
    }

    private static int CountNodes(IReadOnlyList<OutlineNode> nodes)
    {
        var count = 0;
        foreach (var node in WalkNodes(nodes))
        {
            count++;
        }
        return count;
    }

    private static IEnumerable<OutlineNode> WalkNodes(IReadOnlyList<OutlineNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            if (node.Children is not null)
            {
                foreach (var child in WalkNodes(node.Children))
                {
                    yield return child;
                }
            }
        }
    }
}
