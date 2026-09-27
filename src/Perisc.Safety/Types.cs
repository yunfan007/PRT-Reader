using System;

namespace Perisc.Safety;

// ═══════════════════════════════════════════════════════════════════
// 枚举（PSS 标准 4.7 / 4.8 / 2.6.1 / 2.7）
// ═══════════════════════════════════════════════════════════════════

/// <summary>裁决三态（3.4）。</summary>
public enum DecisionStatus { Allow, Deny, Ask }

/// <summary>模块状态（6.1.4）：Unavailable 必须 fail-closed。</summary>
public enum ModuleState { Unavailable, Degraded, Ready }

/// <summary>审计裁决（7.4）：含 undeclared 与 escalation。</summary>
public enum AuditDecision { Allow, Deny, Ask, Undeclared, Escalation }

/// <summary>产生审计的层（7.4）。</summary>
public enum AuditLayer { Arm, Intercept, Guard }

/// <summary>模块界面页（4.3 ShowUi）。</summary>
public enum SafetyUiPage { Audit, Permits, ActionList }

/// <summary>许可范围的比对方式（4.7.1）。</summary>
public enum MatchMode { Exact, PathPrefix, HostSuffix, HostPortRange }

/// <summary>审计写入失败时的策略（2.6.1）；默认 Buffer。</summary>
public enum AuditFailurePolicy { Deny, Buffer, Escalate }

/// <summary>
/// 接入模式（PSS 标准 3.7）：A = Native（原生全接入），B = Bridged（桥接全接入）。
/// <para>
/// 标准 3.7 规定「两种模式是全部」——不存在「只接一部分层」的接入方式，
/// 故本枚举**不**提供其它取值；任一种模式都要求四层齐备。
/// </para>
/// </summary>
public enum IntegrationMode { Native, Bridged }

/// <summary>错误码全集（4.8）：十值，不得自造。</summary>
public enum PssCode
{
    Ok = 0,
    NoModule,           // 模块库不可用（未安装、被禁用、加载失败）
    Denied,             // 未申报 / 用户拒绝 / 被拦截层阻断
    Timeout,            // 申报未在限时内返回，按拒绝处理
    BadArg,             // 行为编号不存在、对象粒度过粗、理由为空
    AuditUnavailable,   // 审计不可写：按 AuditFailurePolicy 处理
    GuardUnavailable,   // 守护服务未运行或 IPC 不通（等级封顶 B+）
    Limit,              // 申报频率过高、许可数超限（12.2）
    ApiMismatch,        // API 版本不匹配（7.2）
    Internal            // 实现内部错误（不得用于表达业务拒绝）
}

// ═══════════════════════════════════════════════════════════════════
// 结果类型：程序只读（属性对外只读、模块内部可写；4.7 类型约定）
// ═══════════════════════════════════════════════════════════════════

/// <summary>一次申报的裁决结果（4.3）。</summary>
public sealed class Decision
{
    public Decision(DecisionStatus status, PssCode code, string detail, string requestId, PermitScope? scope = null)
    {
        Status = status;
        Code = code;
        Detail = detail ?? throw new ArgumentNullException(nameof(detail));
        RequestId = requestId ?? throw new ArgumentNullException(nameof(requestId));
        Scope = scope;
    }

    public DecisionStatus Status { get; internal set; }
    public PssCode Code { get; internal set; }
    public string Detail { get; internal set; }
    public string RequestId { get; internal set; }
    public PermitScope? Scope { get; internal set; }

    /// <summary>Status == DecisionStatus.Allow。</summary>
    public bool IsAllowed => Status == DecisionStatus.Allow;
}

/// <summary>写审计 / 守护调用的通用结果（4.7）。</summary>
public sealed class PssResult
{
    public PssResult(PssCode code, string detail, bool degraded = false)
    {
        Code = code;
        Detail = detail ?? throw new ArgumentNullException(nameof(detail));
        Degraded = degraded;
    }

    public PssCode Code { get; internal set; }
    public string Detail { get; internal set; }
    public bool Degraded { get; internal set; }

    /// <summary>Code == PssCode.Ok。</summary>
    public bool Ok => Code == PssCode.Ok;
}

