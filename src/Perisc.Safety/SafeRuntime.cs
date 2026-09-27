using System;

namespace Perisc.Safety;

/// <summary>
/// 模块四（SRT）无返回值的操作结果（PSS 标准 5.4 ④）。
/// <para>
/// 与模块一的 <see cref="PssResult"/> 同构：业务结果一律由返回值表达，不抛异常；
/// <see cref="Executed"/> 明确区分「已获许可并执行」与「未执行」——
/// 3.8 ③ 要求裁决为 deny、超时或模块不可用时一律<b>不执行</b>，本字段是该要求的可核验出口。
/// </para>
/// </summary>
public sealed class SafeResult
{
    public SafeResult(PssCode code, string detail, bool executed, string? requestId = null)
    {
        Code = code;
        Detail = detail ?? throw new ArgumentNullException(nameof(detail));
        Executed = executed;
        RequestId = requestId;
    }

    public PssCode Code { get; internal set; }
    public string Detail { get; internal set; }

    /// <summary>敏感动作是否真的被执行；false 表示未执行（3.8 ③ 的同失败语义）。</summary>
    public bool Executed { get; internal set; }

    /// <summary>本次申报的标识，便于与审计记录对齐。</summary>
    public string? RequestId { get; internal set; }

    public bool Ok => Code == PssCode.Ok;
}

/// <summary>
/// 模块四（SRT）带返回值的操作结果（PSS 标准 5.4 ④）。
/// 未执行时 <see cref="Value"/> 恒为该类型的默认值，调用方不应把它当作有效结果使用。
/// </summary>
public sealed class SafeResult<T>
{
    public SafeResult(PssCode code, string detail, T? value, bool executed, string? requestId = null)
    {
        Code = code;
        Detail = detail ?? throw new ArgumentNullException(nameof(detail));
        Value = value;
        Executed = executed;
        RequestId = requestId;
    }

    public PssCode Code { get; internal set; }
    public string Detail { get; internal set; }

    /// <summary>执行产出；仅当 <see cref="Executed"/> 为 true 时有效。</summary>
    public T? Value { get; internal set; }

    /// <summary>敏感动作是否真的被执行。</summary>
    public bool Executed { get; internal set; }

    public string? RequestId { get; internal set; }

    public bool Ok => Code == PssCode.Ok;
}

/// <summary>
/// 模块四：安全运行时（SRT，Safe RunTime）的类型族入口（PSS 标准 3.8、5.4）。
/// <para>
/// 做什么：为九类敏感行为提供<b>语义等价</b>的替代函数，使「申报」被焊进接口形状——
/// 调用 SRT 的函数即完成「申报 → 裁决 → 执行 → 审计」四步（3.8、5.4 ②）。
/// </para>
/// <para>
/// 不做什么（3.8）：不提供语言标准库没有的敏感能力，不扩大可达范围，
/// 不成为绕过申报或拦截的通道；<b>不签发许可</b>——许可的唯一权威是模块一（3.9 ①）。
/// </para>
/// </summary>
public static class SafeRuntime
{
    private static SafetyClient? s_client;
    private static readonly object s_gate = new();

    /// <summary>文件系统（FS）：创建 / 写入 / 删除 / 改名与移动 / 目录枚举 / 符号链接创建。</summary>
    public static SafeFile File { get; } = new SafeFile();

    /// <summary>进程与系统（PROC）：起子进程 / 结束进程 / 加载原生库 / 计划任务与自启。</summary>
    public static SafeProcess Process { get; } = new SafeProcess();

    /// <summary>
    /// 配置与注册表（CFG）：写系统配置 / 注册表 / 文件关联 / 环境变量。
    /// <para><b>平台特定</b>：注册表类成员依赖 Windows 注册表 API（HKCU），非 Windows 上按执行失败返回。</para>
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static SafeConfig Config { get; } = new SafeConfig();

    /// <summary>外设与媒体（DEV）：摄像头 / 麦克风 / 截屏 / 剪贴板 / 位置。</summary>
    public static SafeDevice Device { get; } = new SafeDevice();

    /// <summary>凭据与密钥（CRED）：读凭据存储 / 读写私钥 / 加解密用户数据。</summary>
    public static SafeCredential Credential { get; } = new SafeCredential();

    /// <summary>代码执行与反射（CODE）：动态编译 / 反射非公开成员 / 反序列化 / 加载程序集。</summary>
    public static SafeCode Code { get; } = new SafeCode();

    /// <summary>用户数据与隐私（PRIV）：读文档与媒体库 / 通讯录与日历 / 浏览器数据 / 上报。</summary>
    public static SafePrivacy Privacy { get; } = new SafePrivacy();

