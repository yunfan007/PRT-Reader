using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Prt.Core;
using Prt.Core.Rendering;
using Prt.Core.Structure;
using Prt.Core.Syntax;
using PrtTextAlignment = Prt.Core.TextAlignment;
using WpfTextAlignment = System.Windows.TextAlignment;

namespace Prt.App.Rendering;

/// <summary>
/// PRT 语法树 → WPF <see cref="FlowDocument"/> 的原生渲染器。
/// <para>
/// 硬性约束：全部使用 WPF 原生文档与控件，不涉及任何浏览器内核 / WebView。
/// 布局块（分栏 / 折叠 / 选项卡）借助 <see cref="BlockUIContainer"/> 内嵌原生控件实现，
/// 以满足「锚点可跳转」与「无 WebView」两项要求。
/// </para>
/// <para>
/// 【COMP 模块不支持】计算块与插值在此按标准第 13 章降级呈现（源码 / 原文），不作任何求值。
/// </para>
/// </summary>
internal sealed partial class PreviewBuilder
{
    private readonly PreviewStyle _style;
    private readonly Dictionary<PrtBlock, FrameworkContentElement> _anchors = new();

    /// <summary>「预览元素 → 源块」反查表：与 <see cref="_anchors"/> 同步增量增长，供点击预览定位源码。</summary>
    private readonly Dictionary<FrameworkContentElement, PrtBlock> _reverse = new();

    public PreviewBuilder(PreviewStyle style)
    {
        _style = style;
    }

    /// <summary>文档所在目录，用于解析图片相对路径。</summary>
    public string? DocumentDirectory { get; set; }

    /// <summary>结构解析结果，用于展开 <c>[[contents]]</c>。</summary>
    public DocumentStructure? Structure { get; set; }

    /// <summary>
    /// 任务清单复选框被点击时的回调：参数为（任务项源行号（1 起），勾选后的状态）。
    /// 由 <see cref="Prt.App.Views.DocumentView"/> 提供，把状态写回源文本。
    /// </summary>
    public Action<int, bool>? TaskToggleCallback { get; set; }

    /// <summary>渐进构建首批渲染的顶层块数（游离构建从首批开始，全部就绪后由调用方一次性挂载）。</summary>
    public const int FirstChunkSize = 32;

    /// <summary>渐进构建每批追加的块数（追加到**游离**流文档，不触发布局；批间让出 UI 线程）。</summary>
    public const int ChunkSize = 48;

    /// <summary>渐进构建单批的最大工作量（目录条目 / 脚注 / 顶层块通用；批次按几何级数增长到此封顶）。</summary>
    public const int MaxChunkSize = 512;

    /// <summary>锚点表只读视图：渐进构建期间该表随批次增长，调用方持有同一实例即可持续读到新条目。</summary>
    public IReadOnlyDictionary<PrtBlock, FrameworkContentElement> Anchors => _anchors;

    /// <summary>渲染整篇文档。</summary>
    public PreviewResult Build(PrtDocument document)
    {
        _anchors.Clear();
        _reverse.Clear();
        Structure = document.Structure;
        // 注意：DocumentDirectory 由调用方在 Build 之前设置（用于解析图片相对路径），此处不得清空。

        var flow = CreateFlowDocument();
        AppendDocumentHeader(flow.Blocks, document.Metadata);
        AppendBlocks(flow.Blocks, document.Root.Children);
        AppendFootnoteArea(flow.Blocks, document);
        return new PreviewResult(flow, _anchors);
    }

    /// <summary>
    /// 渐进构建第一步：创建流文档框架（含文档头）并只渲染前 <paramref name="count"/> 个顶层块，
    /// 其余工作经 <paramref name="pending"/> 交还调用方按批追加。
    /// <para>
    /// 首批窗口内的整篇目录（<c>[[contents]]</c> 独占段落）不会一次性全量渲染：
    /// 先建目录外壳与首批条目，未完成的 <see cref="TocBuild"/> 按原顺序插入待办队列，
    /// 保证目录条目在预览中的位置与其源码位置一致。
    /// 仅长文档使用本 API（小文档直接 <see cref="Build"/> 一次成型）；返回的 <see cref="PreviewResult.Anchors"/>
    /// 与 <see cref="Anchors"/> 是同一张活表，随 <see cref="AppendChunk"/> 持续增长。
    /// 注意：对**已挂载**的流文档追加块会在该批触发整篇布局；惰性路径（DocumentView.RenderLazily）
    /// 以此为代价换取"打开即见首屏"，但把每批开销压到新增块本身（反查表 / 基线由锚点注册增量维护，
    /// 批间让渡 UI 线程），避免冻结。游离一次性构建路径则始终在挂载前完成全部追加，不改此约束。
    /// </summary>
    public PreviewResult BuildFrame(PrtDocument document, int count, out IReadOnlyList<object> pending, out int builtWork)
    {
        _anchors.Clear();
        _reverse.Clear();
        Structure = document.Structure;

        var flow = CreateFlowDocument();
        AppendDocumentHeader(flow.Blocks, document.Metadata);

        var children = document.Root.Children;
        var take = Math.Clamp(count, 1, children.Count);
        builtWork = 0;

        var queue = new List<object>();
        for (var i = 0; i < children.Count; i++)
        {
            if (i < take)
            {
                var rendered = RenderTopLevel(flow.Blocks, children[i], FirstChunkSize);
                builtWork += rendered.Done;
                if (rendered.Continuation is not null)
                {
                    queue.Add(rendered.Continuation);
                }
            }
            else
            {
                queue.Add(children[i]);
            }
        }

        pending = queue;
        return new PreviewResult(flow, _anchors);
    }

    /// <summary>渐进构建后续批次：把一批顶层块追加到流文档（单个块异常已在 <see cref="AppendBlocks"/> 内降级）。</summary>
    public void AppendChunk(FlowDocument flow, IEnumerable<PrtBlock> chunk) => AppendBlocks(flow.Blocks, chunk);


}
