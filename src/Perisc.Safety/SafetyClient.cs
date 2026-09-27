using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Perisc.Safety;

/// <summary>
/// 模块一（ARM）：申报与审计入口（4.3，规范性）。
/// 全部公开成员以返回值表达结果，不抛异常（4.10 第 1 条；编程错误除外）。
/// </summary>
public sealed class SafetyClient : IDisposable
{
    /// <summary>
    /// 模块 API 版本（7.1）：遵循 PSS 标准 v7.3 的 API 契约 **v1.1**。
    /// <para>
    /// v1.0 → v1.1 属<strong>次版本</strong>（7.1）：只做「澄清 + 兼容标注」——同步 <see cref="Ask"/>
    /// 在 4.3 中标明「兼容保留，新代码应使用 <see cref="AskAsync"/>」，签名与语义均不变。
    /// </para>
    /// </summary>
    private static readonly Version ApiVer = new(1, 1);

    /// <summary>Ask 的默认超时（4.3 第 3 条）：5 秒；超时按 <c>Deny + Timeout</c> 返回。</summary>
    private static readonly TimeSpan DefaultAskTimeout = TimeSpan.FromSeconds(5);

    private readonly SafetyOptions _options;
    private readonly SafetyProcessState _runtime;
    private readonly SafetyUiHost _uiHost;
    private readonly object _gate = new();
    private readonly Dictionary<string, Decision> _decisions = new(StringComparer.Ordinal);
    private bool _disposed;

    private SafetyClient(SafetyOptions options, SafetyProcessState runtime)
    {
        _options = options;
        _runtime = runtime;
        // 界面挂钩随客户端实例持有（9.6.4 / D-07）：未注入时给一个空宿主，
        // 后续 Consult 取到 null handler 即按"用户未决定"拒绝（fail-closed）。
        _uiHost = options.UiHost ?? new SafetyUiHost();
        _runtime.Permits.PermitEnded += OnPermitEnded;
    }

    public static SafetyClient Create(SafetyOptions? options = null)
    {
        var opts = options ?? new SafetyOptions();
        // 运行时的建立走显式入口：不再有可被任意位置改写的静态属性（D-07），
        // 进程内共享同一实例的要求（2.3）由 SafetyEnvironment 保证。
        // Acquire 同时**记一份持有**：本客户端 Dispose 时归还，最后一个持有者离开即拆除审计存储
        // （D-12：审计存储持有补写定时器，不能靠进程退出兜底）。
        var runtime = SafetyEnvironment.Acquire();
        // 落点取自环境（隔离环境内为临时目录，进程环境内为标准路径）：
        // 自检在隔离环境里建客户端时，审计不会写到用户的真实数据上。
        runtime.EnsureAudit(opts.AuditFailurePolicy, SafetyEnvironment.AuditDirectory);
        // 申报频率（2.1）：客户端声明，作用于**进程级**的频控——运行时按 2.3 是进程共享的，
        // 「申报频率」这一限额也因此是每程序一份，不是每客户端一份。
        runtime.DeclareAskRateLimit(opts.AskRateLimitPerSecond);
        if (!string.IsNullOrWhiteSpace(opts.ProgramId))
        {
            runtime.RegisterProgram(opts.ProgramId);
        }
        return new SafetyClient(opts, runtime);
    }

    public static Task<SafetyClient> CreateAsync(SafetyOptions? options = null, CancellationToken cancellationToken = default)
    {
        // 本实现的模块一/二为编译期内建，无需等待加载；同步路径即完成（4.10 第 9 条允许）。
        return Task.FromResult(Create(options));
    }

    // ───────────────────────────── 申报 ─────────────────────────────

    /// <summary>
    /// 同步申报（4.3 第 3 条：在调用线程上等待答复）。
    /// <para>
    /// <b>不得在 UI 线程调用</b>：界面宿主的答复需要回到 UI 线程完成交互，
    /// 在 UI 线程上同步等待等于等待自己（4.3 第 3 条与界面宿主的天然冲突，见《设计取舍》）。
    /// 界面程序一律用 <see cref="AskAsync"/>；本重载保留给控制台 / 服务类宿主与自检。
    /// </para>
    /// </summary>
    public Decision Ask(AskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AskCoreAsync(request, CancellationToken.None).GetAwaiter().GetResult();
    }

