using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Prt.App.Theming;

/// <summary>
/// 系统标题栏的明暗同步。
/// <para>
/// 主窗口是自绘标题栏（<c>WindowStyle=None</c>）不受影响；但设置页、激活页等对话框仍使用
/// **系统绘制的标题栏**，Windows 默认按系统主题着色——在深色界面下会出现一条刺眼的浅色
/// 标题栏。这里用 DWM 属性把标题栏切到与界面配色一致（Win10 1809+ / Win11；旧系统调用失败时静默忽略）。
/// </para>
/// </summary>
internal static class DarkTitleBar
{
    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE（Win10 20H1 起）。</summary>
    private const int UseImmersiveDarkMode = 20;

    /// <summary>同一属性的早期编号（Win10 1809–1909）。</summary>
    private const int UseImmersiveDarkModeLegacy = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>按当前界面配色同步该窗口的系统标题栏。</summary>
    public static void Apply(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var value = App.Palette.IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref value, sizeof(int)) != 0)
            {
                // 返回值刻意丢弃：这是"新属性编号不被系统支持"的回退尝试。
                // 旧编号若同样失败，外层 catch 与"标题栏颜色不值得让程序出错"的既有
                // 取舍已经兜住；在此再判一次 HRESULT 只会多一条无处置动作的分支。
                _ = DwmSetWindowAttribute(handle, UseImmersiveDarkModeLegacy, ref value, sizeof(int));
            }
        }
        catch
        {
            // 精简版系统或非 DWM 环境下可能不可用；标题栏颜色不对不值得让程序出错。
        }
    }
}
