using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Prt.Core;
using Prt.Core.Structure;
using Prt.Core.Syntax;

namespace Prt.App.Rendering;

// 渐进构建的「长块」分批：整篇目录（[[contents]]）与脚注区（PreviewBuilder 的 partial）。
// <para>
// 这两类内容的单个块渲染量与全文档规模成正比（数千条目录 / 脚注）：若在单个批次内一次成型，
// 构建阶段仍会出现单次长耗时。因此它们与普通顶层块分开处理：外壳先行落位
// （目录 List / 脚注区分隔线与标题），条目按批追加到**游离**文档上，批间让出 UI 线程；
// 全部就绪后由调用方一次性挂载（挂载后的任何追加都会触发整篇重排，是卡顿根源）。
// </para>
internal sealed partial class PreviewBuilder
{
    /// <summary>整篇目录的渐进构建状态：目录外壳已挂到文档，条目跨批追加。</summary>
    public sealed class TocBuild
    {
        internal List ListElement = null!;
        internal List<TocEntry> Remaining = new();

        /// <summary>末尾的「正在计算目录…」占位条目；全部条目就位后移除。</summary>
        internal ListItem? ProgressItem;

        /// <summary>目录总条目数。</summary>
        public int Total { get; internal set; }

        /// <summary>已追加条目数。</summary>
        public int Done { get; internal set; }

        /// <summary>是否全部条目已就位。</summary>
        public bool IsDone => Done >= Total;
    }

    /// <summary>脚注区的渐进构建状态：分隔线与「脚注」标题已落位，条目跨批追加。</summary>
    public sealed class FootnoteBuild
    {
        internal BlockCollection Target = null!;
        internal List<FootnoteEntry> Remaining = new();

        /// <summary>脚注总条数。</summary>
        public int Total { get; internal set; }

        /// <summary>已追加条数。</summary>
        public int Done { get; internal set; }

        /// <summary>是否全部条目已就位。</summary>
        public bool IsDone => Done >= Total;
    }

    /// <summary>判定顶层块是否为「整篇目录」段落（<c>[[contents]]</c> 独占一段）；是则返回其内容指令。</summary>
    public static ContentsInline? GetWholeDocumentToc(PrtBlock block)
        => block is ParagraphBlock paragraph
           && paragraph.Inlines.Count == 1
           && paragraph.Inlines[0] is ContentsInline contents
            ? contents
            : null;

    /// <summary>统计该目录将渲染的条目数（渐进判定的工作量估算用）。</summary>
    public int CountTocEntries(ContentsInline contents) => FilterToc(contents.Depth, contents.Mode).Count;

    /// <summary>
    /// 渐进渲染一个顶层块：整篇目录改走分批构建（<see cref="BeginTocList"/>），
    /// 其余块与 <see cref="AppendBlocks"/> 等价（含逐块降级与通用锚点注册）。
    /// 返回已完成工作量与未完成的续体（目录条目未追加完时为 <see cref="TocBuild"/>）。
    /// </summary>
    internal (int Done, object? Continuation) RenderTopLevel(BlockCollection target, PrtBlock block, int batch)
    {
        if (GetWholeDocumentToc(block) is { } contents)
        {
            var build = BeginTocList(target, contents, batch);
            return (build.Done, build.IsDone ? null : build);
        }

        // 锚点判定用 LastBlock 引用比较（O(1)）：TextTree 集合的 Count 是 O(n)，逐块查询退化为 O(n²)。
        var lastBefore = target.LastBlock;
        try
        {
            AppendBlock(target, block);
        }
        catch (Exception ex)
        {
            // 与 AppendBlocks 的单块降级一致：一个块渲染异常不拖垮整篇预览。
            target.Add(new Paragraph(new Run($"[渲染异常] {block.GetType().Name}：{ex.Message}"))
            {
                Foreground = _style.MutedText,
                FontStyle = FontStyles.Italic,
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 8),
            });
        }

        if (target.LastBlock is FrameworkContentElement element && !ReferenceEquals(element, lastBefore))
        {
            RegisterAnchor(block, element);
        }

