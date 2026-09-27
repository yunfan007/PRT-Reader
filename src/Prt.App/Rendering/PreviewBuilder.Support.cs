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

// 辅助（PreviewBuilder 的 partial；拆分依据见《工程改进方案》§4.8 / D-06）。
internal sealed partial class PreviewBuilder
{
    // ─────────────────────────────── 辅助 ───────────────────────────────

    /// <summary><see cref="Anchors"/> 的反查：预览元素 → 源块（点击预览定位源码用，与锚点表同步增量增长）。</summary>
    public IReadOnlyDictionary<FrameworkContentElement, PrtBlock> ReverseAnchors => _reverse;

    /// <summary>锚点注册后的回调（渲染管线在锚点登记时实时更新反查表 / 采集基线，避免每批全量重建）。</summary>
    public Action<PrtBlock, FrameworkContentElement>? OnAnchorRegistered { get; set; }

    /// <summary>锚点注册单点：正反两张表同步写入，并通知渲染管线的增量维护回调。</summary>
    private void RegisterAnchor(PrtBlock block, FrameworkContentElement element)
    {
        _anchors[block] = element;
        _reverse[element] = block;
        OnAnchorRegistered?.Invoke(block, element);
    }

    /// <summary>
    /// 交叉引用 / 术语链接被点击时的回退导航：锚点表里没有目标元素时调用（虚拟预览下
    /// 目标块的元素由虚拟化按需实例化，FlowDocument 元素不存在，由宿主按「块 → 列表项」导航）。
    /// </summary>
    public Action<PrtBlock>? AnchorLinkFallback { get; set; }

    private void OnAnchorLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is Hyperlink { Tag: PrtBlock block })
        {
            if (_anchors.TryGetValue(block, out var element))
            {
                element.BringIntoView();
            }
            else
            {
                AnchorLinkFallback?.Invoke(block);
            }

            e.Handled = true;
        }
    }

    /// <summary>
    /// 尝试解析并加载本地图片（相对路径按文档所在目录解析）。
    /// 出于安全与离线一致性的考虑，不联网加载 http(s) 图片，仅以占位符呈现。
    /// </summary>
    private BitmapImage? TryLoadImage(string? url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            string? path = null;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                if (uri.IsFile)
                {
                    path = uri.LocalPath;
                }
                else
                {
                    // http/https 等远程资源不加载（离线、安全）。
                    return null;
                }
            }
            else if (!string.IsNullOrWhiteSpace(DocumentDirectory))
            {
                path = System.IO.Path.GetFullPath(System.IO.Path.Combine(DocumentDirectory!, url));
            }

            // 这里不再做 File.Exists 探测，理由有两层：
            // ① 探测本身是一次文件访问，按 3.8 不得绕开 SRT；而预览渲染是高频路径，
            //    逐图申报会在一次渲染里弹出几十次裁决窗。
            // ② 图片解码走的是 WPF 成像栈（BitmapImage），4.6 与 5.4 ② 的对照表都没有这一项，
            //    SRT 也就没有等价函数可替；图片文件的可达性以「文档本身的读取」申报（PRIV-01）为界。
            // 文件不存在或解码失败由下面的 catch 统一返回 null（与原先的"探测不过就返回 null"等效）。
            if (path is null)
            {
                return null;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
