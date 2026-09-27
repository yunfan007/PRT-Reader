using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Prt.App.Views;

/// <summary>
/// 自绘标题栏所需的 Win32 / DWM 互操作。
/// <para>
/// 从 <see cref="MainWindow"/> 抽出的独立职责（《工程改进方案》§4.8 / CRS 9.6.4 D-06）：
/// 这里全是**静态纯函数**——输入窗口句柄、返回或修正结果，不持有窗口状态，
/// 因此不依赖 <see cref="MainWindow"/> 的任何字段，可以脱离窗口单独审读与复用。
/// </para>
/// <para>
/// 抽出的另一个理由是命名空间一致性（9.6.4 / D-05）：原先这段 P/Invoke 与界面逻辑混在一个文件里，
/// "哪些代码会调用系统 API"这件事只能靠通读 1800 行才能回答。
/// </para>
/// </summary>
internal static class WindowInterop
{
    private const int WmGetMinMaxInfo = 0x0024;

    private const int MonitorDefaultToNearest = 0x00000002;

    private const int DwmWindowCornerPreference = 33;

    private const int DwmWindowCornerRound = 2;

    /// <summary>
    /// 给窗口挂上消息钩子，并尽力开启 Windows 11 的圆角窗角。
    /// **调用方**：<see cref="MainWindow.OnSourceInitialized"/>——句柄只有在那时才存在。
    /// </summary>
    public static void Attach(IntPtr handle)
    {
        HwndSource.FromHwnd(handle)?.AddHook(MessageHook);
        TryEnableRoundedCorners(handle);
    }

    /// <summary>
    /// 自绘标题栏的必要配套：把最大化尺寸限制到显示器工作区。
    /// <para>
    /// 窗口使用 <see cref="WindowChrome"/> 且 <c>WindowStyle=None</c> 时，系统会把窗口铺满整块屏幕
    /// （含任务栏与边框），导致内容被裁掉；此处接管 <c>WM_GETMINMAXINFO</c> 修正为工作区。
    /// </para>
    /// </summary>
    private static IntPtr MessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmGetMinMaxInfo)
        {
            return IntPtr.Zero;
        }

        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero)
        {
            var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref monitorInfo))
            {
                var work = monitorInfo.Work;
                var screen = monitorInfo.Monitor;

                info.MaxPosition.X = Math.Abs(work.Left - screen.Left);
                info.MaxPosition.Y = Math.Abs(work.Top - screen.Top);
                info.MaxSize.X = Math.Abs(work.Right - work.Left);
                info.MaxSize.Y = Math.Abs(work.Bottom - work.Top);
            }
        }

        Marshal.StructureToPtr(info, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>Windows 11 圆角窗角；旧系统上该属性不存在，静默忽略。</summary>
    private static void TryEnableRoundedCorners(IntPtr handle)
    {
        try
        {
            var preference = DwmWindowCornerRound;
            _ = DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref preference, sizeof(int));
        }
        catch (Exception)
        {
            // Windows 10 及更早版本没有 DWM 圆角偏好设置，保持直角即可。
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public int Flags;
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("dwmapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
