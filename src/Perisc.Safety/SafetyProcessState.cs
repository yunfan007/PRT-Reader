using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Perisc.Safety;

/// <summary>
/// 进程内共享运行时（实现内部）：程序标识、许可存储、审计存储、频控与状态归集。
/// 模块一（SafetyClient）与模块二（InterceptClient）共用同一实例，保证许可缓存
/// 与审计在两层之间一致（2.3：不得对已获 allow 且在有效期内的行为重复阻断）。
/// <para>
/// <b>释放契约（9.6.4 / D-12）</b>：本类是审计存储的持有者，而审计存储持有补写定时器。
/// 定时器会经定时器队列把自身挂成 GC 根，不 Dispose 就是真实泄漏（不是"等 GC 回收"那类）。
/// 因此本类实现 <see cref="IDisposable"/>，由 <see cref="SafetyEnvironment"/> 在
/// 最后一份持有归还、或宿主显式关闭环境时调用。
/// </para>
/// </summary>
internal sealed class SafetyProcessState : IDisposable
{
    private readonly object _gate = new();
    private int _askTimestampsCount;
    private readonly long[] _askTicks = new long[128]; // 环形记录近 128 次申报时刻（按 2.1 硬上限 100 次/秒取容量）
    private int _askIndex;

    /// <summary><see cref="TryReserveAskSlot"/> 的窗口上限，由客户端按 2.1 声明（默认 10 次/秒）。</summary>
    private int _askRateLimitPerSecond = SafetyOptions.DefaultAskRateLimitPerSecond;

    private bool _guardUnavailable;
    private bool _disposed;

    // 审计降级已升级（2.6.1 Escalate）：用无锁字段承接审计存储的回调，避免与审计存储的锁形成环。
    private volatile bool _auditEscalated;

    /// <summary>
    /// 运行时的唯一构造入口由 <see cref="SafetyEnvironment"/> 把持：不再有可被任意改写的静态属性，
    /// 进程内共享同一实例的要求（2.3）由环境持有者保证，而不是靠"到处都是的静态字段"保证。
    /// </summary>
    internal SafetyProcessState(ISafetyClock clock)
    {
        Clock = clock;
        Permits = new PermitStore(() => clock.Now());
    }

    /// <summary>注入的时钟；运行时内**不得**再直接读系统时间。</summary>
    internal ISafetyClock Clock { get; }

    public string ProgramId { get; private set; } = "unknown-program";
    public PermitStore Permits { get; }
    public AuditStore? Audit { get; private set; }
    public AuditFailurePolicy AuditPolicy { get; private set; } = AuditFailurePolicy.Buffer;

    /// <summary>登记程序标识（首次生效；guard.connect 或首次 Create 时调用）。</summary>
    public void RegisterProgram(string programId)
    {
        if (string.IsNullOrWhiteSpace(programId))
        {
            return;
        }
        lock (_gate)
        {
            if (ProgramId == "unknown-program")
            {
                ProgramId = programId.Trim();
            }
        }
    }

    /// <summary>
    /// 初始化审计存储（按首次 SafetyOptions 的策略）；重复调用忽略。
    /// </summary>
    /// <param name="policy">审计写入失败时的策略（2.6.1）。</param>
    /// <param name="directory">
    /// 落点覆盖；null 表示用 <see cref="AuditStore"/> 的标准路径。
    /// 由 <see cref="SafetyEnvironment.AuditDirectory"/> 传入——只有在隔离环境里才非空，
    /// 目的与 <see cref="AuditStore"/> 的同名构造参数一致：自检不得写到用户的真实审计数据上。
    /// </param>
    public void EnsureAudit(AuditFailurePolicy policy, string? directory = null)
    {
        AuditStore? created = null;
        lock (_gate)
        {
            if (_disposed)
            {
                // 已拆除的环境不再建立新的审计存储：否则会起一个没人负责停的补写定时器。
                return;
            }
            if (Audit is null)
            {
                // 构造只建字段。目录创建、完整性校验、接管遗留待写文件都要读盘，
                // 一律推到锁外（AuditStore.Initialize）——这是 D-28 在本类上的全部修复点。
                created = new AuditStore(policy, Clock, () => ProgramId, directory);
                Audit = created;
            }
            AuditPolicy = policy;
        }

        if (created is not null)
        {
            // 2.6.1 Escalate：同一会话内第二次降级升级为 Escalation —— 状态随即降级（6.1.4）。
            created.Escalated += _ => _auditEscalated = true;
            created.Initialize();
        }
    }

