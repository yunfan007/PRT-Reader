using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Prt.Core;
using Prt.Core.Rendering;
using Prt.Core.Structure;
using Prt.Core.Syntax;

namespace Prt.App.Rendering;

/// <summary>
/// 虚拟预览的逐块渲染器（《虚拟化改造方案》M2a）。
/// <para>
/// 与 <see cref="PreviewBuilder"/>（整篇 <see cref="System.Windows.Documents.FlowDocument"/>，
/// 打印 / 导出 / 自检快照继续使用）并列：本类把「一个列表项」渲染成一块独立的
/// <see cref="FrameworkElement"/>，由 <see cref="Prt.App.Views.VirtualPreview"/> 的虚拟化面板
/// 只实例化视口内的若干项——滚动大文档时不再整篇参与 measure。
/// </para>
/// <para>
/// M2a 范围：段落 / 标题 / 分隔线 / 代码块 / 引用 / 列表（含任务清单复选框）为真实渲染；
/// 章节块按「标题 + 递归子块」最小真实渲染（结构块承载正文主体，降级卡片会把内容藏起来）；
/// 其余块降级为「类型标签 + 纯文本卡片」（内容不丢，样式化渲染在 M2b 补齐）。
/// 行内格式复用 <see cref="PreviewBuilder"/> 的行内渲染（单一实现），主题配色沿用
/// <see cref="PreviewStyle"/>——深色界面下预览随界面走深色的语义不变。
/// </para>
/// </summary>
internal sealed class VirtualPreviewBuilder
{
    private readonly PreviewStyle _style;

    /// <summary>
    /// 行内渲染引擎：借用 <see cref="PreviewBuilder"/> 的行内实现（AppendInlines 已 internal 化），
    /// 只用其行内方法，不调用 Build（不会触碰它的锚点表）。
    /// </summary>
    private readonly PreviewBuilder _inlines;

    public VirtualPreviewBuilder(PreviewStyle style)
    {
        _style = style;
        _inlines = new PreviewBuilder(style);
    }

    /// <summary>文档所在目录，用于解析图片相对路径（透传给行内引擎）。</summary>
    public string? DocumentDirectory
    {
        get => _inlines.DocumentDirectory;
        set => _inlines.DocumentDirectory = value;
    }

    /// <summary>任务清单复选框被点击时的回调（与 <see cref="PreviewBuilder.TaskToggleCallback"/> 同义）。</summary>
    public Action<int, bool>? TaskToggleCallback { get; set; }

    /// <summary>
    /// 交叉引用 / 目录项被点击且目标块没有已实现元素时的回退导航
    /// （宿主把它接到「虚拟列表滚动到对应项」）。
    /// </summary>
    public Action<PrtBlock>? AnchorLinkFallback
    {
        get => _inlines.AnchorLinkFallback;
        set => _inlines.AnchorLinkFallback = value;
    }

    // ─────────────────────────────── 拆项（模型侧，一次性） ───────────────────────────────

    /// <summary>把整篇文档拆成虚拟列表项，并建立「可锚定块 → 项索引」映射。</summary>
    public VirtualPreviewModel BuildModel(PrtDocument document)
    {
        _inlines.Structure = document.Structure;
        var items = new List<object>();
        var blockToIndex = new Dictionary<PrtBlock, int>();

        AppendHeader(items, document.Metadata);

        foreach (var child in document.Root.Children)
        {
            // [[contents]] 独占段落升级为独立目录项（可点击跳转），其余按顶层块一项。
            if (child is ParagraphBlock { Inlines.Count: 1 } paragraph && paragraph.Inlines[0] is ContentsInline contents)
            {
                // 该段落本身也登记锚点（点击定位、光标联动对目录段落同样有效）。
                blockToIndex[child] = items.Count;
                items.Add(new TocItem(contents));
            }
            else
            {
                RegisterItemBlocks(blockToIndex, child, items.Count);
                items.Add(child);
            }
        }

        if (document.Structure.Footnotes.Count > 0)
        {
            items.Add(new FootnoteAreaItem(document));
        }

        return new VirtualPreviewModel(items, blockToIndex);
    }

    /// <summary>把一个块（含其全部后代）登记到所在列表项的索引上——锚点语义与 FlowDocument 版一致。</summary>
    private static void RegisterItemBlocks(Dictionary<PrtBlock, int> map, PrtBlock block, int index)
    {
        map[block] = index;
        foreach (var child in ChildrenOf(block))
        {
            RegisterItemBlocks(map, child, index);
        }
    }

