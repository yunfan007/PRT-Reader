using Prt.App.Services;

namespace Prt.App.Models;

/// <summary>一个打开中的文本文档（标签页的纯数据部分，不含任何界面元素）。</summary>
public sealed class DocumentTab
{
    /// <summary>磁盘路径；新建未保存时为 null。</summary>
    public string? FilePath { get; set; }

    /// <summary>新建文档的序号（用于区分多个「未命名」标签）。</summary>
    public int UntitledIndex { get; set; }

    /// <summary>标签页标题（以文件名为准，未命名时按序号区分）。</summary>
    public string Title => FilePath is null
        ? (UntitledIndex > 0 ? $"未命名-{UntitledIndex}" : "未命名")
        : FileService.DisplayName(FilePath);

    /// <summary>当前文本内容。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>自上次保存以来是否有修改。</summary>
    public bool IsDirty { get; set; }

    /// <summary>是否尚未落盘的新建文档。</summary>
    public bool IsUntitled => FilePath is null;

    /// <summary>完整标题（含脏标记），用于标签页与窗口标题。</summary>
    public string DisplayTitle => Title + (IsDirty ? " *" : string.Empty);
}