    /// <summary>守护连接状态标记（6.1.4：Degraded 条件之一）。</summary>
    public void SetGuardUnavailable(bool unavailable)
    {
        lock (_gate)
        {
            _guardUnavailable = unavailable;
        }
    }

    /// <summary>当前模块状态（6.1.4）：Unavailable 仅在库自身不可用时出现（本实现编译期内建，不会出现）。</summary>
    public ModuleState State
    {
        get
        {
            var audit = Audit;
            if (audit is not null && audit.HasPendingBacklog)
            {
                return ModuleState.Degraded;
            }
            if (_auditEscalated)
            {
                return ModuleState.Degraded;
            }
            lock (_gate)
            {
                if (_guardUnavailable)
                {
                    return ModuleState.Degraded;
                }
            }
            return ModuleState.Ready;
        }
    }

    /// <summary>申报频控（2.1：默认 10 次/秒，硬上限 100 次/秒）。超限返回 false。</summary>
    public bool TryReserveAskSlot()
    {
        lock (_gate)
        {
            // 用注入的时钟，而不是 Environment.TickCount64：限流窗口需要一个**单调**的时刻源，
            // 而注入时钟按契约保证单调（MonotonicClock），同时让自检能在一个进程内验证限流。
            var now = Clock.Now().ToUnixTimeMilliseconds();
            var windowStart = now - 1000;
            var count = 0;
            for (var i = 0; i < _askTicks.Length; i++)
            {
                if (_askTicks[i] >= windowStart)
                {
                    count++;
                }
            }
            if (count >= _askRateLimitPerSecond)
            {
                return false;
            }
            _askTicks[_askIndex % _askTicks.Length] = now;
            _askIndex++;
            _askTimestampsCount++;
            return true;
        }
    }

    /// <summary>
    /// 声明本进程的申报频率上限（2.1：默认 10 次/秒、硬上限 100 次/秒）。
    /// <para>
    /// 取<b>已声明值中的最大值</b>，并一律按 2.1 的硬上限截断（100）。
    /// 为什么取最大而不是取最小：这个值表达的是"本程序声称自己确实会用到多大节拍"，
    /// 而运行时为进程级共享（2.3）——一个客户端声明得高，说明这个程序确实有高节拍的路径；
    /// 按最小生效会让后建的保守客户端把先前声明的高节拍场景按回默认值，
    /// 于是"声明了却仍然被限流"，比不声明更糟（声明与实际不符）。
    /// 在正常的本程序里只有一个客户端（界面路径）或一个自检客户端，两者不会并存。
    /// </para>
    /// </summary>
    public void DeclareAskRateLimit(int perSecond)
    {
        var declared = Math.Clamp(perSecond, 1, SafetyOptions.MaxAskRateLimitPerSecond);
        lock (_gate)
        {
            if (declared > _askRateLimitPerSecond)
            {
                _askRateLimitPerSecond = declared;
            }
        }
    }

    /// <summary>
    /// 取当前时刻。6.1.3 的"回拨不得延长许可"由注入的时钟承担（见 <see cref="MonotonicClock"/>）。
    /// <para>
    /// 改造前这里自己读系统时间并维护观察最大值——那样只有本运行时被保护，
    /// 而审计分片命名、守护心跳等位置仍各读各的时钟，保护并不完整。
    /// </para>
    /// </summary>
    public DateTimeOffset Now() => Clock.Now();

    /// <summary>记录 requestId 与 (Action, Target) 的关联（6.1.7 AuditRecord.For 回填用）。</summary>
    public void RememberRequest(string requestId, string action, string target)
        => Permits.RememberRequest(requestId, action, target);

