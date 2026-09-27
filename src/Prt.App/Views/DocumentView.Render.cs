using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Prt.App.Models;
using Prt.App.Rendering;
using Prt.App.Services;
using Prt.Core;
using Prt.Core.Export;
using Prt.Core.Rendering;
using Prt.Core.Syntax;

namespace Prt.App.Views;

// 解析、预览构建与三格式导出（DocumentView 的 partial；D-06）
// 拆分依据：《工程改进方案》§4.8 / CRS 9.6.4 D-06；拆分**只做位置迁移**，未改行为。
public partial class DocumentView : UserControl
{
    // ─────────────────────────────── 解析与渲染 ───────────────────────────────

    /// <summary>渲染管线代数：每次启动新解析周期递增，惰性补载据此识别「已被更新的周期取代」。</summary>
    private int _renderGeneration;

    /// <summary>重新解析当前文本并刷新预览（fire-and-forget 同步签名；解析在后台线程完成）。</summary>
    public void Reparse() => _ = ReparseAsync();

    /// <summary>
    /// 重新解析当前文本并刷新预览。<b>解析在后台线程完成</b>，UI 只负责占位与挂载，
    /// 因此打开大文档时界面不会被解析冻结（旧实现把整个同步管线压在 UI 线程，超大文档
    /// 解析阶段会卡死到计算完毕才可用）。
    /// <para>
    /// 历史教训：曾用「后台解析 + DispatcherPriority.Background 让渡」导致 UI 更新被调度
    /// 队列长期饿死（解析已完成但预览迟迟不出现）。这里改用 <see cref="Task.Run"/> 完成
    /// 纯计算 + <see cref="DispatcherPriority.Normal"/> 回 UI 挂载，不再自行降优先级让渡，
    /// 规避该坑。
    /// </para>
    /// </summary>
    public async Task ReparseAsync()
    {
        // 解析基于源码真源 Tab.Text（编辑器大文本分帧填充期间 Editor 可能尚不完整，
        // 不再从此处同步 Editor→Tab；正常编辑时由 OnEditorTextChanged 维护 Tab.Text）。
        _options.StrictOverride = StrictOverride;
        _renderedOnce = true;
        var generation = ++_renderGeneration;
        ShowRenderProgress();
        // 进入解析在途：主窗口据此把侧边栏大纲切到「计算中」占位（先显示、后计算），
        // 待真正就绪（下方 DispatchUi 成功或失败后）再复位并广播 StateChanged。
        IsParseInProgress = true;
        ParseStarted?.Invoke(this, EventArgs.Empty);

        try
        {
            // 解析是纯计算（语法树 + 结构/编号/引用），无 UI 依赖，可安全离屏。
            var snapshot = Tab.Text;
            var options = _options.Clone();
            var parsed = await Task.Run(() => PrtParser.Parse(snapshot, options));

            // 解析期间可能又有新一次解析启动：旧结果作废，不再应用。
            // 注意：此处不复位 IsParseInProgress——新一轮解析（更高世代）仍可能在途，
            // 只有最后启动的那一轮才有资格在完成/失败时复位该标志。
            if (generation != _renderGeneration)
            {
                return;
            }

            await DispatchUi(() =>
            {
                Document = parsed;
                var theme = ResolveTheme(parsed, out var resolvedName);
                ResolvedPreviewTheme = resolvedName;

                if (_editBlock is null)
                {
                    var style = new PreviewStyle(theme);
                    RenderVirtual(parsed, style);
                }
                else
                {
                    // 就地编辑进行中：本次结果暂不上屏——重建列表项会把编辑框拆掉。
                    // 编辑提交 / 取消都会立即重新解析渲染，届时自然采用这份解析结果。
                }

                HideRenderProgress();
                IsParseInProgress = false;
                StateChanged?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (Exception ex)
        {
            IsParseInProgress = false;
            try
            {
                await DispatchUi(() =>
                {
                    ShowPreviewFailure("解析或渲染失败，预览无法生成。", ex);
                    StateChanged?.Invoke(this, EventArgs.Empty);
                });
            }
            catch
            {
                // fire-and-forget 防火墙：UI 呈现彻底失败（理论上不致此）也不再上抛。
            }
        }
    }

    /// <summary>开始解析时在预览顶部显示不确定进度条（长文档后台计算期间的视觉反馈）。</summary>
    private void ShowRenderProgress()
    {
        RenderProgress.IsIndeterminate = true;
        RenderProgress.Visibility = Visibility.Visible;
    }

    /// <summary>把 UI 操作调度到本项目前的 Dispatcher（已在线程则直接执行），以 Normal 优先级规避低优先级让渡饿死。</summary>
    private async Task DispatchUi(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            await Dispatcher.InvokeAsync(action, DispatcherPriority.Normal);
        }
    }

    /// <summary>
    /// 虚拟渲染（《虚拟化改造方案》M2a）：解析结果拆成列表项挂进虚拟化容器，元素按需实例化。
    /// <para>
    /// 与旧「游离全量构建 + 一次性挂载」不同：项列表是模型对象（轻量、一次性），
    /// 块元素只在你滚动到它时才渲染——打开大文档的首屏开销只与视口有关，与总块数无关。
    /// 惰性补载管线（LazyRender）因此随本路径退役：虚拟化天然「看到哪里渲染到哪里」。
    /// </para>
    /// </summary>
    private void RenderVirtual(PrtDocument parsed, PreviewStyle style)
    {
        var builder = new VirtualPreviewBuilder(style)
        {
            DocumentDirectory = FileService.DirectoryOf(Tab.FilePath),
            TaskToggleCallback = ToggleTaskLine,
            AnchorLinkFallback = ScrollVirtualToBlock,
        };

        var model = builder.BuildModel(parsed);
        SwapInItems(model, builder, style);
    }

    /// <summary>交叉引用 / 目录项点击：滚动虚拟列表到目标块所在项。</summary>
    private void ScrollVirtualToBlock(PrtBlock block)
    {
        if (_anchorIndex.TryGetValue(block, out var index))
        {
            Preview.ScrollToIndex(index);
        }
    }

    /// <summary>
    /// 把拆好的列表项挂到虚拟预览区并复位联动状态。
    /// <para>
    /// 就地编辑已移除（M2 取舍），挂载后只恢复滚动位置。
    /// 块元素按需实例化，偏移恢复在像素滚动单位下是近似值。
    /// </para>
    /// </summary>
    private void SwapInItems(VirtualPreviewModel model, VirtualPreviewBuilder builder, PreviewStyle style)
    {
        var savedOffset = _previewScroll?.VerticalOffset ?? 0;

        Preview.ResetItems();
        Preview.ItemRenderer = builder.RenderItem;
        Preview.ItemsSource = model.Items;
        Preview.Background = style.PageBackground;

        _anchorIndex = model.BlockToIndex;
        ResetCaretSync();

        if (savedOffset > 0 && _previewScroll is not null)
        {
            _programmaticScroll = true;
            try
            {
                _previewScroll.ScrollToVerticalOffset(savedOffset);
            }
            finally
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _programmaticScroll = false));
            }
        }
    }

    /// <summary>渲染完成后隐藏进度条。</summary>
    private void HideRenderProgress() => RenderProgress.Visibility = Visibility.Collapsed;

    /// <summary>可锚定块总数（启动自检核验锚点映射完整性用；模型侧一次构建即全量就绪）。</summary>
    internal int DebugAnchorCount => _anchorIndex.Count;

    /// <summary>当前虚拟列表项总数（自检核验拆项完整性用）。</summary>
    internal int DebugItemCount => Preview.Items.Count;

    /// <summary>在预览区就地呈现失败原因，保证预览永不无声空白；同时给出状态栏提示。</summary>
    private void ShowPreviewFailure(string summary, Exception exception)
    {
        HideRenderProgress();
        _anchorIndex = new Dictionary<PrtBlock, int>();

        // 失败面板也跟随界面配色（深色界面下不出现白底错误页）。
        var style = new PreviewStyle(PrtTheme.BuiltIn(App.Palette.IsDark ? "dark" : "default", out _));

        var panel = new StackPanel { Margin = new Thickness(28, 22, 28, 48) };
        panel.Children.Add(new TextBlock
        {
            Text = summary,
            FontWeight = FontWeights.SemiBold,
            Foreground = style.Heading,
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = exception.Message,
            FontFamily = style.MonoFamily,
            FontSize = style.BodyFontSize - 1,
            Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = TextWrapping.Wrap,
        });

        Preview.ResetItems();
        Preview.ItemRenderer = _ => panel;
        Preview.ItemsSource = new[] { new FailureItem() };
        Preview.Background = style.PageBackground;
        StatusMessage?.Invoke(this, summary + " " + exception.Message);
    }

    private PrtTheme ResolveTheme(PrtDocument document, out string resolvedName)
    {
        var requested = PreviewThemeName;

        // 「跟随」的语义：深色界面下，预览显示区也随界面走深色（深色模式全局生效），
        // 文档声明的浅色主题（如 meta.theme: default）不再把预览拉回白底；
        // 浅色界面下仍以文档声明为准，未声明则用 default。
        if (string.IsNullOrWhiteSpace(requested) ||
            string.Equals(requested, AppSettings.PreviewThemeFollow, StringComparison.Ordinal))
        {
            if (App.Palette.IsDark)
            {
                requested = "dark";
            }
            else
            {
                var declared = document.Metadata.Theme;
                requested = string.IsNullOrWhiteSpace(declared) ? "default" : declared;
            }
        }

        return PrtTheme.Resolve(requested, document.ThemeBlocks, out resolvedName);
    }

    /// <summary>
    /// 构建一份**独立的**流文档供打印使用。
    /// <para>
    /// 必须重新构建：<see cref="FlowDocument"/> 不能同时挂在两处（屏幕预览已占用现成那份）。
    /// </para>
    /// </summary>
    /// <param name="monochrome">true 时用 print 主题（黑白省墨）。</param>
    /// <returns>解析尚未完成时返回 null。</returns>
    public FlowDocument? BuildPrintableDocument(bool monochrome)
    {
        if (Document is not { } document)
        {
            return null;
        }

        string requested;
        if (monochrome)
        {
            requested = "print";
        }
        else if (PreviewThemeName is { Length: > 0 } name &&
                 !string.Equals(name, AppSettings.PreviewThemeFollow, StringComparison.Ordinal))
        {
            requested = name;
        }
        else
        {
            requested = document.Metadata.Theme is { Length: > 0 } declared
                ? declared
                : App.Palette.IsDark ? "dark" : "default";
        }

        var theme = PrtTheme.Resolve(requested, document.ThemeBlocks, out _);
        var style = new PreviewStyle(theme);

        var builder = new PreviewBuilder(style)
        {
            DocumentDirectory = FileService.DirectoryOf(Tab.FilePath),
            // 打印版不响应点击：回调为 null 时任务清单的复选框不会去改源文本。
            TaskToggleCallback = null,
        };

        return builder.Build(document).Document;
    }

    /// <summary>预览主题的解析结果（如 default / dark / print），供打印等处参考。</summary>
    public string CurrentThemeName => ResolvedPreviewTheme;

    /// <summary>失败占位项：仅作为 <see cref="ShowPreviewFailure"/> 的列表内容标记。</summary>
    private sealed class FailureItem
    {
    }

    /// <summary>
    /// 界面配色变化后通知视图：预览主题为「跟随」时需按新配色重算（深色界面 → 深色预览）。
    /// </summary>
    public void NotifyInterfaceThemeChanged()
    {
        if (string.IsNullOrWhiteSpace(PreviewThemeName) ||
            string.Equals(PreviewThemeName, AppSettings.PreviewThemeFollow, StringComparison.Ordinal))
        {
            Reparse();
        }
    }

    /// <summary>显式设置自动折行（设置页用；与「切换」不同，避免连续应用时来回翻转）。</summary>
    public void SetWordWrap(bool wrap)
    {
        Editor.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        Editor.HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 在光标处插入一段文本（教程窗口的「插入到编辑器」用）。
    /// 以「替换当前选区」的方式写入，从而保住 TextBox 的撤销栈。
    /// </summary>
    public void InsertAtCaret(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        Editor.Focus();
        var start = Math.Clamp(Editor.CaretIndex, 0, Editor.Text.Length);
        Editor.Select(start, Editor.SelectionLength);
        Editor.SelectedText = text;
        Editor.CaretIndex = start + text.Length;
        Editor.ScrollToLine(Editor.GetLineIndexFromCharacterIndex(Editor.CaretIndex));
    }

    /// <summary>
    /// 预览中的任务清单复选框被点击：把源文本对应行中的「[ ]」/「[x]」取反。
    /// <para>
    /// 直接改编辑器文本（而非仅改预览），从而复用既有管线：脏标记、撤销栈、
    /// 防抖后的重新解析与预览重建都会自动发生，预览中的复选框状态随之更新。
    /// </para>
    /// </summary>
    private void ToggleTaskLine(int lineNumber, bool isChecked)
    {
        var (start, length) = GetLineSpan(Editor.Text, lineNumber);
        if (start < 0)
        {
            return;
        }

        var lineText = Editor.Text.Substring(start, length);
        var oldToken = isChecked ? "[ ]" : "[x]";
        var newToken = isChecked ? "[x]" : "[ ]";
        var position = lineText.IndexOf(oldToken, StringComparison.Ordinal);
        if (position < 0)
        {
            return;
        }

        var updated = lineText[..position] + newToken + lineText[(position + oldToken.Length)..];

        // 以「选区替换」写入：保住 TextBox 的撤销栈，也避免整体重设 Text 造成的滚动跳变。
        Editor.Select(start, length);
        Editor.SelectedText = updated;
    }

    /// <summary>计算指定行（1 起）在文本中的字符区间。</summary>
    private static (int Start, int Length) GetLineSpan(string text, int lineNumber)
    {
        var current = 1;
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] == '\n')
            {
                if (current == lineNumber)
                {
                    return (start, i - start);
                }

                current++;
                start = i + 1;
            }
        }

        return (-1, -1);
    }

    /// <summary>应用默认配置（在视图加载前调用，避免重复解析）。</summary>
    public void ConfigureDefaults(string? previewTheme, bool? strictOverride)
    {
        PreviewThemeName = previewTheme;
        StrictOverride = strictOverride;
    }

    public void SetPreviewTheme(string? name)
    {
        PreviewThemeName = name;
        Reparse();
    }

    public void SetStrictOverride(bool? strict)
    {
        StrictOverride = strict;
        Reparse();
    }

    public void Reload(string text)
    {
        LoadText(text);
        Reparse();
    }

    /// <summary>保存成功后清除脏标记。</summary>
    public void MarkClean()
    {
        Tab.IsDirty = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ─────────────────────────────── 导出 ───────────────────────────────

    public string ToHtml()
    {
        var document = Document;
        return document is null ? string.Empty : HtmlRenderer.Render(document);
    }

    public string ToMarkdown()
    {
        var document = Document;
        return document is null ? string.Empty : MarkdownRenderer.Render(document);
    }

    public string ToPlainText()
    {
        var document = Document;
        return document is null ? string.Empty : PlainTextRenderer.Render(document);
    }
}
