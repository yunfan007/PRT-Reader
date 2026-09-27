using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Perisc.Safety.SafeGuard;

/// <summary>
/// 启动器的控制台判定与接管（<b>平台特定实现</b>：Windows 的 kernel32 / user32）。
/// <para>
/// 启动器是 <b>GUI 子系统</b>程序（csproj 里的 <c>WinExe</c>）：系统不会为它建控制台，
/// 双击时屏幕上因此既不出现黑框、也不出现终端标签页——这是「<b>不产生</b>」，不是「产生了再藏起来」。
/// </para>
/// <para>
/// <b>为什么不再是"隐藏黑框"</b>：早先用控制台子系统 + <c>ShowWindow(SW_HIDE)</c> 隐藏自建窗口，
/// 该做法只在 <c>conhost.exe</c> 作终端宿主时有效。Windows 11 起默认终端是
/// <b>Windows Terminal</b>（ConPTY）：控制台窗口归 <c>WindowsTerminal.exe</c> 所有，
/// 启动器进程内 <c>GetConsoleWindow()</c> 拿到的只是自己的一个 0×0 <c>PseudoConsoleWindow</c>
/// 消息窗，对它调 <c>ShowWindow</c> 什么也藏不掉（实测那一轮：窗口全程可见 11.8 s 未消失，
/// 而探针只认 <c>ConsoleWindowClass</c>，于是误报"未出现"）。**没有任何 API 能在不连带隐藏
/// 用户自己终端的前提下藏掉它**，改子系统是唯一根治办法。
/// </para>
/// <para>
/// <b>命令行用法由 <see cref="AttachToParentConsole"/> 保住</b>：从 cmd / PowerShell / 脚本启动时，
/// 附加回<b>父进程</b>的控制台并把标准流重接到它上面，输出与交互照旧。附不上就说明父进程没有控制台
/// ——那正是「双击（资源管理器）/ 从无控制台的宿主启动」这一支，走图形路径。
/// 判据因此比原来更直接：**有没有控制台可用**，而不是"控制台是不是本次启动新建的"。
/// </para>
/// <para>
/// <b>代价（须在合规材料里写明，不得读成"可以关掉守护"）</b>：GUI 子系统进程不被 shell 等待——
/// cmd / PowerShell 会在启动器仍在运行时先返回提示符；脚本要等它结束须用
/// <c>Start-Process -Wait</c> 或 <c>Process.WaitForExit</c>（<c>build.ps1</c> 本来就是后者）。
/// 这一条只影响"谁来等"，<b>不改变任何安全行为</b>：运行体哈希校验、作业对象、命名管道、
/// 终止规则一律照旧。
/// </para>
/// <para>
/// 图形路径下用户看不到 <c>Console.Error</c>，因此启动失败必须走系统弹窗
/// （<see cref="ShowFatal"/>）——否则双击遇到 HASH-MISMATCH 时会毫无反馈地一闪而过。
/// </para>
/// </summary>
internal static class GuardConsole
{
    /// <summary>附加到父进程的控制台（<c>ATTACH_PARENT_PROCESS</c>）。</summary>
    private const int AttachParentProcess = -1;

    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    private const uint MessageBoxOk = 0x00000000;
    private const uint MessageBoxIconError = 0x00000010;
    private const uint MessageBoxSetForeground = 0x00010000;
    private const uint MessageBoxTopMost = 0x00040000;

    private static readonly IntPtr InvalidHandleValue = new(-1);

    /// <summary>不带 BOM 的 UTF-8：标准流出 BOM 会污染下游（管道、日志、报告）。</summary>
    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 把本次启动接到一个可用的控制台上；返回 <c>false</c> 表示<b>父进程没有控制台</b>，
    /// 本次走图形路径（双击、或从没有控制台的宿主启动）。
    /// <para>
    /// <b>顺序不可颠倒，且必须先记后接</b>：<c>AttachConsole</c> 会把「继承来的」标准句柄
    /// <b>顶成父控制台的句柄</b>——实测 `cmd /c "…--program …" 2> 日志.txt` 这条路径上，
    /// 附加之后 stderr 从「文件」变成了「控制台」，于是"拒绝启动"的原因写进了用户看不见的控制台，
    /// 而用户指定的日志文件是空的（同一次实测：被顶掉的原句柄在附加之后**仍然有效**，
    /// 所以能原样装回去）。因此流程固定为：① 记下三个标准句柄 → ② 附加 →
    /// ③ 把① 中本来可用的装回去 → ④ 先定输出编码、再接仍缺失的流。
    /// </para>
    /// <para>
    /// ③ 必须在④ 之前：<c>Console.OutputEncoding</c> 的 setter 只改控制台代码页、
    /// 不重建已有的 <c>Console.Out</c>；若先接出一个 GBK 的写出器再改代码页，中文就会变成乱码。
    /// </para>
    /// <para>
    /// 任何异常都判为「没有控制台」：判定拿不准时走图形路径是保守侧——那时失败有弹窗兜底，
    /// 而误判成命令行路径会让"拒绝启动"变成一句没人看得见的 stderr。
    /// </para>
    /// </summary>
    /// <returns>本次启动是否具备可用的控制台。</returns>
    public static bool AttachToParentConsole()
    {
        try
        {
            // ① 先记下调用方给的标准句柄，免得被下面的附加顶掉（见方法说明）。
            var savedOutput = GetStdHandle(StdOutputHandle);
            var savedError = GetStdHandle(StdErrorHandle);
            var savedInput = GetStdHandle(StdInputHandle);
            var outputWasUsable = IsUsable(savedOutput);
            var errorWasUsable = IsUsable(savedError);
            var inputWasUsable = IsUsable(savedInput);

            // 已经带着控制台（父进程显式给了一个）时不再附加：对已附着控制台的进程调
            // AttachConsole 只会拿到 ERROR_ACCESS_DENIED。
            var alreadyAttached = GetConsoleProcessList(new uint[4], 4) > 0;
            if (!alreadyAttached && !AttachConsole(AttachParentProcess))
            {
                // 父进程没有控制台 = 图形路径（双击、无控制台的自动化调用）。这不是失败。
                return false;
            }

            // ③ 把被顶掉的装回去：调用方重定向到文件 / 管道时，那是它明确要的落点。
            if (outputWasUsable)
            {
                _ = SetStdHandle(StdOutputHandle, savedOutput);
            }
            if (errorWasUsable)
            {
                _ = SetStdHandle(StdErrorHandle, savedError);
            }
            if (inputWasUsable)
            {
                _ = SetStdHandle(StdInputHandle, savedInput);
            }

            TrySetUtf8Output();
            RewireStandardStreams();
            return true;
        }
        catch
        {
            // 吞掉的是"控制台接管这一步出了岔子"（P/Invoke 不可用、句柄耗尽等）；
            // 降级到图形路径——观感与提示仍成立，安全行为不受影响。
            // 何时应传播：不需要——它决定的是"输出往哪写、失败怎么告知"，不是裁决本身。
            return false;
        }
    }