/// <summary>许可范围（4.7）：RemainingUses 由模块权威扣减（6.1.3）。</summary>
public sealed class PermitScope
{
    public PermitScope(string action, string target, MatchMode matchMode, DateTimeOffset expiresAt, int remainingUses)
    {
        Action = action ?? throw new ArgumentNullException(nameof(action));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        MatchMode = matchMode;
        ExpiresAt = expiresAt;
        RemainingUses = remainingUses;
    }

    public string Action { get; internal set; }
    public string Target { get; internal set; }
    public MatchMode MatchMode { get; internal set; }
    public DateTimeOffset ExpiresAt { get; internal set; }
    public int RemainingUses { get; internal set; }
}

/// <summary>某次申报的当前状态（4.3 QueryStatus）。</summary>
public sealed class RequestStatus
{
    public RequestStatus(string requestId, DecisionStatus status, PermitScope? scope)
    {
        RequestId = requestId ?? throw new ArgumentNullException(nameof(requestId));
        Status = status;
        Scope = scope;
    }

    public string RequestId { get; internal set; }
    public DecisionStatus Status { get; internal set; }
    public PermitScope? Scope { get; internal set; }
}

/// <summary>守护服务终止通知（2.4 / 4.6）。</summary>
public sealed class TerminationNotice
{
    public TerminationNotice(string eventId, string rule, string target, DateTimeOffset deadline)
    {
        EventId = eventId ?? throw new ArgumentNullException(nameof(eventId));
        Rule = rule ?? throw new ArgumentNullException(nameof(rule));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Deadline = deadline;
    }

    public string EventId { get; internal set; }
    public string Rule { get; internal set; }
    public string Target { get; internal set; }
    public DateTimeOffset Deadline { get; internal set; }
}

/// <summary>终止事件的事后解释（4.6 Explain）。</summary>
public sealed class TerminationExplanation
{
    public TerminationExplanation(string eventId, string rule, string target,
                                  string programId, DateTimeOffset time, string evidence)
    {
        EventId = eventId ?? throw new ArgumentNullException(nameof(eventId));
        Rule = rule ?? throw new ArgumentNullException(nameof(rule));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        ProgramId = programId ?? throw new ArgumentNullException(nameof(programId));
        Time = time;
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
    }

    public string EventId { get; internal set; }
    public string Rule { get; internal set; }
    public string Target { get; internal set; }
    public string ProgramId { get; internal set; }
    public DateTimeOffset Time { get; internal set; }
    public string Evidence { get; internal set; }
}

/// <summary>拦截层的裁决结果（4.5 Report）：未申报一律 Deny。</summary>
public sealed class InterceptReport
{
    public InterceptReport(DecisionStatus status, PssCode code, string detail)
    {
        Status = status;
        Code = code;
        Detail = detail ?? throw new ArgumentNullException(nameof(detail));
    }

    public DecisionStatus Status { get; internal set; }
    public PssCode Code { get; internal set; }
    public string Detail { get; internal set; }
}

// ═══════════════════════════════════════════════════════════════════
// 入参类型：由程序用公开构造函数创建，创建完即只读快照（模块可回填）
// ═══════════════════════════════════════════════════════════════════

/// <summary>申报请求（4.7）：Action 取 3.6 的行为编号。</summary>
public sealed class AskRequest
{
    public AskRequest(string action, string target, string reason,
                      MatchMode matchMode = MatchMode.Exact,
                      TimeSpan? ttl = null,
                      string? requestId = null,
                      int? maxUses = null)
    {
        Action = action ?? throw new ArgumentNullException(nameof(action));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Reason = reason ?? throw new ArgumentNullException(nameof(reason));
        MatchMode = matchMode;
        Ttl = ttl;
        RequestId = requestId;
        MaxUses = maxUses;
    }

    /// <summary>3.6 的行为编号，如 "FS-02"。</summary>
    public string Action { get; internal set; }

    /// <summary>对象文本，如 "~/Documents/报告.prt"。</summary>
    public string Target { get; internal set; }

    /// <summary>面向用户的一句话。</summary>
    public string Reason { get; internal set; }