    /// <summary>枚举一个块的全部直接子块（锚点登记与降级文本提取共用）。</summary>
    private static IEnumerable<PrtBlock> ChildrenOf(PrtBlock block) => block switch
    {
        QuoteBlock quote => quote.Children,
        SectionBlock section => section.Children,
        SummaryBlock summary => summary.Children,
        TextStyleBlock textStyle => textStyle.Children,
        AlignBlock align => align.Children,
        ListItemBlock item => item.Children,
        LayoutBlock layout => layout.Children
            .Cast<PrtBlock>()
            .Concat(layout.ColumnItems)
            .Concat(layout.TabItems),
        _ => Enumerable.Empty<PrtBlock>(),
    };

    private static void AppendHeader(List<object> items, PrtMetadata metadata)
    {
        var hasTitle = !string.IsNullOrWhiteSpace(metadata.Title);
        var hasSubtitle = !string.IsNullOrWhiteSpace(metadata.Subtitle);
        var metaLine = BuildMetaLine(metadata);
        if (hasTitle || hasSubtitle || metaLine.Length > 0)
        {
            items.Add(new HeaderItem(metadata));
        }
    }

    private static string BuildMetaLine(PrtMetadata metadata)
    {
        var parts = new List<string>();
        if (metadata.Authors.Count > 0)
        {
            parts.Add(string.Join("、", metadata.Authors));
        }
        if (!string.IsNullOrWhiteSpace(metadata.Date))
        {
            parts.Add(metadata.Date!);
        }
        if (!string.IsNullOrWhiteSpace(metadata.Version))
        {
            parts.Add("版本 " + metadata.Version);
        }
        if (metadata.Tags.Count > 0)
        {
            parts.Add("标签：" + string.Join(" / ", metadata.Tags));
        }
        return string.Join(" · ", parts);
    }

    // ─────────────────────────────── 渲染（元素侧，按需） ───────────────────────────────

    /// <summary>把一个列表项渲染为独立的 <see cref="FrameworkElement"/>（容器实现时调用）。</summary>
    public FrameworkElement RenderItem(object item) => item switch
    {
        HeaderItem header => RenderHeader(header.Metadata),
        TocItem toc => RenderToc(toc.Contents),
        FootnoteAreaItem footnotes => RenderFootnotes(footnotes.Document),
        PrtBlock block => RenderBlock(block),
        FrameworkElement element => element,
        _ => new TextBlock { Text = "（无法呈现的列表项）", Foreground = _style.MutedText },
    };