    /// <summary>资源占用（RES）：后台常驻 / 密集计算。</summary>
    public static SafeResource Resource { get; } = new SafeResource();

    /// <summary>
    /// 类型族共用的模块一客户端（进程内唯一）。
    /// <para>
    /// 为何缓存实例而不是每次 <see cref="SafetyClient.Create"/>：许可池与审计存储由
    /// <see cref="SafetyEnvironment"/> 持有共享，但每次 Create 会多记一份持有
    /// （D-12：最后一份持有归还才拆除审计存储）。类型族在进程内只取一份，
    /// 避免高频调用把持有计数撑成泄漏。
    /// </para>
    /// </summary>
    internal static SafetyClient Client
    {
        get
        {
            lock (s_gate)
            {
                return s_client ??= SafetyClient.Create();
            }
        }
    }

    /// <summary>
    /// 由宿主注入共享客户端（3.9 ② 的兑现点）。
    /// <para>
    /// 为什么必须由宿主注入：模块一（ARM）是许可的<b>唯一权威</b>，SRT 与模块二对同一次行为的表态
    /// 必须源自同一次 ARM 裁决，也就是共用同一许可池。若 SRT 自起一份客户端，
    /// 宿主发的许可 SRT 认不到，同一行为会出现「宿主说允许、SRT 说拒绝」的自相矛盾，
    /// 且 SRT 没有界面宿主时会一律 fail-closed——程序连自己的配置都读不出来。
    /// </para>
    /// <para>
    /// 传入 null 表示交回默认行为：下次访问时按默认配置重建一份（用于宿主退出时解绑，
    /// 避免类型族长期持有一个已 Dispose 的客户端）。
    /// </para>
    /// </summary>
    internal static void SetClient(SafetyClient? client)
    {
        lock (s_gate)
        {
            s_client = client;
        }
    }
}

/// <summary>
/// SRT 的四步执行器（PSS 标准 3.8 ②、5.4 ②）：申报 → 裁决 → 执行 → 审计。
/// <para>
/// 全类型族共用这一条路径，是为了让「四步不跳步」只有一处实现——
/// 若每个函数各写一遍，就会出现有的函数只申报不审计、有的函数裁决失败仍执行（3.8 ② 判据 ②③）。
/// </para>
/// <para>
/// <b>为什么不对 <c>Limit</c> 做退让重报（设计取舍，别再改回去）。</b>
/// 模块按 2.1 对申报有频控（默认 10 次/秒），超限时给出 <c>PssCode.Limit</c>；
/// 它的语义是"报得太密"，不是"这件事不许做"。所以看起来「睡一个窗口再申报一次」很自然——
/// 但那条路的代价是<b>把一个同步 API 变成会阻塞调用线程的 API</b>：
/// </para>
/// <list type="number">
/// <item>SRT 是同步 API，而本程序从<b>界面线程</b>调用它（打开文档、保存、复制粘贴都在 UI 线程上）；
/// 一次 1 秒的等待就是一次 1 秒的界面冻结——9.6.4 / D-14 明令禁止的形态，
/// 只不过藏在库文件里，静态扫描看不到。</item>
/// <item>触发它的不只是频控：审计待写队列有积压时同样返回 <c>Limit</c>（3.6.1 的闸门）。
/// 磁盘繁忙或写满时队列会一直有积压，于是每一次操作都冻一次——不是偶发，是持续。</item>
/// <item>本库自己的立场也一样：<c>SafetyClient.ConsultAsync</c> 特意把
/// <c>Task.Wait(timeout)</c> 换成 <c>CancelAfter</c>，理由就是「不占住一个线程干等」。</item>
/// </list>
/// <para>
/// 所以这里的处置是 <b>fail-closed 但不等待</b>：<c>Limit</c> 与其他拒绝一样走
/// 「不执行 + 返回可识别结果」（3.8 ③），由调用方当场给用户一句可读的话。
/// 而「申报频率」本身是<b>可声明</b>的（2.1 默认 10 次/秒、硬上限 100 次/秒）——
/// 需要高节拍的场景（自检框架以机器速度扮演用户）应当在建立客户端时声明，
/// 而不是靠在这里睡觉去迁就（见 <c>SafetyOptions.AskRateLimitPerSecond</c>）。
/// </para>
/// <para>
/// 库侧另有 4.3 第 4 条的许可缓存：同一（行为 + 对象 + 匹配模式）在许可有效期内重复申报
/// <b>不消耗频控额度</b>（缓存判定在频控之前）。所以"反复做同一件事"本来就不会撞上限。
/// </para>
/// </summary>
internal static class SafeExecutor
{
    /// <summary>执行无返回值的敏感动作。</summary>
    internal static SafeResult Run(string action, string target, string reason,
                                   Action execute,
                                   MatchMode matchMode = MatchMode.Exact,
                                   TimeSpan? ttl = null)
        => RunCore(action, target, reason, matchMode, ttl, () =>
        {
            execute();
            return true;
        }).ToVoid();

