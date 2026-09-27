using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Perisc.Safety;

/// <summary>审计写入结果（模块内部）。</summary>
internal enum AuditWriteOutcome
{
    Written,    // 已即时落盘
    Buffered,   // 已进入待写队列（降级）
    Failed      // 按策略应拒绝（Deny）或已永久不可用
}

/// <summary>
/// 审计存储（6.1.6，规范性）+ 审计失败策略（2.6.1/2.6.2）。
/// - 存储位置：当前用户 %LOCALAPPDATA%\Perisc\Safety\audit\；按日分片 audit-YYYYMMDD.jsonl，只追加；
/// - 完整性：同目录校验文件 audit-YYYYMMDD.sha256（逐行记录字节区间哈希），启动时与补写前重读比对；
/// - Buffer 待写队列：内存 + 追加式落盘（audit-buffer.jsonl），恢复后按原顺序补写（保留原始 time、附加 backfilledAt）；
/// - 永久不可用判定（2.6.2）：连续 3 次失败 / 持续 30 s 未写入 / 队列满且补写失败 / 完整性校验失败。
/// <para>
/// <b>并发结构（本次改造的要点）</b>：两把锁，职责分开——
/// </para>
/// <list type="bullet">
/// <item><c>_io</c>：独占"对审计目录的一切磁盘访问"（追加、校验文件、待写日志、导出、清空、读取）。
/// 用信号量而非 lock，是为了让磁盘等待**不占用状态锁**；</item>
/// <item><c>_gate</c>：只保护内存状态（待写队列、失败计数、降级时刻、永久不可用标记）。</item>
/// </list>
/// <para>
/// <b>锁序铁律：先 <c>_io</c>、后 <c>_gate</c>，绝不反向</b>。
/// <c>SemaphoreSlim</c> 不可重入，反向嵌套会自锁；且"持状态锁等磁盘"正是要消除的形态（9.6.4 / D-28）。
/// 因此所有磁盘动作都在 <c>_gate</c> 之外进行，状态变更只在 <c>_gate</c> 内进行，两者不交叠。
/// </para>
/// <para>
/// <b>为什么仍然同步落盘（不得改为异步）</b>：4.4 要求审计在同一调用栈内完成、
/// 不得异步滞后到进程退出；2.6.1/2.6.2 的降级、升级与"永久不可用"判定依赖**本次**写入的
/// 即时结果（<see cref="TryAppend"/> 的返回值）。把写入搬到后台队列会同时破坏这两条。
/// 本类做的是把 I/O 移出状态锁、缩短临界区，而不是把写入改成异步。
/// </para>
/// <para>
/// <b>文件构成（9.6.4 / D-06）</b>：本类型按职责拆成同目录的 partial——
/// 本文件（字段、构造、初始化、状态查询、释放）、<c>AuditStore.Write.cs</c>（写入与补写）、
/// <c>AuditStore.Query.cs</c>（查询 / 导出 / 清空）、<c>AuditStore.Maintenance.cs</c>（元记录、待写日志、升级）。
/// 字节级的搬运（分片与校验文件、待写日志的实际读写）与 7.4 格式各自独立成
/// <see cref="AuditShardWriter"/> 与 <see cref="AuditJson"/>：它们的变更理由与"策略"无关。
/// </para>
/// </summary>
internal sealed partial class AuditStore : IDisposable
{
    private const int MaxQueueEntries = 10000;          // 12.2 Buffer 待写队列容量（条）
    private const long MaxQueueBytes = 16L * 1024 * 1024;
    private const int InFlightWaitMs = 1000;            // Dispose 等在途补写退出的上限（见 Dispose）
    private static readonly TimeSpan PermanentAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryPeriod = TimeSpan.FromSeconds(3);

    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly object _gate = new();

    private readonly AuditFailurePolicy _policy;
    private readonly ISafetyClock _clock;
    private readonly Func<string> _programId;

