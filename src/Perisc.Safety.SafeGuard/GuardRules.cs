namespace Perisc.Safety.SafeGuard;

/// <summary>
/// 规则编号全集（PSS 标准 6.2.2，规范性）。
/// <para>
/// <c>TerminationNotice.Rule</c> 与对应 Escalation 记录的规则标识<b>必须</b>取自本类，
/// <b>不得</b>自造编号——否则 8.5 的一致性验证无法通过。实现可以收窄触发条件，不得放宽。
/// </para>
/// <para>
/// 全集分两组：七条<b>终止</b>规则（<see cref="TerminationRules"/>）与一条<b>启动拒绝</b>规则
/// <see cref="BaselineMissing"/>；合起来即 <see cref="RuleCodes"/>。
/// </para>
/// </summary>
public static class GuardRules
{
    /// <summary>拦截层被卸载、禁用或旁路后，仍发生敏感行为（5.6）。</summary>
    public const string BypassIntercept = "BYPASS-INTERCEPT";

    /// <summary>模块一被禁用或卸载后，仍发生敏感行为。</summary>
    public const string ModuleDisabled = "MODULE-DISABLED";

    /// <summary>审计通道被关闭、篡改，或按 3.6.2 判定永久不可用后仍发生敏感行为。</summary>
    public const string AuditChannelBroken = "AUDIT-CHANNEL-BROKEN";

    /// <summary>运行体哈希与已声明的发布包不一致，且不被白名单凭据或新哈希基准覆盖（3.4）。</summary>
    public const string HashMismatch = "HASH-MISMATCH";

    /// <summary>进程被注入或替换后直接调用系统接口（3.4）。</summary>
    public const string InjectionDetected = "INJECTION-DETECTED";

    /// <summary>已登记程序连续 3 个心跳周期无心跳，且经一次宽限重探仍无心跳（5.7）。</summary>
    public const string HeartbeatLost = "HEARTBEAT-LOST";

    /// <summary>检测到针对宿主启动器自身的破坏（管道端点被替换、运行体校验逻辑被改写的尝试、配置被改写）。</summary>
    public const string GuardTamper = "GUARD-TAMPER";

    /// <summary>
    /// 启动前取不到运行体完整性基准（基准文件缺失、读不出，或里面没有该运行体）——按 3.4 二 拒绝启动。
    /// <para>
    /// 与 <see cref="HashMismatch"/> 分开记：后者是「有基准且对不上」，本条是「根本没有基准可用」，
    /// 两者的排查方向不同（前者查运行体被改或升级未整对替换，后者查发布包不完整）。
    /// </para>
    /// <para>
    /// 处置是<b>拒绝启动</b>而非终止（此刻程序尚未启动，没有 <c>TerminationNotice</c> 可发），
    /// 故<b>不</b>列入 <see cref="TerminationRules"/>；但在 6.2.2 的规则编号全集之内（见 <see cref="RuleCodes"/>）。
    /// </para>
    /// </summary>
    public const string BaselineMissing = "BASELINE-MISSING";

    /// <summary>
    /// 七条<b>终止</b>规则的编号全集（顺序固定，供界面与验证清单枚举）。
    /// <para>
    /// 这是<b>能产生终止</b>的那七条。6.2.2 的规则编号全集另含启动拒绝类的
    /// <see cref="BaselineMissing"/>（见 <see cref="RuleCodes"/>）——它发生在接入程序启动之前，
    /// 此时没有可终止的进程，故不属本表。校验入口用本表（<see cref="IsTerminationRule"/>），
    /// 枚举入口用 <see cref="RuleCodes"/>。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> TerminationRules { get; } = new[]
    {
        BypassIntercept,
        ModuleDisabled,
        AuditChannelBroken,
        HashMismatch,
        InjectionDetected,
        HeartbeatLost,
        GuardTamper,
    };

    /// <summary>
    /// 八条规则编号全集（6.2.2，顺序与标准表一致）：七条终止规则 + 一条启动拒绝规则。
    /// <para>
    /// 供界面与验证清单枚举「规则标识的合法取值」；<b>不得</b>自造编号（自造会让 8.5 的
    /// 一致性验证无法通过）。判断某条规则能否产生终止要用 <see cref="IsTerminationRule"/>，
    /// 不要用本表——两者差 <see cref="BaselineMissing"/> 一条。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> RuleCodes { get; } = new[]
    {
        BypassIntercept,
        ModuleDisabled,
        AuditChannelBroken,
        HashMismatch,
        InjectionDetected,
        HeartbeatLost,
        GuardTamper,
        BaselineMissing,
    };

    // ── 非终止类事件（不属于 6.2.2 的规则全集，仅用于运行留痕）──

    /// <summary>宿主启动器启动。</summary>
    public const string EventStarted = "STARTED";

    /// <summary>宿主启动器退出。</summary>
    public const string EventStopped = "STOPPED";

    /// <summary>接入程序已登记（guard.connect 成功）。</summary>
    public const string EventConnected = "CONNECTED";

    /// <summary>接入程序连接断开。</summary>
    public const string EventDisconnected = "DISCONNECTED";

    /// <summary>接入程序声明了误报白名单。</summary>
    public const string EventWhitelistDeclared = "WHITELIST-DECLARED";

    /// <summary>心跳上报的 versionHash 发生变化（3.4 六：自更新后的新哈希基准）。</summary>
    public const string EventHashBaseline = "HASH-BASELINE";

    /// <summary>用户把某次终止标记为误报（6.2.4 三：仅对同一规则与同一对象生效）。</summary>
    public const string EventFalsePositive = "FALSE-POSITIVE";

    /// <summary>是否属于 6.2.2 的规则编号全集（<b>不得</b>自造编号）。</summary>
    public static bool IsRuleCode(string? rule)
        => rule is not null && RuleCodes.Contains(rule, StringComparer.Ordinal);

    /// <summary>
    /// 是否属于 6.2.2 中<b>能产生终止</b>的那七条（<see cref="TerminationRules"/>）。
    /// 启动拒绝类的 <see cref="BaselineMissing"/> 返回 <c>false</c>——它不该走到终止路径上。
    /// </summary>
    public static bool IsTerminationRule(string? rule)
        => rule is not null && TerminationRules.Contains(rule, StringComparer.Ordinal);
}
