using System.Windows;
using Prt.App.Models;
using Prt.App.Services;
using StructLayoutKind = System.Runtime.InteropServices.LayoutKind;

namespace Prt.App.Views;

// 自检入口（MainWindow 的 partial；D-06）
// 拆分依据：《工程改进方案》§4.8 / CRS 9.6.4 D-06；拆分**只做位置迁移**，未改行为。
public partial class MainWindow : Window
{
    // ─────────────────────────────── 自检入口（仅 SelfTest 使用）───────────────────────────────

    /// <summary>触发「打开示例文档」流程；SelfTest 调用以验证标签创建链路。</summary>
    internal Task OpenSampleForTestAsync() => OpenSampleAsync();

    /// <summary>通过与标签头 X 按钮完全相同的路径关闭标签。</summary>
    internal void CloseTabForTest(DocumentTab model) => CloseTab(model);

    /// <summary>按磁盘路径查找标签；返回 <c>null</c> 表示未找到。</summary>
    internal TabEntry? FindTabByPathForTest(string fullPath)
    {
        return _entries.FirstOrDefault(entry =>
            string.Equals(entry.Model.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
    }
}