    /// <summary>
    /// 用系统弹窗报告启动失败。
    /// <para>
    /// 只在<b>没有控制台</b>时使用：那时 <c>Console.Error</c> 无处可去，
    /// 而"拒绝启动"必须让用户知道（PSS 3.4：启动失败不得静默）。
    /// </para>
    /// <para>
    /// 归属窗口取桌面（<c>IntPtr.Zero</c>）而不是某个窗口：双击路径上启动器没有自己的窗口可挂，
    /// 挂在桌面上能保证弹在最前、不被别的窗口压住。
    /// </para>
    /// </summary>
    public static void ShowFatal(string message)
    {
        try
        {
            _ = MessageBoxW(IntPtr.Zero, message, "PRT 阅读器 · 宿主启动器",
                MessageBoxOk | MessageBoxIconError | MessageBoxSetForeground | MessageBoxTopMost);
        }
        catch
        {
            // 连弹窗都打不开时已无更强的告知手段，保持退出码语义不变（调用方据此判失败）。
        }
    }

    /// <summary>
    /// 输出接 UTF-8（与既有行为一致）。设不动就保持默认，不让这一句成为启动的失败源：
    /// 控制台代码页在无控制台时改不了，旧代码那句无条件赋值在 GUI 子系统下会直接抛"句柄无效"。
    /// </summary>
    private static void TrySetUtf8Output()
    {
        try
        {
            Console.OutputEncoding = Utf8NoBom;
        }
        catch
        {
            // 吞掉的是"控制台代码页改不动"（无控制台 / 已重定向到文件）；
            // 降级到默认编码——输出仍写得出去，只是非 UTF-8。
        }
    }

    /// <summary>
    /// 把标准流重接到刚附上的控制台。
    /// <para>
    /// <b>只补缺、不覆盖</b>：调用方做了重定向（<c>&gt; 日志.txt</c>、管道）或父进程已经传下句柄时，
    /// 那份句柄是调用方明确要的落点——重接到 <c>CONOUT$</c> 等于把输出从用户指定的地方抢走。
    /// 无控制台时 <c>GetStdHandle</c> 返回 0 或 -1，这两种才补。
    /// </para>
    /// </summary>
    private static void RewireStandardStreams()
    {
        RewireIfMissing(StdOutputHandle, "CONOUT$", stream =>
            Console.SetOut(new StreamWriter(stream, Utf8NoBom) { AutoFlush = true }));
        RewireIfMissing(StdErrorHandle, "CONOUT$", stream =>
            Console.SetError(new StreamWriter(stream, Utf8NoBom) { AutoFlush = true }));
        RewireIfMissing(StdInputHandle, "CONIN$", stream =>
            Console.SetIn(new StreamReader(stream, Utf8NoBom)));
    }

    private static void RewireIfMissing(int standardHandle, string deviceName, Action<Stream> assign)
    {
        if (IsUsable(GetStdHandle(standardHandle)))
        {
            return;
        }

        var handle = CreateFileW(deviceName, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == InvalidHandleValue)
        {
            return;     // 控制台设备打不开：保持原句柄（无效），不替代调用方的选择
        }

        _ = SetStdHandle(standardHandle, handle);
        // FileStream 接过句柄所有权：进程退出时随 Console 的写出器一起释放。
        assign(new FileStream(new SafeFileHandle(handle, ownsHandle: true), FileAccess.ReadWrite));
    }

    private static bool IsUsable(IntPtr handle) => handle != IntPtr.Zero && handle != InvalidHandleValue;

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern uint GetConsoleProcessList([Out] uint[] processList, uint count);

    [DllImport("kernel32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int standardHandle, IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern IntPtr CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