    private FrameworkElement RenderHeader(PrtMetadata metadata)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        if (!string.IsNullOrWhiteSpace(metadata.Title))
        {
            panel.Children.Add(new TextBlock
            {
                Text = metadata.Title,
                FontSize = _style.HeadingSize(1) + 4,
                FontWeight = FontWeights.Bold,
                Foreground = _style.Heading,
                Margin = new Thickness(0, 0, 0, 4),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        if (!string.IsNullOrWhiteSpace(metadata.Subtitle))
        {
            panel.Children.Add(new TextBlock
            {
                Text = metadata.Subtitle,
                FontSize = _style.HeadingSize(3),
                Foreground = _style.SubtleText,
                Margin = new Thickness(0, 0, 0, 6),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        var metaLine = BuildMetaLine(metadata);
        if (metaLine.Length > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = metaLine,
                FontSize = 12.5,
                Foreground = _style.MutedText,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        return panel;
    }

    /// <summary>渲染一个块为 FE；单个块渲染异常就地降级，不拖垮整篇预览（与 FlowDocument 版同语义）。</summary>
    public FrameworkElement RenderBlock(PrtBlock block)
    {
        try
        {
            return RenderBlockCore(block);
        }
        catch (Exception ex)
        {
            return DegradeCard("[渲染异常] " + block.GetType().Name, ex.Message);
        }
    }

    private FrameworkElement RenderBlockCore(PrtBlock block) => block switch
    {
        // meta / refs / theme 属于「元信息块」，本身不产生正文内容（与 FlowDocument 版一致不渲染）。
        MetaBlock or RefsBlock or ThemeBlock => new Border { Height = 0, Margin = new Thickness(0) },
        ParagraphBlock paragraph => RenderParagraph(paragraph),
        HeadingBlock heading => RenderHeading(heading.Level, heading.AssignedNumber, heading.Inlines),
        ThematicBreakBlock => RenderDivider(),
        CodeBlock code => RenderCode(code.Code, code.Language),
        QuoteBlock quote => RenderQuote(quote),
        ListBlock list => RenderList(list),
        SectionBlock section => RenderSection(section),
        _ => RenderDegrade(block),
    };

    private FrameworkElement RenderParagraph(ParagraphBlock paragraph)
    {
        if (paragraph.Inlines.Count == 1 && paragraph.Inlines[0] is ContentsInline contents)
        {
            return RenderToc(contents);
        }
        var text = BlockText(paragraph.Inlines);
        text.Margin = new Thickness(0, 0, 0, 10);
        return text;
    }

    private FrameworkElement RenderHeading(int level, string? number, IReadOnlyList<PrtInline>? headingInlines, string? literalText = null)
    {
        var heading = new TextBlock
        {
            FontSize = _style.HeadingSize(level),
            FontWeight = FontWeights.SemiBold,
            Foreground = _style.Heading,
            Margin = new Thickness(0, 18, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        };
        if (!string.IsNullOrEmpty(number))
        {
            heading.Inlines.Add(new Run(number + " ") { Foreground = _style.SubtleText });
        }
        if (headingInlines is { Count: > 0 })
        {
            _inlines.AppendInlines(heading.Inlines, headingInlines);
        }
        else if (!string.IsNullOrEmpty(literalText))
        {
            heading.Inlines.Add(new Run(literalText));
        }

        if (level > 1)
        {
            return heading;
        }

        // 一级标题带底部细线（与 FlowDocument 版观感一致）。
        return new Border
        {
            BorderBrush = _style.Border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 6),
            Child = heading,
        };
    }

    private FrameworkElement RenderDivider() => new Border
    {
        Height = 1,
        Background = _style.Border,
        Margin = new Thickness(0, 14, 0, 14),
    };

    private FrameworkElement RenderCode(string code, string? language)
    {
        var panel = new StackPanel();
        if (!string.IsNullOrWhiteSpace(language))
        {
            panel.Children.Add(new TextBlock
            {
                Text = language,
                FontFamily = _style.MonoFamily,
                FontSize = 11,
                Foreground = _style.MutedText,
                Margin = new Thickness(0, 0, 0, 4),
            });
        }

        // 代码不折行，横向滚动查看（与常见阅读器一致；折行设置在 M2b 接管）。
        panel.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new TextBlock
            {
                Text = code,
                FontFamily = _style.MonoFamily,
                FontSize = 13,
                Foreground = _style.CodeText,
            },
        });

        return new Border
        {
            Background = _style.CodeBackground,
            BorderBrush = _style.Border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 4, 0, 12),
            Child = panel,
        };
    }

    private FrameworkElement RenderQuote(QuoteBlock quote)
    {
        var panel = new StackPanel();
        foreach (var child in quote.Children)
        {
            panel.Children.Add(RenderBlock(child));
        }

        return new Border
        {
            BorderBrush = _style.Border,
            BorderThickness = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(14, 4, 0, 4),
            Margin = new Thickness(0, 4, 0, 12),
            Child = panel,
        };
    }

    private FrameworkElement RenderSection(SectionBlock section)
    {
        var level = Math.Clamp(section.NumberLevel, 1, 7);
        var panel = new StackPanel { Margin = new Thickness(level > 1 ? 14 : 0, 0, 0, 0) };
        panel.Children.Add(RenderHeading(level, section.AssignedNumber, null, section.Title ?? string.Empty));
        foreach (var child in section.Children)
        {
            panel.Children.Add(RenderBlock(child));
        }
        return panel;
    }

    private FrameworkElement RenderList(ListBlock block)
    {
        var panel = new StackPanel { Margin = new Thickness(18, 0, 0, 10) };
        for (var i = 0; i < block.Items.Count; i++)
        {
            var item = block.Items[i];

            // 标记列 + 内容列：内容列要能按宽度折行，必须用 Grid 的星号列
            //（横向 StackPanel 会以无限宽测量子级，折行因此失效）。
            var row = new Grid { Margin = new Thickness(0, 0, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (item.IsTask)
            {
                // 任务清单（EXT）：真实复选框，点击经回调写回源文本（与 FlowDocument 版同语义）。
                var taskLine = item.Line;
                var checkBox = new CheckBox
                {
                    IsChecked = item.IsChecked,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 6, 0),
                };
                checkBox.Checked += (_, _) => TaskToggleCallback?.Invoke(taskLine, true);
                checkBox.Unchecked += (_, _) => TaskToggleCallback?.Invoke(taskLine, false);
                Grid.SetColumn(checkBox, 0);
                row.Children.Add(checkBox);
            }
            else
            {
                var marker = new TextBlock
                {
                    Text = block.Ordered ? (block.StartNumber + i) + ". " : "• ",
                    Foreground = _style.SubtleText,
                    Margin = new Thickness(0, 0, 2, 0),
                };
                Grid.SetColumn(marker, 0);
                row.Children.Add(marker);
            }

            var content = BlockText(item.Inlines);
            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            panel.Children.Add(row);

            foreach (var child in item.Children)
            {
                var nested = RenderBlock(child);
                nested.Margin = new Thickness(18, 0, 0, nested.Margin.Bottom);
                panel.Children.Add(nested);
            }
        }
        return panel;
    }

    /// <summary>M2a 降级卡片：类型标签 + 纯文本（内容不丢，样式化渲染在 M2b 补齐）。</summary>
    private FrameworkElement RenderDegrade(PrtBlock block)
    {
        var (label, body) = Describe(block);
        return DegradeCard(label, body);
    }

    private FrameworkElement DegradeCard(string label, string body)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = _style.MutedText,
            Margin = new Thickness(0, 0, 0, 3),
        });
        if (!string.IsNullOrWhiteSpace(body))
        {
            panel.Children.Add(new TextBlock
            {
                Text = body,
                FontSize = _style.BodyFontSize - 1,
                Foreground = _style.SubtleText,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        return new Border
        {
            Background = _style.Surface,
            BorderBrush = _style.Border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 4, 0, 12),
            Child = panel,
        };
    }

    /// <summary>
    /// 预览内目录明细的「全部展开」条目上限（与侧边栏大纲 <c>MainWindow.OutlineExpandAllLimit</c> 同一策略）：
    /// 条目不超过该值时直接展开明细；超过则默认收起为一行占位，点击展开——
    /// 实测 3001 条明细一次全量构建 + 布局 ≈ 2 秒 UI 停顿（首屏的主要停顿源），收起后打开大文档即零停顿。
    /// </summary>
    internal const int TocExpandAllLimit = 300;

    private FrameworkElement RenderToc(ContentsInline contents)
    {
        var entries = FilterToc(contents.Depth, contents.Mode);
        var panel = new StackPanel { Margin = new Thickness(12, 0, 0, 14) };
        if (entries.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "（目录：当前无已编号章节）",
                FontStyle = FontStyles.Italic,
                Foreground = _style.MutedText,
            });
            return panel;
        }

        // 小目录：直接展开明细（观感与 FlowDocument 版一致）。
        if (entries.Count <= TocExpandAllLimit)
        {
            foreach (var line in BuildTocEntryLines(entries))
            {
                panel.Children.Add(line);
            }

            return panel;
        }

        // 大目录：默认收起为一行占位，首次点击才构建明细（方案原文允许「展开为目录明细或保持占位」）。
        var summary = new TextBlock
        {
            Text = "目录（" + entries.Count + " 条，点击展开）",
            Foreground = _style.SubtleText,
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = Brushes.Transparent,   // 让整行（而非仅文字字形）可点
        };
        var holder = new ContentControl();
        var expanded = false;
        summary.MouseLeftButtonUp += (_, e) =>
        {
            if (expanded)
            {
                return;
            }

            expanded = true;
            var detail = new StackPanel();
            foreach (var line in BuildTocEntryLines(entries))
            {
                detail.Children.Add(line);
            }

            holder.Content = detail;
            summary.Text = "目录（" + entries.Count + " 条）";
            e.Handled = true;
        };
        panel.Children.Add(summary);
        panel.Children.Add(holder);
        return panel;
    }

    /// <summary>把目录条目构建为带缩进的可点击明细行（点击经 <see cref="AnchorLinkFallback"/> 滚动到目标块）。</summary>
    private IEnumerable<TextBlock> BuildTocEntryLines(List<TocEntry> entries)
    {
        foreach (var entry in entries)
        {
            var line = new TextBlock
            {
                Margin = new Thickness(Math.Max(0, entry.Level - 1) * 18, 1, 0, 1),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var text = string.IsNullOrWhiteSpace(entry.Number)
                ? entry.Title
                : entry.Number + " " + entry.Title;
            var link = new Hyperlink(new Run(text))
            {
                Foreground = _style.SubtleText,
                TextDecorations = null,
            };
            if (entry.Source is { } target)
            {
                var captured = target;
                link.Click += (_, e) =>
                {
                    AnchorLinkFallback?.Invoke(captured);
                    e.Handled = true;
                };
            }

            line.Inlines.Add(link);
            yield return line;
        }
    }

    private FrameworkElement RenderFootnotes(PrtDocument document)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 20, 0, 10) };
        panel.Children.Add(new Border { Height = 1, Background = _style.Border, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock
        {
            Text = "脚注",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = _style.SubtleText,
            Margin = new Thickness(0, 0, 0, 6),
        });
        foreach (var footnote in document.Structure.Footnotes)
        {
            panel.Children.Add(new TextBlock
            {
                Text = footnote.Number + ". " + footnote.Content,
                FontSize = 12.5,
                Foreground = _style.SubtleText,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
            });
        }
        return panel;
    }