    /// <summary>匹配模式；默认精确匹配（3.3）。</summary>
    public MatchMode MatchMode { get; internal set; }

    /// <summary>期望有效时长；null 表示单次。</summary>
    public TimeSpan? Ttl { get; internal set; }

    /// <summary>幂等键；null 时由模块生成并回填。</summary>
    public string? RequestId { get; internal set; }

    /// <summary>有效期内允许次数。</summary>
    public int? MaxUses { get; internal set; }
}

/// <summary>一条审计记录（4.7 / 7.4）：detail 单行，不得含输入内容或剪贴板正文。</summary>
public sealed class AuditRecord
{
    public AuditRecord(string program, string action, string target,
                       AuditDecision decision, PssCode code, AuditLayer layer, string detail,
                       string? requestId = null, bool cached = false, TimeSpan? duration = null,
                       string? evidence = null, DateTimeOffset? time = null,
                       bool auditDegraded = false, DateTimeOffset? backfilledAt = null)
    {
        Program = program ?? throw new ArgumentNullException(nameof(program));
        Action = action ?? throw new ArgumentNullException(nameof(action));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Decision = decision;
        Code = code;
        Layer = layer;
        Detail = detail ?? throw new ArgumentNullException(nameof(detail));
        RequestId = requestId;
        Cached = cached;
        Duration = duration;
        Evidence = evidence;
        // 未给时刻时取进程时钟，而不是 DateTimeOffset.Now：4.7 规定的构造签名不能加时钟参数，
        // 经 SafetyEnvironment.Clock 取值可让"默认时刻"同样受 6.1.3 的回拨保护。
        Time = time ?? SafetyEnvironment.Clock.Now();
        AuditDegraded = auditDegraded;
        BackfilledAt = backfilledAt;
    }

    public DateTimeOffset Time { get; internal set; }
    public string Program { get; internal set; }
    public string Action { get; internal set; }
    public string Target { get; internal set; }
    public AuditDecision Decision { get; internal set; }
    public PssCode Code { get; internal set; }
    public AuditLayer Layer { get; internal set; }
    public string Detail { get; internal set; }
    public string? RequestId { get; internal set; }
    public bool Cached { get; internal set; }
    public TimeSpan? Duration { get; internal set; }

    /// <summary>仅 Escalation 必需（sha256:&lt;64hex&gt;，6.2.3）。</summary>
    public string? Evidence { get; internal set; }

    /// <summary>2.6.1：本条是否在审计降级状态下写入。</summary>
    public bool AuditDegraded { get; internal set; }

    /// <summary>补写时刻；仅补写产生的记录有值（4.4）。</summary>
    public DateTimeOffset? BackfilledAt { get; internal set; }

    /// <summary>
    /// 按一次裁决快速构造"已执行"记录（6.1.7）。
    /// <para>
    /// 本方法是公开的便捷构造器，签名里不能出现 internal 的运行时类型，
    /// 故经 <see cref="SafetyEnvironment"/> 取进程运行时——那里只暴露"复用既有实例"，
    /// 不接受外部替换，与改造前"任意位置可改的静态属性"不是一回事。
    /// </para>
    /// </summary>
    public static AuditRecord For(Decision decision, string detail)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var runtime = SafetyEnvironment.Runtime;
        var (action, target) = runtime.LookupLastRequest(decision.RequestId);
        var audit = decision.Status switch
        {
            DecisionStatus.Allow => AuditDecision.Allow,
            DecisionStatus.Deny => AuditDecision.Deny,
            _ => AuditDecision.Ask,
        };
        return new AuditRecord(
            program: runtime.ProgramId,
            action: action,
            target: target,
            decision: audit,
            code: decision.Code,
            layer: AuditLayer.Arm,
            detail: detail ?? throw new ArgumentNullException(nameof(detail)),
            requestId: decision.RequestId,
            time: SafetyEnvironment.Clock.Now());
    }
}

