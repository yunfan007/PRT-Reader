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

/// <summary>预览渲染结果：文档本体 + 「源块 → 渲染元素」锚点表（供目录与交叉引用跳转）。</summary>
internal sealed class PreviewResult
{
    public PreviewResult(FlowDocument document, IReadOnlyDictionary<PrtBlock, FrameworkContentElement> anchors)
    {
        Document = document;
        Anchors = anchors;
    }

    public FlowDocument Document { get; }

    public IReadOnlyDictionary<PrtBlock, FrameworkContentElement> Anchors { get; }
}