    private List<TocEntry> FilterToc(int? depth, string mode)
    {
        var result = new List<TocEntry>();
        var structure = _inlines.Structure;
        if (structure is null)
        {
            return result;
        }

        foreach (var entry in structure.Toc)
        {
            if (depth.HasValue && entry.Level > depth.Value)
            {
                continue;
            }
            if (string.Equals(mode, "numbered", StringComparison.OrdinalIgnoreCase) && !entry.IsNumbered)
            {
                continue;
            }
            result.Add(entry);
        }
        return result;
    }

    // ─────────────────────────────── 文本与排版辅助 ───────────────────────────────

    /// <summary>按正文排版构造一个可换行的文本块（行高与 FlowDocument 版一致）。</summary>
    private TextBlock BlockText(IEnumerable<PrtInline> inlines)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            LineHeight = _style.BodyFontSize * 1.65,
            Foreground = _style.Text,
        };
        _inlines.AppendInlines(text.Inlines, inlines);
        return text;
    }

    /// <summary>降级卡的类型标签与纯文本内容（模型侧提取，不触碰任何 WPF 文档对象）。</summary>
    private static (string Label, string Body) Describe(PrtBlock block) => block switch
    {
        PipeTableBlock pipe => ("表格（管道表）", TableText(pipe.Table)),
        TableBlock table => ("表格（" + (table.Caption ?? "完整表格") + "）", TableText(table.Table)),
        SemanticBlock semantic => ("语义块（" + semantic.Kind + (semantic.Title is null ? string.Empty : "：" + semantic.Title) + "）", BlocksText(semantic.Children)),
        LayoutBlock layout => ("布局块（" + layout.Kind + "）", BlocksText(layout.Children.Count > 0
            ? layout.Children
            : layout.ColumnItems.Cast<PrtBlock>().Concat(layout.TabItems))),
        FigureBlock figure => ("插图（" + (figure.Caption ?? figure.Alt ?? figure.Source ?? "无题注") + "）", figure.Source ?? string.Empty),
        EquationBlock equation => ("公式（" + (equation.AssignedNumber ?? equation.Caption ?? "未编号") + "）", equation.Source),
        ComputationBlock computation => ("计算（" + computation.Kind + "，COMP 受限，按标准第 13 章降级为原文）", computation.Source),
        UnknownBlock unknown => ("未知块（" + unknown.TypeName + "）", unknown.Body),
        SummaryBlock summary => ("摘要", BlocksText(summary.Children)),
        TextStyleBlock textStyle => ("样式文本", BlocksText(textStyle.Children)),
        AlignBlock align => ("对齐文本", BlocksText(align.Children)),
        RefsBlock => ("引用注册表", string.Empty),
        MetaBlock => ("元数据", string.Empty),
        ThemeBlock => ("主题覆盖", string.Empty),
        _ => (block.GetType().Name, string.Empty),
    };

    private static string TableText(TableModel table)
    {
        var rows = new List<string>();
        foreach (var row in table.HeaderRows.Concat(table.BodyRows).Concat(table.FooterRows))
        {
            rows.Add(string.Join(" | ", row.Cells.Select(cell => InlineListText(cell.Inlines))));
        }
        return string.Join(Environment.NewLine, rows);
    }

    private static string BlocksText(IEnumerable<PrtBlock> blocks)
        => string.Join(Environment.NewLine, blocks.Select(BlockTextOf).Where(text => text.Length > 0));

    private static string BlockTextOf(PrtBlock block) => block switch
    {
        ParagraphBlock paragraph => InlineListText(paragraph.Inlines),
        HeadingBlock heading => InlineListText(heading.Inlines),
        CodeBlock code => code.Code,
        QuoteBlock quote => BlocksText(quote.Children),
        ListBlock list => string.Join(Environment.NewLine,
            list.Items.Select(item => (item.IsTask ? (item.IsChecked ? "[x] " : "[ ] ") : "- ") + InlineListText(item.Inlines))),
        SectionBlock section => (section.Title ?? string.Empty)
            + (section.Children.Count > 0 ? Environment.NewLine + BlocksText(section.Children) : string.Empty),
        ComputationBlock computation => computation.Source,
        UnknownBlock unknown => unknown.Body,
        _ => string.Empty,
    };

    private static string InlineListText(IEnumerable<PrtInline> inlines)
        => string.Concat(inlines.Select(InlineTextOf));

    private static string InlineTextOf(PrtInline inline) => inline switch
    {
        TextInline text => text.Text,
        LiteralInline literal => literal.Text,
        CodeInline code => code.Text,
        KbdInline kbd => kbd.Keys,
        DateLiteralInline date => date.Text,
        InterpolationInline interpolation => interpolation.LiteralText,
        FootnoteInline footnote => "[^" + footnote.Number + " " + footnote.Content + "]",
        RefInline reference => reference.ResolvedText ?? reference.DisplayText ?? "[[ref:" + reference.TargetId + "]]",
        TermLinkInline term => term.ResolvedText ?? term.DisplayText ?? term.Key,
        LinkInline link when link.IsImage => "[图片: " + (string.IsNullOrWhiteSpace(link.Alt) ? link.Url : link.Alt) + "]",
        LinkInline link => string.Concat(link.Children.Select(InlineTextOf)),
        LineBreakInline => " ",
        EmphasisInline emphasis => string.Concat(emphasis.Children.Select(InlineTextOf)),
        StrikeInline strike => string.Concat(strike.Children.Select(InlineTextOf)),
        HighlightInline highlight => string.Concat(highlight.Children.Select(InlineTextOf)),
        UnderlineInline underline => string.Concat(underline.Children.Select(InlineTextOf)),
        SuperscriptInline superscript => string.Concat(superscript.Children.Select(InlineTextOf)),
        SubscriptInline subscript => string.Concat(subscript.Children.Select(InlineTextOf)),
        ColorInline color => string.Concat(color.Children.Select(InlineTextOf)),
        BackgroundColorInline background => string.Concat(background.Children.Select(InlineTextOf)),
        SizeInline size => string.Concat(size.Children.Select(InlineTextOf)),
        _ => inline.RawText ?? string.Empty,
    };
}