/// <summary>审计查询条件（4.7）：非法取值返回空结果集，不判 BadArg（6.1.1）。</summary>
public sealed class AuditQuery
{
    public AuditQuery(DateTimeOffset? from = null, DateTimeOffset? to = null, string? category = null,
                      string? action = null, AuditDecision? decision = null, bool? degradedOnly = null)
    {
        From = from;
        To = to;
        Category = category;
        Action = action;
        Decision = decision;
        DegradedOnly = degradedOnly;
    }

    public DateTimeOffset? From { get; internal set; }
    public DateTimeOffset? To { get; internal set; }

    /// <summary>"FS" / "NET" / …（9 类之一；非法取值 → 空结果集）。</summary>
    public string? Category { get; internal set; }

    public string? Action { get; internal set; }
    public AuditDecision? Decision { get; internal set; }

    /// <summary>true 表示只看审计降级期间的记录（2.6.1）。</summary>
    public bool? DegradedOnly { get; internal set; }
}

/// <summary>审计导出选项（4.7）：目前仅支持 JSON Lines（4.4）。</summary>
public sealed class AuditExportOptions
{
    public AuditExportOptions(string path, DateTimeOffset? from = null, DateTimeOffset? to = null, bool jsonLines = true)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        From = from;
        To = to;
        JsonLines = jsonLines;
    }

    public string Path { get; internal set; }
    public DateTimeOffset? From { get; internal set; }
    public DateTimeOffset? To { get; internal set; }

    /// <summary>目前仅支持 JSON Lines（4.4）。</summary>
    public bool JsonLines { get; internal set; }
}

/// <summary>误报白名单条目（4.6 / 6.1.5）：Rule = "&lt;匹配模式&gt; &lt;对象文本&gt;"。</summary>
public sealed class WhitelistEntry
{
    public WhitelistEntry(string rule, string reason, string evidence)
    {
        Rule = rule ?? throw new ArgumentNullException(nameof(rule));
        Reason = reason ?? throw new ArgumentNullException(nameof(reason));
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
    }

    public string Rule { get; internal set; }
    public string Reason { get; internal set; }
    public string Evidence { get; internal set; }
}

// ═══════════════════════════════════════════════════════════════════
// 客户端配置对象（4.7）
// ═══════════════════════════════════════════════════════════════════

/// <summary>SafetyClient 配置（4.7）：默认超时 5 秒、审计失败策略 Buffer、申报频率 10 次/秒。</summary>
public sealed class SafetyOptions
{
    public SafetyOptions(TimeSpan? askTimeout = null,
                         bool blockOnModuleUnavailable = true,
                         bool autoRecordDecisions = true,
                         AuditFailurePolicy auditFailurePolicy = AuditFailurePolicy.Buffer,
                         IntegrationMode mode = IntegrationMode.Native,
                         string? programId = null,
                         SafetyUiHost? uiHost = null,
                         int askRateLimitPerSecond = DefaultAskRateLimitPerSecond)
    {
        AskTimeout = askTimeout;
        BlockOnModuleUnavailable = blockOnModuleUnavailable;
        AutoRecordDecisions = autoRecordDecisions;
        AuditFailurePolicy = auditFailurePolicy;
        Mode = mode;
        ProgramId = programId;
        UiHost = uiHost;
        AskRateLimitPerSecond = askRateLimitPerSecond;
    }

    /// <summary>2.1 的申报频率默认值：10 次/秒。</summary>
    public const int DefaultAskRateLimitPerSecond = 10;

    /// <summary>2.1 的申报频率硬上限：100 次/秒。超过此值的声明只按此值生效。</summary>
    public const int MaxAskRateLimitPerSecond = 100;

    /// <summary>默认 5 秒（4.3 第 3 条）。</summary>
    public TimeSpan? AskTimeout { get; internal set; }

    /// <summary>2.6：模块不可用时是否 fail-closed（默认是）。</summary>
    public bool BlockOnModuleUnavailable { get; internal set; }

    /// <summary>是否自动为每个裁决写审计（默认是）。</summary>
    public bool AutoRecordDecisions { get; internal set; }

    /// <summary>审计写入失败时的策略（2.6.1）；默认 Buffer。</summary>
    public AuditFailurePolicy AuditFailurePolicy { get; internal set; }

    /// <summary>接入模式（2.7）；须与行为清单一致。</summary>
    public IntegrationMode Mode { get; internal set; }