    /// <summary>分片与校验文件的字节级读写（不含任何锁：闸门由本类持有，见 <see cref="AuditShardWriter"/>）。</summary>
    private readonly AuditShardWriter _shards;

    private readonly Queue<(string Line, AuditRecord Record)> _pending = new();
    private long _pendingBytes;
    private DateTimeOffset? _degradedSince;
    private int _consecutiveFailures;
    private bool _permanentUnavailable;
    private int _degradationEvents;      // 本会话内累计降级次数（2.6.1 Escalate 的判定依据）
    private bool _escalationRaised;
    private int _damagedLines;           // 解析失败的行数（D-18：损坏行必须可见，不得静默消失）
    private bool _damageReported;

    /// <summary>
    /// 补写重入闸（定时器周期短于补写耗时时的保护）。
    /// <para>
    /// 用 <c>_gate</c> 保护而不是 <c>Interlocked</c>：它与 <see cref="_disposed"/> 必须**同锁判定**，
    /// 否则 <see cref="Dispose"/> 会与"刚通过 <c>_disposed</c> 检查、尚未置位"的回调错身而过。
    /// </para>
    /// </summary>
    private bool _backfilling;
    private bool _disposed;
    private Timer? _retryTimer;

    /// <summary>
    /// 按 <see cref="AuditFailurePolicy.Escalate"/> 升级（2.6.1 同一会话内第二次降级）时触发，
    /// 供宿主/守护处置；在持任何锁的路径之外回调。
    /// </summary>
    internal event Action<string>? Escalated;

    /// <summary>
    /// 构造：**只建字段，不碰磁盘**。目录创建、完整性校验与接管遗留待写文件都在
    /// <see cref="Initialize"/> 里做——否则调用方（<c>SafetyProcessState.EnsureAudit</c>）
    /// 会在自己的锁内做多次磁盘读，这正是 D-28 的典型形态。
    /// </summary>
    public AuditStore(AuditFailurePolicy policy, ISafetyClock clock, Func<string> programId)
        : this(policy, clock, programId, directory: null)
    {
    }

    /// <summary>
    /// 同上，但把落点显式指到 <paramref name="directory"/>。
    /// <para>
    /// 仅供启动自检：自检要在一个进程内反复建/销毁审计存储、并故意往分片里掺损坏行，
    /// 落点必须是临时目录——否则会污染用户真实的 %LOCALAPPDATA% 审计数据，
    /// 而且"掺坏一行"会变成一次真实的完整性破坏。
    /// </para>
    /// </summary>
    public AuditStore(AuditFailurePolicy policy, ISafetyClock clock, Func<string> programId, string? directory)
    {
        _policy = policy;
        _clock = clock;
        _programId = programId;
        _shards = new AuditShardWriter(directory ?? DefaultDirectory);
    }

    /// <summary>审计目录（6.1.6 参考路径）。</summary>
    public string Directory => _shards.Directory;

    private static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Perisc", "Safety", "audit");

    /// <summary>
    /// 初始化（幂等）：建目录 → 校验当日分片完整性 → 接管遗留待写队列 → 起补写定时器。
    /// **调用方不得持有任何锁**（本方法内部会取 <c>_io</c> 做磁盘访问）。
    /// </summary>
    public void Initialize()
    {
        var permanentReason = string.Empty;

        _io.Wait();
        try
        {
            _shards.EnsureDirectory();

            if (_permanentUnavailable)
            {
                return;
            }

            if (!_shards.VerifyIntegrity(TodayStamp()))
            {
                // 永久不可用的判定在锁内做（纯状态），但**写升级记录必须出锁**——
                // _io 不可重入，在持 _io 时写记录会自锁。故此处只记原因，出锁后再处置。
                permanentReason = "integrity-failed-at-startup";
            }
            else
            {
                // 启动时接管遗留的待写队列文件（4.10 第 5 条：Dispose/崩溃不丢记录）。
                AdoptPendingFromDiskLocked();
            }
        }
        finally
        {
            _io.Release();
        }

        if (permanentReason.Length > 0)
        {
            MarkPermanent(permanentReason);
            return;
        }

        lock (_gate)
        {
            if (_disposed || _permanentUnavailable || _retryTimer is not null)
            {
                return;
            }
            _retryTimer = new Timer(_ => TryBackfill(), null, RetryPeriod, RetryPeriod);
        }
    }

