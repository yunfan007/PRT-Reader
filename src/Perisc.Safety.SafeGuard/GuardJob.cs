using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Perisc.Safety.SafeGuard;

/// <summary>
/// 作业对象（Job Object）封装（PSS 标准 6.2.4 一，规范性；<b>平台特定实现</b>）。
/// <para>
/// 为什么必须用作业对象而不是遍历父子关系：Windows <b>不自动维护进程树</b>——
/// 以 <c>DETACHED_PROCESS</c> 等方式启动的进程不会挂在父子链上，只靠"遍历父子关系"数后代是会漏的；
/// 而漏掉的那个进程可能正是被注入的部分，终止不彻底等于没终止。
/// 作业对象由内核维护成员集合，进程在作业内创建的后代自动成为成员（除非显式 CREATE_BREAKAWAY_FROM_JOB）。
/// </para>
/// <para>
/// 仅在 Windows 上可用：非 Windows 上 <c>kernel32.dll</c> 不存在，构造会抛
/// <see cref="DllNotFoundException"/>，由宿主启动器按启动失败处置（不得降级为"照常启动"）。
/// </para>
/// </summary>
internal sealed class GuardJob : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    private GuardJob(IntPtr handle) => _handle = handle;

    /// <summary>创建作业对象，并设为「句柄一关，全部成员进程即被终止」。</summary>
    public static GuardJob Create()
    {
        var handle = Native.CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "创建作业对象失败。");
        }

        try
        {
            // KILL_ON_JOB_CLOSE 是「收干净」的保证：宿主启动器退出或释放句柄时，
            // 内核负责终止作业内全部进程，不依赖本进程此刻是否还活着、是否能逐个 Kill。
            var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new Native.JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = Native.JobObjectLimitKillOnJobClose,
                },
            };
            var size = Marshal.SizeOf<Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, pointer, false);
                if (!Native.SetInformationJobObject(handle, Native.JobObjectExtendedLimitInformation, pointer, (uint)size))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "设置作业对象限额失败。");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
            return new GuardJob(handle);
        }
        catch
        {
            Native.CloseHandle(handle);
            throw;
        }
    }

    /// <summary>把进程纳入本作业；此后它创建的后代自动成为成员。</summary>
    public void Assign(int processId)
    {
        ThrowIfDisposed();
        using var process = System.Diagnostics.Process.GetProcessById(processId);
        if (!Native.AssignProcessToJobObject(_handle, process.Handle))
        {
            var error = Marshal.GetLastWin32Error();
            // 进程可能刚退出；此时纳入无意义，但也不应让启动流程整体失败。
            if (error != Native.ErrorInvalidParameter && error != Native.ErrorAccessDenied)
            {
                throw new Win32Exception(error, "把进程纳入作业对象失败。");
            }
        }
    }

    /// <summary>
    /// 终止前实时重枚举成员进程（6.2.4 一：取值时机必须是终止前，不得用启动时的快照）。
    /// 收尾窗口内新建的子进程同样在范围内，故每次终止前都重新取一次。
    /// </summary>
    public IReadOnlyList<int> EnumerateProcessIds()
    {
        ThrowIfDisposed();
        var capacity = 64;
        while (true)
        {
            var size = 8 + capacity * IntPtr.Size; // 两个计数 + pid 数组
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!Native.QueryInformationJobObject(_handle, Native.JobObjectBasicProcessIdList,
                        buffer, (uint)size, out var returned))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == Native.ErrorMoreData || error == Native.ErrorInsufficientBuffer)
                    {
                        capacity *= 4;
                        continue;
                    }
                    // 查询失败时退化为「不知道有哪些成员」：返回空集，
                    // 由调用方仍按作业句柄整体终止（TerminateJobObject 不依赖这份列表）。
                    return Array.Empty<int>();
                }
                var count = (int)Marshal.ReadInt32(buffer, 4); // NumberOfProcessIdsInList
                count = Math.Min(count, capacity);
                var ids = new List<int>(count);
                for (var i = 0; i < count; i++)
                {
                    var value = Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size);
                    ids.Add((int)value.ToInt64());
                }
                return ids;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>
    /// 终止作业内全部进程（6.2.4：立即、彻底，含全部后代）。
    /// 用 <c>TerminateJobObject</c> 而不是逐个 <c>Process.Kill</c>：逐个杀会留下时间窗，
    /// 期间新起的后代就漏了；作业对象一次调用覆盖全体成员。
    /// </summary>
    public bool TerminateAll(uint exitCode = 1)
    {
        ThrowIfDisposed();
        return Native.TerminateJobObject(_handle, exitCode);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            // 关闭句柄即触发 KILL_ON_JOB_CLOSE：这是一道兜底，
            // 使宿主启动器异常退出时也不会留下孤儿进程。
            Native.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private static class Native
    {
        public const uint JobObjectLimitKillOnJobClose = 0x2000;
        public const int JobObjectExtendedLimitInformation = 9;
        public const int JobObjectBasicProcessIdList = 3;
        public const int ErrorMoreData = 234;
        public const int ErrorInsufficientBuffer = 122;
        public const int ErrorInvalidParameter = 87;
        public const int ErrorAccessDenied = 5;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint infoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryInformationJobObject(IntPtr job, int infoClass,
                                                            IntPtr info, uint infoLength, out uint returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public IntPtr MinimumWorkingSetSize;
            public IntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public IntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public IntPtr ProcessMemoryLimit;
            public IntPtr JobMemoryLimit;
            public IntPtr PeakProcessMemoryUsed;
            public IntPtr PeakJobMemoryUsed;
        }
    }
}