    /// <summary>本程序标识（审计与安全中心展示用；Create 时登记到运行时）。</summary>
    public string? ProgramId { get; internal set; }

    /// <summary>
    /// 本程序声明的申报频率上限（次/秒，2.1）：默认 <see cref="DefaultAskRateLimitPerSecond"/>（10），
    /// 硬上限 <see cref="MaxAskRateLimitPerSecond"/>（100）；超出硬上限的取值按硬上限生效。
    /// <para>
    /// 为什么这个值可声明：2.1 给出的形式是「默认值 + 硬上限」——默认值是正常使用下的合理选择，
    /// 硬上限是"不得超过"的边界。原实现把它写死成默认值，等于取消了"可以声明得更高"这一半。
    /// </para>
    /// <para>
    /// 超限时申报得到的是 <c>PssCode.Limit</c>（<b>不是</b>安全裁决），处置办法由 2.1 给出：
    /// <b>降频</b>、合并申报，或减少同时持有的许可。本库<b>不</b>代为退让重报——
    /// 那会把同步 API 变成会阻塞调用线程的 API（详见 <c>SafeExecutor</c> 的类注释）。
    /// 因此需要高节拍的场景（自检框架以机器速度扮演用户）应<b>显式声明</b>本值，
    /// 而一般的界面程序保持默认 10 次/秒即可：它的申报节拍由用户逐条答复决定，本来到不了上限。
    /// </para>
    /// </summary>
    public int AskRateLimitPerSecond { get; internal set; }

    /// <summary>
    /// 界面挂钩（3.4 ask / ShowUi）。未提供时申报一律按"用户未决定"拒绝（fail-closed），
    /// <c>ShowUiAsync</c> 返回 NoModule。
    /// <para>
    /// **按客户端实例持有**：不再有进程级静态挂钩（9.6.4 / D-07）。
    /// 同一进程里两个 <see cref="SafetyClient"/> 可以各有各的界面宿主，
    /// 这在原先的 <c>public static</c> handler 下无法表达。
    /// </para>
    /// </summary>
    public SafetyUiHost? UiHost { get; internal set; }
}

/// <summary>SafeGuardClient 配置（4.7）：通知窗口 0 ~ 2000 ms，默认 200（2.4）。</summary>
public sealed class SafeGuardOptions
{
    public SafeGuardOptions(string? pipeName = null, TimeSpan? heartbeatInterval = null,
                            int terminationNoticeWindowMs = 200,
                            IntegrationMode mode = IntegrationMode.Native)
    {
        PipeName = pipeName ?? SafeGuardClient.DefaultPipeName;
        HeartbeatInterval = heartbeatInterval;
        TerminationNoticeWindowMs = terminationNoticeWindowMs;
        Mode = mode;
    }

    /// <summary>默认 SafeGuardClient.DefaultPipeName（5.2）。</summary>
    public string PipeName { get; internal set; }

    /// <summary>默认 5 秒；上限 30 秒（12.2）。</summary>
    public TimeSpan? HeartbeatInterval { get; internal set; }

    /// <summary>0 ~ 2000 ms，默认 200；0 表示不通知直接终止（2.4）。</summary>
    public int TerminationNoticeWindowMs { get; internal set; }

    /// <summary>接入模式（2.7）。</summary>
    public IntegrationMode Mode { get; internal set; }
}

/// <summary>InterceptClient 配置（4.7）：categories 传 null 表示覆盖 3.6 全部行为项。</summary>
public sealed class InterceptOptions
{
    public InterceptOptions(IReadOnlyList<string>? categories = null)
    {
        Categories = categories;
    }

    public IReadOnlyList<string>? Categories { get; internal set; }
}

/// <summary>许可被改动（撤销/到期/用尽）时的事件参数（4.3）。</summary>
public sealed class PermitChangedEventArgs : EventArgs
{
    public PermitChangedEventArgs(string requestId, DecisionStatus status)
    {
        RequestId = requestId ?? throw new ArgumentNullException(nameof(requestId));
        Status = status;
    }

    public string RequestId { get; internal set; }
    public DecisionStatus Status { get; internal set; }
}
