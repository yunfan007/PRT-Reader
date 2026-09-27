using System;
using System.Threading;

namespace Perisc.Safety;

/// <summary>
/// 进程内环境的**唯一**持有者（实现内部），同时是运行时**生命周期的唯一责任人**。
/// <para>
/// 为什么必须有这样一个位置：2.3 规定模块一与模块二**共用同一实例**，
/// 以保证许可缓存与审计在两层之间一致；而 <c>AuditRecord</c> 的公开构造函数
/// 与 <c>AuditRecord.For</c> 是 4.7 / 6.1.7 的公开 API，签名里不能出现
/// <see cref="SafetyProcessState"/>（它是 internal）。
/// </para>
/// <para>
/// 与改造前的 <c>SafetyProcessState.Current</c> 的区别，正是本次改造的要点：
/// </para>
/// <list type="bullet">
/// <item>运行时**按显式构造**建立（<see cref="Acquire"/>），不再有公开/可任意改写的静态属性；
/// 外部无法替换已建立的实例，只能复用；</item>
/// <item>时钟作为构造依赖注入运行时，不再由运行时自己去读系统时间；</item>
/// <item>自检可用 <see cref="UseIsolated"/> 起一个隔离环境（旧设计下无法做到，
/// 因为静态单例无法在同一进程内被替换成第二份）。</item>
/// </list>
/// <para>
/// <b>释放契约（9.6.4 / D-11a、D-12）</b>：运行时持有审计存储，审计存储持有补写定时器——
/// 这是本进程内唯一一个"非托管时间源"，不能靠进程退出兜底。因此：
/// </para>
/// <list type="bullet">
/// <item><see cref="Acquire"/> 记一份持有（客户端实例），<see cref="Release"/> 还一份；</item>
/// <item>最后一份持有一离开、且**无人借用过**（<see cref="Runtime"/> 未被访问）时，
/// 运行时立即拆除（<see cref="SafetyProcessState.Dispose"/>）；</item>
/// <item>一旦有人借用过（<c>AuditRecord.For</c>、<c>SafeGuardClient</c> 这类"没有客户端实例"的调用方），
/// 运行时被钉住，改由 <see cref="Shutdown"/> 显式拆除——宿主退出的地方调用它
/// （见 <c>Prt.App</c> 的 <c>App.OnExit</c>）。</item>
/// </list>
/// <para>
/// 为什么"钉住"而不是"借用也计数"：借用方没有对称的归还点（<c>AuditRecord.For</c> 是静态方法，
/// 调用完就没人再提运行时），强行计数只会让计数永远回不到零，
/// 表面上有契约、实际仍然只能靠进程退出。钉住 + 显式拆除是这里唯一诚实的做法。
/// </para>
/// </summary>
internal static class SafetyEnvironment
{
    private static readonly object Gate = new();
    private static SafetyProcessState? _runtime;
    private static ISafetyClock _processClock = new MonotonicClock();
    private static int _owners;     // 显式持有者数量（SafetyClient / InterceptClient 实例）
    private static bool _pinned;    // 是否发生过无主借用 → 钉住到显式 Shutdown
    private static string? _auditDirectory;  // 仅在隔离环境内非空（见 UseIsolated）

    /// <summary>
    /// 进程时钟。供 <c>AuditRecord</c> 这类**公开数据对象**在调用方未给时刻时取值——
    /// 它们的构造签名由 4.7 规定，不能加时钟参数；取这里的时钟可保证"默认时刻"同样受
    /// 6.1.3 的回拨保护，而不是各自去读系统时间。
    /// </summary>
    internal static ISafetyClock Clock => _processClock;

    /// <summary>
    /// 新建审计存储的落点覆盖（默认 null = 用 <c>AuditStore</c> 的标准路径 %LOCALAPPDATA%\Perisc\Safety\audit）。
    /// <para>
    /// 只由 <see cref="UseIsolated"/> 在隔离区间内置位、并在还原时清回。
    /// 存在的理由与 <c>AuditStore</c> 的自检构造重载完全相同：自检要在一个进程内反复建/销毁审计存储，
    /// 落点必须避开用户真实数据，否则"自检"就成了一次对用户审计的写操作。
    /// </para>
    /// </summary>
    internal static string? AuditDirectory => _auditDirectory;

    /// <summary>
    /// 借用进程内共享运行时（不存在则按进程时钟建立）。
    /// **借用即钉住**：本次会话的运行时此后只能由 <see cref="Shutdown"/> 拆除。
    /// </summary>
    internal static SafetyProcessState Runtime
    {
        get
        {
            lock (Gate)
            {
                _pinned = true;
                return _runtime ??= new SafetyProcessState(_processClock);
            }
        }
    }