    public Task<Decision> AskAsync(AskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // 取消表现为 Deny + Timeout，不抛 OperationCanceledException（4.3 第 7 条 / 4.10 第 6 条）。
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(new Decision(DecisionStatus.Deny, PssCode.Timeout, "申报已取消（按超时拒绝）", AssignRequestId(request)));
        }
        // 直接返回异步内核，**不再用 Task.Run 包一层**（9.6.4 / D-13）：
        // 旧写法把同步内核塞进线程池，只买到"不阻塞调用线程"，代价是取消、超时与
        // 异常传播都要绕过一层外壳，而且调用方拿到的是一个"假的异步"——它内部仍会阻塞一个线程池线程。
        return AskCoreAsync(request, cancellationToken);
    }

    public IReadOnlyList<Decision> AskBatch(IReadOnlyList<AskRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        return AskBatchCoreAsync(requests, CancellationToken.None).GetAwaiter().GetResult();
    }

    public Task<IReadOnlyList<Decision>> AskBatchAsync(IReadOnlyList<AskRequest> requests, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (cancellationToken.IsCancellationRequested)
        {
            var batch = new Decision[requests.Count];
            for (var i = 0; i < batch.Length; i++)
            {
                batch[i] = new Decision(DecisionStatus.Deny, PssCode.Timeout, "批量申报已取消（按超时拒绝）", AssignRequestId(requests[i]));
            }
            return Task.FromResult<IReadOnlyList<Decision>>(batch);
        }
        return AskBatchCoreAsync(requests, cancellationToken);
    }

    private async Task<IReadOnlyList<Decision>> AskBatchCoreAsync(IReadOnlyList<AskRequest> requests, CancellationToken cancellationToken)
    {
        // 4.3.1：逐项裁决、一次交互、部分失败互不影响、批内去重、超限整批 Limit。
        if (requests.Count > 16)
        {
            var over = new Decision[requests.Count];
            for (var i = 0; i < over.Length; i++)
            {
                over[i] = new Decision(DecisionStatus.Deny, PssCode.Limit,
                    $"单批不得超过 16 项（实际 {requests.Count} 项），整批拒绝", AssignRequestId(requests[i]));
            }
            return over;
        }

        var results = new Decision[requests.Count];

        // 批内去重（4.3.1）：同 (Action + Target + MatchMode) 合并为一组，
        // 只做一次裁决、只建一份许可、只弹一条卡片；组内每个输入项返回同一结果对象。
        var groups = new List<BatchGroup>();
        var groupIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < requests.Count; i++)
        {
            if (requests[i] is null)
            {
                throw new ArgumentNullException(nameof(requests), $"第 {i} 项为 null");
            }
            var request = requests[i];

            // 幂等与许可缓存先行。
            var cached = TryGetExistingDecision(request, out var decision);
            if (!cached && TryPermitCache(request, out decision))
            {
                cached = true;
            }
            if (cached)
            {
                results[i] = decision!;
                continue;
            }

            var key = GroupKey(request);
            if (groupIndex.TryGetValue(key, out var group))
            {
                groups[group].Members.Add(i);
                continue;
            }
            groupIndex[key] = groups.Count;
            groups.Add(new BatchGroup(i, new List<int> { i }));
        }

        // 未决组合并为一次用户交互（4.3.1：一次弹窗列出全部未决项）。
        if (groups.Count > 0)
        {
            var batch = new List<AskRequest>(groups.Count);
            foreach (var group in groups)
            {
                batch.Add(requests[group.Representative]);
            }

            // 这一步是整条申报链上唯一需要"等外部输入"的地方：只有它需要 await。
            // 其余的幂等、缓存、校验、频控都是纯内存判定，同步做完即可。
            var consult = await ConsultAsync(batch, cancellationToken).ConfigureAwait(false);
            for (var g = 0; g < groups.Count; g++)
            {
                var group = groups[g];
                var decision = BuildUserDecision(requests[group.Representative],
                    g < consult.Answers.Count ? consult.Answers[g] : null, consult.TimedOut);
                CommitDecision(requests[group.Representative], decision);

                // 同组每个输入项返回同一结果（4.3.1）；不再重复裁决、不重复扣减许可。
                foreach (var member in group.Members)
                {
                    results[member] = decision;
                }
            }
        }

        // 兜底：不应出现 null（防御式，返回 Internal 按 Deny 处理）。
        for (var i = 0; i < results.Length; i++)
        {
            results[i] ??= new Decision(DecisionStatus.Deny, PssCode.Internal, "批量裁决内部错误", AssignRequestId(requests[i]));
        }
        return results;
    }

    /// <summary>批内去重分组（4.3.1）：代表项下标 + 同键的全部输入项下标。</summary>
    private sealed class BatchGroup
    {
        public BatchGroup(int representative, List<int> members)
        {
            Representative = representative;
            Members = members;
        }

        public int Representative { get; }
        public List<int> Members { get; }
    }

    private static string GroupKey(AskRequest request)
        => request.Action + "\u0001" + Matching.Normalize(request.Target) + "\u0001" + PssText.FromMatchMode(request.MatchMode);

    private async Task<Decision> AskCoreAsync(AskRequest request, CancellationToken cancellationToken)
    {
        // ① 幂等：同一 RequestId 重复调用直接返回已授予结果（4.3 第 2 条）。
        if (request.RequestId is not null && TryGetExistingDecision(request, out var existing))
        {
            return existing;
        }

        // ② 许可缓存：同一 (Action+Target+MatchMode) 在有效期内直接返回已授予结果（4.3 第 4 条）。
        if (TryPermitCache(request, out var cachedDecision))
        {
            return cachedDecision;
        }

        // ③ 入参校验（6.1.1）：BadArg detail 必须指明是哪一条。
        var invalid = ValidateRequest(request);
        if (invalid is not null)
        {
            return Finish(request, new Decision(DecisionStatus.Deny, PssCode.BadArg, invalid, AssignRequestId(request)));
        }

        // ④ 审计降级闸门：待写队列未清空前不得接受新的许可申请（2.6.1）。
        var audit = _runtime.Audit;
        if (audit is not null && audit.HasPendingBacklog)
        {
            return Finish(request, new Decision(DecisionStatus.Deny, PssCode.Limit, "审计待写队列未清空，暂停接受新许可申请", AssignRequestId(request)));
        }

        // ⑤ 频控（12.2：10 次/秒）。
        if (!_runtime.TryReserveAskSlot())
        {
            return Finish(request, new Decision(DecisionStatus.Deny, PssCode.Limit, "申报频率过高（超过 10 次/秒）", AssignRequestId(request)));
        }

        // ⑥ 用户裁决（3.4 ask 语义）：无界面宿主时 fail-closed 按拒绝处理。
        var consult = await ConsultAsync(new[] { request }, cancellationToken).ConfigureAwait(false);
        var answer = consult.Answers.Count > 0 ? consult.Answers[0] : null;
        return Finish(request, BuildUserDecision(request, answer, consult.TimedOut));
    }

    /// <summary>一次询问的结果：答复集合 + 是否超时（超时按 Deny + Timeout 表达，4.3 第 3 条）。</summary>
    private sealed class ConsultResult
    {
        public ConsultResult(IReadOnlyList<SafetyUiHost.Answer?> answers, bool timedOut)
        {
            Answers = answers;
            TimedOut = timedOut;
        }

        public IReadOnlyList<SafetyUiHost.Answer?> Answers { get; }
        public bool TimedOut { get; }
    }

    /// <summary>
    /// 询问用户，并强制 4.3 第 3 条的超时（<see cref="SafetyOptions.AskTimeout"/>，默认 5 秒）：
    /// 超时或取消一律按 <c>Deny + Timeout</c> 返回。宿主未注入 handler 时返回全 null（=拒绝）。
    /// <para>
    /// 超时用 <see cref="CancellationTokenSource.CancelAfter"/> 表达，**不再用 <c>Task.Wait(timeout)</c>**：
    /// 后者会占住一个线程干等（9.6.4 / D-13/D-14），而超时本质上只是"等一个任务到点为止"。
    /// 差别在界面场景下是决定性的——占住 UI 线程等，界面就完全停住。
    /// </para>
    /// <para>
    /// 超时**不会**关闭已弹出的对话框：用户正在读的内容不该因为 2 分钟到点就被夺走。
    /// 库这一侧放弃等待并按拒绝处理，界面由宿主自行决定何时关（与改造前行为一致）。
    /// </para>
    /// </summary>
    private async Task<ConsultResult> ConsultAsync(IReadOnlyList<AskRequest> requests, CancellationToken cancellationToken)
    {
        var handler = _uiHost.AskHandler;
        if (handler is null)
        {
            return new ConsultResult(new SafetyUiHost.Answer?[requests.Count], timedOut: false);
        }

        var timeout = _options.AskTimeout ?? DefaultAskTimeout;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            var answers = await handler(requests, linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
            if (answers is null || answers.Count != requests.Count)
            {
                return new ConsultResult(new SafetyUiHost.Answer?[requests.Count], timedOut: false);
            }
            return new ConsultResult(answers, timedOut: false);
        }
        catch (OperationCanceledException)
        {
            // 用户未在限时内答复：按拒绝处理，且必须报告为超时（4.3 第 3 条 / 6.1.2）。
            return new ConsultResult(new SafetyUiHost.Answer?[requests.Count], timedOut: true);
        }
        catch
        {
            // 界面回调异常不得影响裁决（对齐 6.1.4 的事件异常处理思路）：按拒绝处理。
            return new ConsultResult(new SafetyUiHost.Answer?[requests.Count], timedOut: false);
        }
    }

    private Decision BuildUserDecision(AskRequest request, SafetyUiHost.Answer? answer, bool timedOut)
    {
        var requestId = AssignRequestId(request);
        if (timedOut)
        {
            return new Decision(DecisionStatus.Deny, PssCode.Timeout, "申报未在限时内返回，按拒绝处理", requestId);
        }
        if (answer is null || !answer.Allowed)
        {
            return new Decision(DecisionStatus.Deny, PssCode.Denied, "用户拒绝或未决定", requestId);
        }

        // 许可生命周期（6.1.3 / 4.7）：「单次许可」由 MaxUses 定义——MaxUses 为 null（或 Ttl 为 null）
        // 即单次，首次 Allow 后立即用尽；只有显式给出多于 1 次用量时才成为多次许可。
        var now = _runtime.Now();
        var ttl = request.Ttl ?? answer.Ttl;
        var declaredUses = request.MaxUses ?? answer.MaxUses;
        var singleUse = ttl is null || declaredUses is null || declaredUses.Value <= 1;
        var maxUses = singleUse ? 1 : declaredUses!.Value;
        var expiresAt = ttl is null ? now : now + ttl.Value;
        var scope = new PermitScope(request.Action, Matching.Normalize(request.Target), request.MatchMode, expiresAt, maxUses);
        if (!_runtime.Permits.TryAdd(requestId, request.Action, Matching.Normalize(request.Target), request.MatchMode, scope, singleUse))
        {
            return new Decision(DecisionStatus.Deny, PssCode.Limit, "同时持有的许可数已达上限（64）", requestId);
        }
        var granted = _runtime.Permits.Consume(requestId, _runtime.Now); // 首次 Allow 扣减 1
        // 归还快照而非存储内部对象：否则调用方持有的 Decision.Scope 会被后续扣减改写（6.1.3 / 4.7）。
        return new Decision(DecisionStatus.Allow, PssCode.Ok, "用户允许", requestId, granted ?? PermitStore.CloneScope(scope));
    }

    // ───────────────────────────── 状态 / 撤销 ─────────────────────────────

    public RequestStatus QueryStatus(string requestId)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        if (string.IsNullOrWhiteSpace(requestId))
        {
            // 无副作用：格式非法与未知同样返回 Deny + null（6.1.2）。
            return new RequestStatus(requestId, DecisionStatus.Deny, null);
        }
        return _runtime.Permits.QueryStatus(requestId, _runtime.Now);
    }

    public PssResult Revoke(string requestId)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return new PssResult(PssCode.BadArg, "RequestId 不能为空白");
        }
        // 未知/过期/已用尽 → Ok + no-op（幂等；6.1.2）。
        var existed = _runtime.Permits.Revoke(requestId, _runtime.Now);
        return new PssResult(PssCode.Ok, existed ? "许可已撤销" : "no-op");
    }

    // ───────────────────────────── 审计 ─────────────────────────────

    public PssResult Record(AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        // Record 的 BadArg 判定全集（6.1.1）。
        if (string.IsNullOrWhiteSpace(record.Program)) return new PssResult(PssCode.BadArg, "Program 不能为空白");
        if (string.IsNullOrWhiteSpace(record.Action)) return new PssResult(PssCode.BadArg, "Action 不能为空白");
        if (!BehaviorCatalog.Exists(record.Action)) return new PssResult(PssCode.BadArg, $"行为编号不存在：{record.Action}");
        if (string.IsNullOrWhiteSpace(record.Target)) return new PssResult(PssCode.BadArg, "Target 不能为空白");
        if (string.IsNullOrWhiteSpace(record.Detail)) return new PssResult(PssCode.BadArg, "Detail 不能为空白");
        // 12.2 的「单条大小 4 KiB」是字节量，不是 UTF-16 码元数：
        // 中文 detail 1 个字符约 3 字节，按字符数判定会写出超限的审计行（7.4 / 7.5）。
        if (Encoding.UTF8.GetByteCount(record.Detail) > 3800)
        {
            return new PssResult(PssCode.BadArg, "Detail 超过单条大小上限（12.2：4 KiB，按 UTF-8 字节计）");
        }
        if (record.Decision == AuditDecision.Escalation && string.IsNullOrWhiteSpace(record.Evidence))
        {
            return new PssResult(PssCode.BadArg, "Escalation 记录必须携带 Evidence（sha256 摘要）");
        }

        var audit = _runtime.Audit;
        if (audit is null)
        {
            return new PssResult(PssCode.NoModule, "审计存储未初始化");
        }

        var outcome = audit.TryAppend(record);
        return outcome switch
        {
            AuditWriteOutcome.Written => new PssResult(PssCode.Ok, "已记录"),
            AuditWriteOutcome.Buffered => new PssResult(PssCode.Ok, "审计降级：记录已入待写队列，恢复后补写", degraded: true),
            _ => new PssResult(PssCode.AuditUnavailable, "审计通道不可用（详见 2.6.1 策略）", degraded: true),
        };
    }

    public IReadOnlyList<AuditRecord> Query(AuditQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var audit = _runtime.Audit;
        return audit is null ? Array.Empty<AuditRecord>() : audit.Query(query);
    }

    public Task<IReadOnlyList<AuditRecord>> StreamAsync(AuditQuery query, CancellationToken cancellationToken = default)
    {
        // 取消表现为空结果 + 正常返回，不抛 OperationCanceledException（4.10 第 6 条）。
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult<IReadOnlyList<AuditRecord>>(Array.Empty<AuditRecord>());
        }
        return Task.FromResult(Query(query));
    }

    /// <summary>删除审计记录（用户数据必须可删除，4.4）；实现级补充透传。</summary>
    public PssResult Purge(AuditQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var audit = _runtime.Audit;
        return audit is null ? new PssResult(PssCode.NoModule, "审计存储未初始化") : audit.Purge(query);
    }

    public PssResult Export(AuditExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var audit = _runtime.Audit;
        if (audit is null)
        {
            return new PssResult(PssCode.NoModule, "审计存储未初始化");
        }
        return audit.Export(options);
    }

    // ───────────────────────────── 状态与界面 ─────────────────────────────

    /// <summary>当前模块状态；Unavailable 表示必须 fail-closed（6.1.4）。</summary>
    public ModuleState State => _runtime.State;

    /// <summary>
    /// 本程序在审计记录里的标识（4.7 的 program 字段）：取首次登记的程序名，未登记时为 unknown-program。
    /// 模块四（SRT）写审计时按此填 program，使同一进程内两条路径（SRT 与拦截层）的归属一致。
    /// </summary>
    public string ProgramId => _runtime.ProgramId;

    /// <summary>模块 API 版本（7.1/7.2）。</summary>
    public Version ApiVersion => ApiVer;

    /// <summary>本程序声明的接入模式（2.7），由 options 决定，只读。</summary>
    public IntegrationMode Mode => _options.Mode;

    /// <summary>许可被用户改动时触发（撤销/到期/用尽；6.1.4：线程池线程触发）。</summary>
    public event EventHandler<PermitChangedEventArgs>? PermitChanged;

    /// <summary>当前有效许可（模块管理界面展示 / 撤销用；实现级补充，不影响 4.3 API 面）。</summary>
    public IReadOnlyList<(string RequestId, PermitScope Scope)> ListPermits()
        => _runtime.Permits.SnapshotWithIds();

    /// <summary>清空全部许可（仅模块管理界面调用；Dispose 不走此路径）。</summary>
    public void RevokeAllPermits() => _runtime.Permits.RevokeAll(_runtime.Now);

    /// <summary>
    /// 打开模块界面（默认停在审计页）。
    /// <para><b>不得在 UI 线程调用</b>（同 <see cref="Ask"/>）：界面宿主需要回到 UI 线程，同步等待会自锁。</para>
    /// </summary>
    public PssResult ShowUi(SafetyUiPage page = SafetyUiPage.Audit)
        => ShowUiAsync(page).GetAwaiter().GetResult();

    /// <summary>打开模块界面（异步优先，界面程序应使用本重载）。</summary>
    public async Task<PssResult> ShowUiAsync(SafetyUiPage page = SafetyUiPage.Audit)
    {
        var handler = _uiHost.ShowUiHandler;
        if (handler is null)
        {
            return new PssResult(PssCode.NoModule, "模块界面宿主未注册");
        }
        try
        {
            await handler(page).ConfigureAwait(false);
            return new PssResult(PssCode.Ok, "已打开");
        }
        catch (Exception ex)
        {
            return new PssResult(PssCode.Internal, "界面打开失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 释放本客户端持有的一份运行时引用。
    /// <para>
    /// 4.10 第 5 条：Dispose 不得撤销已有许可、不得删除审计、不得丢弃待写队列——
    /// 所以这里只做两件事：摘掉自己的事件订阅，归还一份持有。
    /// 归还后若进程内已无其他持有者且无人借用过运行时，审计存储会被拆除
    /// （<see cref="SafetyEnvironment.Release"/>）；待写队列保留在磁盘上供下次启动接管。
    /// </para>
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        // 4.10 第 5 条：Dispose 不得撤销已有许可、不得删除审计、不得丢弃待写队列。
        _runtime.Permits.PermitEnded -= OnPermitEnded;
        SafetyEnvironment.Release(_runtime);
    }

    // ───────────────────────────── 内部 ─────────────────────────────

    private void OnPermitEnded(object? sender, PermitChangedEventArgs e)
    {
        // 6.1.4：线程池线程触发；回调异常被捕获并忽略（记一条 Internal 审计）。
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var handler = PermitChanged;
            if (handler is null)
            {
                return;
            }
            try
            {
                handler(this, e);
            }
            catch (Exception ex)
            {
                try
                {
                    _runtime.Audit?.AppendInternal(new AuditRecord(
                        _runtime.ProgramId, action: string.Empty, target: string.Empty,
                        AuditDecision.Allow, PssCode.Internal, AuditLayer.Arm,
                        detail: "PermitChanged 回调异常被忽略：" + ex.GetType().Name));
                }
                catch
                {
                    // 吞掉的是"回调异常的记录也写不进"（两级失败）；降级到"该异常只剩内存痕迹"。
                    // 事件本身已分发给订阅方，业务不受影响（PermitChanged 是通知语义，不是请求语义）。
                    // 何时应传播：不需要——在事件分发线程上抛出，会把一次记录失败升级为进程级故障。
                }
            }
        });
    }

    private static string AssignRequestId(AskRequest request)
    {
        if (string.IsNullOrEmpty(request.RequestId))
        {
            request.RequestId = "req-" + Guid.NewGuid().ToString("N");
        }
        return request.RequestId;
    }

    private static string? ValidateRequest(AskRequest request)
    {
        // 6.1.1 Ask 的 BadArg 判定全集（1~8），detail 指明是哪一条。
        if (!BehaviorCatalog.Exists(request.Action))
        {
            return $"行为编号不存在（3.6 共 63 项，大小写敏感）：{request.Action}";
        }
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return "Reason 为空白";
        }
        if (request.Reason.Length > 200)
        {
            return "Reason 超过 200 字符";
        }
        if (string.IsNullOrWhiteSpace(request.Target))
        {
            return "Target 为空白";
        }
        if (request.Target.Length > 1024)
        {
            return "Target 超过 1024 字符";
        }
        if (request.MatchMode != MatchMode.HostSuffix && (request.Target.Contains('*', StringComparison.Ordinal) || request.Target.Contains('?', StringComparison.Ordinal)))
        {
            return "Target 含通配符（*、?）";
        }

        var category = BehaviorCatalog.Find(request.Action)!.Category;
        var matchError = Matching.ValidateTarget(category, request.Target, request.MatchMode);
        if (matchError is not null)
        {
            return matchError;
        }

        if (request.Ttl is not null && (request.Ttl <= TimeSpan.Zero || request.Ttl > TimeSpan.FromHours(24)))
        {
            return "Ttl 必须大于零且不超过 24 小时";
        }
        if (request.MaxUses is not null && (request.MaxUses < 1 || request.MaxUses > 1000))
        {
            return "MaxUses 必须在 1 ~ 1000 之间";
        }
        if (request.RequestId is not null && (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128))
        {
            return "RequestId 为空白或超过 128 字符";
        }
        return null;
    }

    private bool TryGetExistingDecision(AskRequest request, out Decision decision)
    {
        lock (_gate)
        {
            if (request.RequestId is not null && _decisions.TryGetValue(request.RequestId, out decision!))
            {
                // 幂等命中：审计记 Cached = true（4.10 第 3 条）。
                RecordIdempotentHit(request, decision);
                return true;
            }
        }
        decision = null!;
        return false;
    }

    private bool TryPermitCache(AskRequest request, out Decision decision)
    {
        decision = null!;
        if (_runtime.Permits.TryGetActivePermit(
                request.Action, Matching.Normalize(request.Target), request.MatchMode,
                _runtime.Now, out var rid, out _))
        {
            // 4.3 第 4 条：命中已授予许可直接 Allow，不再询问用户；
            // 许可为模块权威计数（6.1.3），命中一次扣减一次；快照取扣减之后的值。
            var granted = _runtime.Permits.Consume(rid, _runtime.Now);
            if (granted is null)
            {
                return false; // 竞态：许可在两步之间被撤销或用尽 → 退回正常裁决路径。
            }
            decision = new Decision(DecisionStatus.Allow, PssCode.Ok, "命中已授予许可", rid, granted);
            RecordCacheHit(request, decision);
            return true;
        }
        return false;
    }

    private void RecordCacheHit(AskRequest request, Decision decision)
    {
        // 幂等映射：同 RequestId 再来直接命中（4.3 第 2 条）。
        var requestId = AssignRequestId(request);
        _runtime.RememberRequest(requestId, request.Action, Matching.Normalize(request.Target));
        lock (_gate)
        {
            _decisions[requestId] = decision;
        }
        if (!_options.AutoRecordDecisions)
        {
            return;
        }
        var audit = _runtime.Audit;
        audit?.TryAppend(new AuditRecord(
            _runtime.ProgramId, request.Action, Matching.Normalize(request.Target),
            AuditDecision.Allow, PssCode.Ok, AuditLayer.Arm,
            detail: "命中已授予许可，未再次询问用户",
            requestId: decision.RequestId,
            cached: true));
    }

    private Decision Finish(AskRequest request, Decision decision)
    {
        CommitDecision(request, decision);
        return decision;
    }

    private void CommitDecision(AskRequest request, Decision decision)
    {
        var requestId = AssignRequestId(request);
        _runtime.RememberRequest(requestId, request.Action, Matching.Normalize(request.Target));

        lock (_gate)
        {
            _decisions[requestId] = decision;
        }

        if (_options.AutoRecordDecisions)
        {
            var auditDecision = decision.Status switch
            {
                DecisionStatus.Allow => AuditDecision.Allow,
                DecisionStatus.Deny => AuditDecision.Deny,
                _ => AuditDecision.Ask,
            };
            var audit = _runtime.Audit;
            audit?.TryAppend(new AuditRecord(
                _runtime.ProgramId, request.Action, Matching.Normalize(request.Target),
                auditDecision, decision.Code, AuditLayer.Arm,
                detail: $"申报裁决：{decision.Detail}",
                requestId: requestId,
                cached: false));
        }
    }

    private void RecordIdempotentHit(AskRequest request, Decision decision)
    {
        if (!_options.AutoRecordDecisions)
        {
            return;
        }
        var audit = _runtime.Audit;
        audit?.TryAppend(new AuditRecord(
            _runtime.ProgramId, request.Action, Matching.Normalize(request.Target),
            decision.Status switch { DecisionStatus.Allow => AuditDecision.Allow, DecisionStatus.Deny => AuditDecision.Deny, _ => AuditDecision.Ask },
            decision.Code, AuditLayer.Arm,
            detail: "重复申报，命中幂等缓存",
            requestId: decision.RequestId,
            cached: true));
    }
}
