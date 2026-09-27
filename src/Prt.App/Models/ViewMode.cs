namespace Prt.App.Models;

/// <summary>编辑器 / 预览的显示模式。</summary>
public enum ViewMode
{
    /// <summary>仅源码编辑。</summary>
    EditorOnly,

    /// <summary>左源码右预览（默认）。</summary>
    Split,

    /// <summary>仅预览。</summary>
    PreviewOnly,
}