        return (1, null);
    }

    /// <summary>
    /// 开始渐进构建整篇目录：目录外壳（<see cref="List"/>）先挂到文档并填入首批条目，
    /// 其余条目经 <see cref="AppendTocChunk"/> 跨批追加。空目录直接落一条占位说明（与同步路径一致）。
    /// </summary>
    public TocBuild BeginTocList(BlockCollection target, ContentsInline contents, int firstBatch)
    {
        var entries = FilterToc(contents.Depth, contents.Mode);
        var build = new TocBuild { Total = entries.Count, Remaining = entries };

        if (entries.Count == 0)
        {
            target.Add(new Paragraph(new Run("（目录：当前无已编号章节）"))
            {
                FontStyle = FontStyles.Italic,
                Foreground = _style.MutedText,
                Margin = new Thickness(0, 0, 0, 12),
            });
            return build;
        }

        var list = new List
        {
            MarkerStyle = TextMarkerStyle.None,
            Margin = new Thickness(12, 0, 0, 14),
            Padding = new Thickness(0),
        };
        target.Add(list);
        build.ListElement = list;

        // 先把「正在计算目录…」占位条目挂到列表末尾：条目跨批追加期间目录区始终有
        // 明确的「计算中」反馈（长文档后台计算时的视觉提示），全部就位后由 AppendTocChunk 移除。
        var progressItem = new ListItem();
        progressItem.Blocks.Add(new Paragraph(new Run("正在计算目录…"))
        {
            FontStyle = FontStyles.Italic,
            Foreground = _style.MutedText,
            Margin = new Thickness(Math.Max(0, 0), 0, 0, 3),
        });
        list.ListItems.Add(progressItem);
        build.ProgressItem = progressItem;

        AppendTocChunk(build, firstBatch);
        return build;
    }

    /// <summary>把一批目录条目追加到目录外壳（<see cref="List.ListItems"/> 持续追加；文档游离期间无布局代价）。</summary>
    public void AppendTocChunk(TocBuild build, int batch)
    {
        if (build.IsDone)
        {
            return;
        }

        var take = Math.Min(batch, build.Remaining.Count);
        // ListItemCollection 不支持按索引插入；改用「临时移除占位 → 追加条目 → 占位放回末尾」，
        // 既保持「计算中」占位始终在列表末尾，又免于重建 ListItems 集合。
        var progress = build.ProgressItem;
        if (progress is not null)
        {
            build.ListElement.ListItems.Remove(progress);
        }

        for (var i = 0; i < take; i++)
        {
            build.ListElement.ListItems.Add(CreateTocListItem(build.Remaining[i]));
        }

        build.Remaining.RemoveRange(0, take);
        build.Done += take;

        if (build.IsDone && progress is not null)
        {
            // 全部条目就位：不再挂回占位，让最终目录与同步路径渲染结果一致（条目数即目录总条数）。
            build.ProgressItem = null;
        }
        else if (progress is not null)
        {
            build.ListElement.ListItems.Add(progress);
        }
    }

    /// <summary>开始渐进构建脚注区：分隔线与「脚注」标题先落位，条目跨批追加；无脚注返回 null（与同步路径一致）。</summary>
    public FootnoteBuild? BeginFootnoteArea(FlowDocument flow, PrtDocument document, int firstBatch)
    {
        var footnotes = document.Structure.Footnotes;
        if (footnotes.Count == 0)
        {
            return null;
        }

        var target = flow.Blocks;
        target.Add(new BlockUIContainer(new Border { Height = 1, Background = _style.Border })
        {
            Margin = new Thickness(0, 20, 0, 10),
        });
        target.Add(new Paragraph(new Run("脚注"))
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = _style.SubtleText,
            Margin = new Thickness(0, 0, 0, 6),
        });

        var build = new FootnoteBuild
        {
            Target = target,
            Total = footnotes.Count,
            Remaining = footnotes.ToList(),
        };
        AppendFootnoteChunk(build, firstBatch);
        return build;
    }

    /// <summary>把一批脚注条目追加到已挂载的脚注区。</summary>
    public void AppendFootnoteChunk(FootnoteBuild build, int batch)
    {
        if (build.IsDone)
        {
            return;
        }

        var take = Math.Min(batch, build.Remaining.Count);
        for (var i = 0; i < take; i++)
        {
            var footnote = build.Remaining[i];
            var paragraph = new Paragraph
            {
                FontSize = 12.5,
                Foreground = _style.SubtleText,
                Margin = new Thickness(0, 0, 0, 4),
            };
            paragraph.Inlines.Add(new Run(footnote.Number + ". "));
            paragraph.Inlines.Add(new Run(footnote.Content));
            build.Target.Add(paragraph);
        }

        build.Remaining.RemoveRange(0, take);
        build.Done += take;
    }

    /// <summary>目录条目 → 列表项（同步路径与渐进路径共用，保证两种构建方式渲染结果一致）。</summary>
    private ListItem CreateTocListItem(TocEntry entry)
    {
        var item = new ListItem();
        var paragraph = new Paragraph
        {
            Margin = new Thickness(Math.Max(0, entry.Level - 1) * 14, 0, 0, 3),
        };
        paragraph.Inlines.Add(new Run(JoinNumber(entry.Number, entry.Title))
        {
            Foreground = entry.IsSection ? _style.Text : _style.SubtleText,
        });
        item.Blocks.Add(paragraph);
        return item;
    }
}