    /// <summary>待写队列未清空（6.1.4 Degraded 条件之一）。只读内存状态，不受磁盘是否缓慢影响。</summary>
    public bool HasPendingBacklog
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count > 0 || _degradedSince is not null || _permanentUnavailable;
            }
        }
    }

    /// <summary>审计降级中（供界面提示）。只读内存状态。</summary>
    public bool Degraded
    {
        get
        {
            lock (_gate)
            {
                return _degradedSince is not null || _permanentUnavailable;
            }
        }
    }

    /// <summary>待补写条数（供界面展示）。只读内存状态。</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>
    /// 解析失败的审计行数（本进程内累计）。
    /// 存在的理由：这些行来自**可被篡改的外部文件**，跳过它们本身是对的（不能因一行坏数据让整次查询失败），
    /// 但"跳过了多少行"必须能被看到，否则篡改与损坏都无从察觉。
    /// </summary>
    public int DamagedLines => Volatile.Read(ref _damagedLines);

    /// <summary>
    /// 释放：停补写定时器，并等在途的那一次补写退出（上限 <see cref="InFlightWaitMs"/>）。
    /// <para>
    /// 4.10 第 5 条：**Dispose 不撤销许可、不删除审计、不丢弃待写队列**。
    /// 因此这里只停表；待写队列保留在内存与磁盘上，供下次启动接管。
    /// </para>
    /// <para>
    /// <b>为什么只等上限、不等到底</b>：<c>Timer.Dispose()</c> 不等待在途回调，
    /// 所以"Dispose 返回后不会再有线程碰本对象"需要额外保证。
    /// 这里的保证分两层——正确性靠 <c>_disposed</c> 与 <c>_backfilling</c> 的同锁判定
    /// （<see cref="TryBackfill"/> 不会再启动新的补写），
    /// 礼节性靠这段有上限的等待（已经开跑的那一次尽量等它写完）。
    /// 超过上限就放它自己跑完：它只写文件、只取 <c>_io</c>，而 <c>_io</c> 本就不释放（见下），
    /// 所以"写到一个已被 Dispose 的审计存储上"是内存安全的，不会碰到已释放的句柄。
    /// </para>
    /// </summary>
    public void Dispose()
    {
        Timer? timer;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            timer = _retryTimer;
            _retryTimer = null;
        }
        timer?.Dispose();

        // 等补写落地。SpinOnce 会先空转、若干次后自行降级为 1ms 让出，行为与
        // 「睡一小会儿再重查」等价，却不必在库层留下 Thread.Sleep（D-13）。
        // 上限不变：SpinWait 只影响让出方式，不影响 deadline 判定。
        var spin = new SpinWait();
        var deadline = Environment.TickCount64 + InFlightWaitMs;
        while (Environment.TickCount64 < deadline)
        {
            lock (_gate)
            {
                if (!_backfilling)
                {
                    break;
                }
            }
            spin.SpinOnce();
        }

        // _io 不显式释放：SemaphoreSlim 不申请非托管句柄（未访问 AvailableWaitHandle 时），
        // 由 GC 回收即无泄漏（9.6.4 / D-11c 明文：纯托管 IDisposable 未 Dispose 不等于泄漏）。
        // 反而不可在此释放——在途的补写可能正持有它，释放会抛 ObjectDisposedException。
        // 这条是《设计取舍》里登记的"已知妥协：纯托管对象依赖 GC"在本类的唯一落点。
    }
}