/// <summary>虚拟预览的列表项基类（模型侧标记类型，渲染由 <see cref="VirtualPreviewBuilder.RenderItem"/> 承担）。</summary>
internal abstract class VirtualItemBase
{
}

/// <summary>文档头（标题 / 副标题 / 元信息行）。无任何元信息时不生成。</summary>
internal sealed class HeaderItem(PrtMetadata metadata) : VirtualItemBase
{
    public PrtMetadata Metadata { get; } = metadata;
}

/// <summary>整篇目录（<c>[[contents]]</c> 独占段落）。</summary>
internal sealed class TocItem(ContentsInline contents) : VirtualItemBase
{
    public ContentsInline Contents { get; } = contents;
}

/// <summary>脚注区（有脚注时追加为最后一个列表项）。</summary>
internal sealed class FootnoteAreaItem(PrtDocument document) : VirtualItemBase
{
    public PrtDocument Document { get; } = document;
}

/// <summary>虚拟预览列表的项模型：项列表（全量）＋「可锚定块 → 项索引」映射。</summary>
internal sealed class VirtualPreviewModel
{
    public VirtualPreviewModel(IReadOnlyList<object> items, IReadOnlyDictionary<PrtBlock, int> blockToIndex)
    {
        Items = items;
        BlockToIndex = blockToIndex;
    }

    /// <summary>列表项全量集合（元素按需实例化，正是虚拟化的意义）。</summary>
    public IReadOnlyList<object> Items { get; }

    /// <summary>可锚定块 → 所在列表项索引（含顶层块与全部后代块）。</summary>
    public IReadOnlyDictionary<PrtBlock, int> BlockToIndex { get; }
}