    /// <summary>查某次申报的行为与对象（无关联返回空串）。</summary>
    public (string Action, string Target) LookupLastRequest(string requestId)
        => Permits.LookupRequest(requestId);

    /// <summary>
    /// 拆除运行时：放下审计存储（含补写定时器）。
    /// <para>
    /// 幂等；可在任意线程调用。**不撤销许可**（4.10 第 5 条：Dispose 不得撤销已有许可），
    /// 也不删除审计、不丢弃待写队列——那三件事与"释放资源"是两回事，
    /// 只有 <c>RevokeAll</c> / <c>Purge</c> 这类显式接口才做。
    /// </para>
    /// <para>
    /// 审计存储的 <c>Dispose</c> 在锁**外**调用：它要等在途的补写循环退出（有上限），
    /// 持 <c>_gate</c> 干等会把状态查询整条堵住——正是本改造要消除的形态。
    /// </para>
    /// </summary>
    public void Dispose()
    {
        AuditStore? audit;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            audit = Audit;
            Audit = null;
        }
        audit?.Dispose();
    }
}

/// <summary>
/// 许可存储（6.1.3，规范性）：权威计数在模块；单次许可（MaxUses=null）首次 Allow 后即用尽；
/// 过期判定用运行时的 Now()（含时钟回拨保护）；撤销/到期/用尽触发 PermitChanged。
/// </summary>
internal sealed class PermitStore
{
    // 4.2 / 4.7 类型约定：一律普通类，不用 record / required / init（required 虽是编译期特性，
    // 不属反射可见的成员属性，但明文禁止即不得使用）。
    private sealed class Entry
    {
        public PermitScope Scope = null!;
        public string KeyAction = string.Empty;
        public string KeyTarget = string.Empty;
        public MatchMode KeyMode;
        public bool SingleUse;
    }

    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, Entry> _byRequestId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _keyToRequestId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Action, string Target)> _requestFacts = new(StringComparer.Ordinal);

    /// <summary>构造：取时点由运行时注入（不再于内部直接读系统时间）。</summary>
    public PermitStore(Func<DateTimeOffset> now) => _now = now;

    /// <summary>许可被撤销/到期/用尽（6.1.4）；由 SafetyClient 转发为 PermitChanged。</summary>
    public event EventHandler<PermitChangedEventArgs>? PermitEnded;

    /// <summary>当前有效许可数。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byRequestId.Count;
            }
        }
    }

    /// <summary>是否已有该 (Action + Target + MatchMode) 的有效许可（4.3 第 4 条缓存命中）。</summary>
    public bool TryGetActivePermit(string action, string target, MatchMode mode, Func<DateTimeOffset> now, out string requestId, out PermitScope? scope)
    {
        var found = false;
        var rid = string.Empty;
        PermitScope? snapshot = null;
        List<PermitChangedEventArgs>? ended;
        lock (_gate)
        {
            // 先惰性清理过期项（清理本身只改内存状态，事件留到锁外发）。
            ended = ExpireLocked(now());

            if (_keyToRequestId.TryGetValue(Key(action, target, mode), out var key) &&
                _byRequestId.TryGetValue(key, out var entry))
            {
                rid = key;
                snapshot = CloneScope(entry.Scope);
                found = true;
            }
        }

        RaiseEnded(ended);
        requestId = rid;
        scope = snapshot;
        return found;
    }

    /// <summary>按 requestId 查状态；未知/失效返回 Deny + null（6.1.2，不返回 null、不抛异常）。</summary>
    public RequestStatus QueryStatus(string requestId, Func<DateTimeOffset> now)
    {
        RequestStatus status;
        List<PermitChangedEventArgs>? ended;
        lock (_gate)
        {
            ended = ExpireLocked(now());
            status = _byRequestId.TryGetValue(requestId, out var entry)
                ? new RequestStatus(requestId, DecisionStatus.Allow, CloneScope(entry.Scope))
                : new RequestStatus(requestId, DecisionStatus.Deny, null);
        }

        RaiseEnded(ended);
        return status;
    }

    /// <summary>登记新许可。超过 64 项（12.2）返回 false（由调用方返回 Limit）。</summary>
    public bool TryAdd(string requestId, string action, string target, MatchMode mode, PermitScope scope, bool singleUse)
    {
        lock (_gate)
        {
            if (_byRequestId.Count >= 64)
            {
                return false;
            }

            var entry = new Entry
            {
                Scope = scope,
                KeyAction = action,
                KeyTarget = target,
                KeyMode = mode,
                SingleUse = singleUse,
            };
            _byRequestId[requestId] = entry;
            _keyToRequestId[Key(action, target, mode)] = requestId;
            _requestFacts[requestId] = (action, target);
            return true;
        }
    }

    /// <summary>许可获得 Allow（含缓存命中）后扣减 1（6.1.3）；用尽即移除并触发事件。</summary>
    /// <returns>扣减后的许可范围快照；未知 id 返回 null。</returns>
    public PermitScope? Consume(string requestId, Func<DateTimeOffset> now)
    {
        PermitChangedEventArgs? ended = null;
        PermitScope? snapshot = null;
        lock (_gate)
        {
            if (_byRequestId.TryGetValue(requestId, out var entry))
            {
                entry.Scope.RemainingUses -= 1;
                snapshot = CloneScope(entry.Scope);
                if (entry.Scope.RemainingUses <= 0)
                {
                    RemoveLocked(requestId, entry);
                    ended = new PermitChangedEventArgs(requestId, DecisionStatus.Deny);
                }
            }
        }
        if (ended is not null)
        {
            RaiseEnded(ended);
        }
        return snapshot;
    }

    /// <summary>撤销许可（6.1.3）：生效后立即失效并触发事件；未知 id 为幂等 no-op。</summary>
    public bool Revoke(string requestId, Func<DateTimeOffset> now)
    {
        PermitChangedEventArgs? ended = null;
        bool existed;
        lock (_gate)
        {
            existed = _byRequestId.TryGetValue(requestId, out var entry);
            if (existed)
            {
                RemoveLocked(requestId, entry!);
                ended = new PermitChangedEventArgs(requestId, DecisionStatus.Deny);
            }
        }
        if (ended is not null)
        {
            RaiseEnded(ended);
        }
        return existed;
    }

    public void RememberRequest(string requestId, string action, string target)
    {
        lock (_gate)
        {
            _requestFacts[requestId] = (action, target);
        }
    }

    public (string Action, string Target) LookupRequest(string requestId)
    {
        lock (_gate)
        {
            if (_requestFacts.TryGetValue(requestId, out var fact))
            {
                return fact;
            }
        }
        return (string.Empty, string.Empty);
    }

    /// <summary>清空全部许可（模块界面撤销全部；Dispose 不得撤销许可，此方法仅供界面）。</summary>
    public void RevokeAll(Func<DateTimeOffset> now)
    {
        List<PermitChangedEventArgs> ended = new();
        lock (_gate)
        {
            foreach (var rid in new List<string>(_byRequestId.Keys))
            {
                if (_byRequestId.TryGetValue(rid, out var entry))
                {
                    RemoveLocked(rid, entry);
                    ended.Add(new PermitChangedEventArgs(rid, DecisionStatus.Deny));
                }
            }
        }
        foreach (var e in ended)
        {
            RaiseEnded(e);
        }
    }

    public IEnumerable<PermitScope> Snapshot()
    {
        List<PermitScope> list;
        List<PermitChangedEventArgs>? ended;
        lock (_gate)
        {
            ended = ExpireLocked(_now());
            list = new List<PermitScope>(_byRequestId.Count);
            foreach (var entry in _byRequestId.Values)
            {
                list.Add(CloneScope(entry.Scope));
            }
        }

        RaiseEnded(ended);
        return list;
    }

    /// <summary>带 requestId 的许可快照（模块管理界面展示 / 撤销用）。</summary>
    public IReadOnlyList<(string RequestId, PermitScope Scope)> SnapshotWithIds()
    {
        List<(string, PermitScope)> list;
        List<PermitChangedEventArgs>? ended;
        lock (_gate)
        {
            ended = ExpireLocked(_now());
            list = new List<(string, PermitScope)>(_byRequestId.Count);
            foreach (var (rid, entry) in _byRequestId)
            {
                list.Add((rid, CloneScope(entry.Scope)));
            }
        }

        RaiseEnded(ended);
        return list;
    }

    // ───────────────────────────── 内部 ─────────────────────────────

    private static string Key(string action, string target, MatchMode mode) => action + "\u0001" + target + "\u0001" + mode;

    /// <summary>
    /// 交出许可范围的**快照**（6.1.3）：权威计数保存在模块内部（<c>Entry.Scope</c>），
    /// 归还给调用方的必须是调用时刻的值，否则调用方持有的 <c>Decision.Scope</c> 会在后续
    /// 扣减中被悄悄改写（4.7 的「对外只读」在语义上被绕过）。
    /// <para>
    /// 为什么叫 <c>CloneScope</c> 而不叫 <c>Copy</c>：本方法在 <c>_gate</c> 内被多处调用，
    /// 而 D-28 的静态检查按<b>方法名</b>沿一跳调用链判断「锁内是否触达磁盘」——
    /// 模块四的 <c>SafeFile.Copy</c>（真的调 <c>File.Copy</c>）一旦与它同名，
    /// 这些锁就会被误判成"锁内做磁盘 I/O"。改名消除这次同名碰撞；
    /// 顺带这个名字也更准：它交出的是范围快照，不是文件复制。
    /// </para>
    /// </summary>
    internal static PermitScope CloneScope(PermitScope scope)
        => new(scope.Action, scope.Target, scope.MatchMode, scope.ExpiresAt, scope.RemainingUses);

    /// <summary>
    /// 清理已过期的许可（**调用方须已持 <c>_gate</c>**）。
    /// <para>
    /// 只做状态变更、**不触发事件**，把被移除的许可作为返回值交出去，
    /// 由调用方在锁外统一触发 <see cref="PermitEnded"/>。
    /// </para>
    /// <para>
    /// 这一点是本次改造修正的：原实现在本方法内部直接 <c>RaiseEnded</c>，
    /// 而本方法又总是由 <c>TryGetActivePermit</c> / <c>QueryStatus</c> / <c>Snapshot</c> /
    /// <c>SnapshotWithIds</c> 在 <c>_gate</c> 内调用——于是事件实际是**持锁触发**的。
    /// 订阅方若在自己的回调里回头查许可（非常自然的写法），就会撞上不可重入的 <c>lock</c>。
    /// </para>
    /// </summary>
    private List<PermitChangedEventArgs>? ExpireLocked(DateTimeOffset now)
    {
        List<PermitChangedEventArgs>? ended = null;
        List<string>? dead = null;
        foreach (var (rid, entry) in _byRequestId)
        {
            if (now >= entry.Scope.ExpiresAt)
            {
                (dead ??= new List<string>()).Add(rid);
                (ended ??= new List<PermitChangedEventArgs>()).Add(new PermitChangedEventArgs(rid, DecisionStatus.Deny));
            }
        }
        if (dead is not null)
        {
            foreach (var rid in dead)
            {
                RemoveLocked(rid, _byRequestId[rid]);
            }
        }
        return ended;
    }

    private void RemoveLocked(string requestId, Entry entry)
    {
        _byRequestId.Remove(requestId);
        _keyToRequestId.Remove(Key(entry.KeyAction, entry.KeyTarget, entry.KeyMode));
    }

    /// <summary>批量触发（锁外）：<see cref="ExpireLocked"/> 交回来的清理结果在这里统一发出去。</summary>
    private void RaiseEnded(List<PermitChangedEventArgs>? ended)
    {
        if (ended is null)
        {
            return;
        }
        foreach (var e in ended)
        {
            RaiseEnded(e);
        }
    }

    private void RaiseEnded(PermitChangedEventArgs e)
    {
        var handler = PermitEnded;
        // 6.1.4：不得在持锁状态或裁决调用栈内同步触发。
        // 本类的调用点已全部保证：状态变更在 _gate 内、事件在 _gate 外（由调用方统一发出）。
        handler?.Invoke(this, e);
    }
}