    /// <summary>
    /// 登记一份持有并返回运行时（由 <c>SafetyClient.Create</c> / <c>InterceptClient.Register</c> 调用）。
    /// 首次调用可指定时钟；此后重复调用复用既有实例（2.3），只补登记程序标识。
    /// </summary>
    internal static SafetyProcessState Acquire(ISafetyClock? clock = null)
    {
        lock (Gate)
        {
            if (_runtime is null)
            {
                if (clock is not null)
                {
                    _processClock = clock;
                }
                _runtime = new SafetyProcessState(_processClock);
            }
            _owners++;
            return _runtime;
        }
    }

    /// <summary>
    /// 归还一份持有（由客户端实例的 <c>Dispose</c> 调用）。
    /// 归还的是**当初拿到的那一个实例**，不是"当前的那个"——这样即使中途发生过
    /// <see cref="Shutdown"/> 并重建，旧实例的归还也不会误减新实例的计数。
    /// </summary>
    internal static void Release(SafetyProcessState runtime)
    {
        if (runtime is null)
        {
            return;
        }

        SafetyProcessState? teardown = null;
        lock (Gate)
        {
            if (_owners > 0)
            {
                _owners--;
            }
            if (!ReferenceEquals(_runtime, runtime))
            {
                // 已被 Shutdown 换掉：旧实例在那次 Shutdown 里已拆除，此处不再重复处置。
                return;
            }
            if (_owners == 0 && !_pinned)
            {
                teardown = _runtime;
                _runtime = null;
            }
        }

        // 拆除在锁外：AuditStore.Dispose 要等在途的补写退出，
        // 持环境锁做这件事会把整条安全链路的建立/归还全部堵住。
        teardown?.Dispose();
    }

    /// <summary>
    /// 强制拆除运行时（宿主退出、或自检需要把环境彻底复位时调用）。
    /// <para>
    /// 4.10 第 5 条：拆除**不撤销许可、不删除审计、不丢弃待写队列**——
    /// <see cref="AuditStore.Dispose"/> 只停补写定时器，待写队列保留在内存与磁盘上供下次启动接管。
    /// </para>
    /// </summary>
    internal static void Shutdown()
    {
        SafetyProcessState? teardown;
        lock (Gate)
        {
            teardown = _runtime;
            _runtime = null;
            _owners = 0;
            _pinned = false;
        }
        teardown?.Dispose();
    }

    /// <summary>
    /// **仅供启动自检**：以指定时钟起一个隔离环境，返回可还原的句柄。
    /// <para>还原时既换回原有引用，也**拆除隔离环境自己**（否则每跑一条自检用例就漏一个补写定时器）。</para>
    /// </summary>
    /// <param name="clock">隔离环境使用的时钟。</param>
    /// <param name="pinned">
    /// 是否把隔离环境的运行时钉住到句柄还原（默认是）。传 false 表示"可以按引用计数自动拆除"，
    /// 供自检验证归还时机（<see cref="Release"/> 的语义）；钉住/不钉住两种模式都要能被验证到。
    /// </param>
    /// <param name="auditDirectory">
    /// 隔离区间内新建审计存储的落点；null 表示沿用标准路径（自检不应这样做，见 <see cref="AuditDirectory"/>）。
    /// </param>
    internal static IDisposable UseIsolated(ISafetyClock clock, bool pinned = true, string? auditDirectory = null)
    {
        SafetyProcessState isolated;
        SafetyProcessState? previousRuntime;
        ISafetyClock previousClock;
        string? previousAuditDirectory;
        int previousOwners;
        bool previousPinned;

        lock (Gate)
        {
            previousRuntime = _runtime;
            previousClock = _processClock;
            previousAuditDirectory = _auditDirectory;
            previousOwners = _owners;
            previousPinned = _pinned;

            _processClock = clock;
            _auditDirectory = auditDirectory;
            isolated = _runtime = new SafetyProcessState(clock);
            _owners = 0;
            _pinned = pinned;
        }

        return new Restore(() =>
        {
            lock (Gate)
            {
                _runtime = previousRuntime;
                _processClock = previousClock;
                _auditDirectory = previousAuditDirectory;
                _owners = previousOwners;
                _pinned = previousPinned;
            }
            isolated.Dispose();
        });
    }

    private sealed class Restore : IDisposable
    {
        private readonly Action _action;

        public Restore(Action action) => _action = action;

        public void Dispose() => _action();
    }
}