    /// <summary>执行有返回值的敏感动作。</summary>
    internal static SafeResult<T> Run<T>(string action, string target, string reason,
                                         Func<T> execute,
                                         MatchMode matchMode = MatchMode.Exact,
                                         TimeSpan? ttl = null)
        => RunCore(action, target, reason, matchMode, ttl, execute);

    private static SafeResult<T> RunCore<T>(string action, string target, string reason,
                                            MatchMode matchMode, TimeSpan? ttl, Func<T> execute)
    {
        // ── ① 申报 ──
        // 行为编号必须取自 4.6 的既有编号（3.8「等效不增强」）：自造编号等于新增敏感能力。
        if (string.IsNullOrWhiteSpace(action) || !BehaviorCatalog.Exists(action))
        {
            return new SafeResult<T>(PssCode.BadArg, "行为编号不在 4.6 的全集内：" + action, default, false);
        }
        if (string.IsNullOrWhiteSpace(target))
        {
            return new SafeResult<T>(PssCode.BadArg, "申报对象为空（4.3 对象粒度）", default, false);
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            return new SafeResult<T>(PssCode.BadArg, "申报理由为空（5.9 BadArg）", default, false);
        }

        var client = SafeRuntime.Client;
        if (client.State == ModuleState.Unavailable)
        {
            // 模块不可用即 fail-closed（3.6）：不得自行放行。
            return new SafeResult<T>(PssCode.NoModule, "模块不可用，已拒绝该操作（fail-closed）", default, false);
        }

        Decision decision;
        try
        {
            decision = client.Ask(new AskRequest(action, target, reason, matchMode, ttl));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 5.11 第 1 条：除进程级灾难外不得以异常表达业务失败。
            return new SafeResult<T>(PssCode.Internal, "申报过程异常：" + ex.Message, default, false);
        }

        // ── ② 裁决 ──
        // deny / ask 未决 / 超时 / 限额，一律不执行（3.8 ③ 同失败语义；不得部分执行、不得静默成功）。
        if (decision is null)
        {
            return new SafeResult<T>(PssCode.NoModule, "申报未返回结果，按拒绝处理", default, false);
        }
        if (!decision.IsAllowed)
        {
            return new SafeResult<T>(decision.Code, decision.Detail, default, false, decision.RequestId);
        }

        // ── ③ 执行 ──
        T value;
        var started = Environment.TickCount64;
        try
        {
            value = execute();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 执行期异常不是裁决结果，但同样要留痕：已获许可却未成功，属可追溯事实。
            Audit(client, action, target, AuditDecision.Allow, PssCode.Internal,
                  AuditLayer.Arm, "已获许可但执行失败：" + ex.GetType().Name, decision.RequestId,
                  Environment.TickCount64 - started);
            return new SafeResult<T>(PssCode.Internal, "执行失败：" + ex.Message, default, false, decision.RequestId);
        }

        // ── ④ 审计 ──
        // 5.5：必须在本调用栈内当场尝试写入，不得丢给后台线程。
        Audit(client, action, target, AuditDecision.Allow, PssCode.Ok, AuditLayer.Arm,
              decision.Detail, decision.RequestId, Environment.TickCount64 - started);
        return new SafeResult<T>(PssCode.Ok, decision.Detail, value, true, decision.RequestId);
    }

    /// <summary>把一次裁决与结果写入审计（5.5：在本调用栈内当场尝试；写不下由模块按 3.6.1 处置）。</summary>
    private static void Audit(SafetyClient client, string action, string target,
                              AuditDecision decision, PssCode code, AuditLayer layer,
                              string detail, string? requestId, long elapsedMs)
    {
        try
        {
            client.Record(new AuditRecord(
                program: client.ProgramId ?? "unknown-program",
                action: action,
                target: target,
                decision: decision,
                code: code,
                layer: layer,
                detail: detail,
                requestId: requestId,
                duration: TimeSpan.FromMilliseconds(elapsedMs)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 审计写不下的处置由 AuditFailurePolicy 在模块内完成（3.6.1）；
            // 此处吞掉是为了不让「补记失败」反过来改变已执行动作的返回值语义。
            _ = ex;
        }
    }

    private static SafeResult ToVoid(this SafeResult<bool> source)
        => new(source.Code, source.Detail, source.Executed, source.RequestId);
}
