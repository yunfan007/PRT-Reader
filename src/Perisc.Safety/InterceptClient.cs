using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Perisc.Safety;

/// <summary>
/// 误报白名单存储（6.1.5，规范性）：
/// - 条目 Rule = "&lt;匹配模式&gt; &lt;对象文本&gt;"，上限 64 条；
/// - 持久化于 %LOCALAPPDATA%\Perisc\Safety\whitelist-&lt;程序标识&gt;.json；
/// - 命中白名单的未申报事件放行（不升级、不终止），但仍然写审计。
/// </summary>
public sealed class WhitelistStore
{
    private const int MaxEntries = 64; // 12.2

    /// <summary>状态锁：只保护 <see cref="_entries"/>（纯内存）。</summary>
    private readonly object _gate = new();

    /// <summary>
    /// I/O 闸门：独占白名单文件的读与写。
    /// <para>
    /// 与状态锁分开的理由是 D-28：原先 <c>Save()</c> 在 <c>_gate</c> 内做整文件重写，
    /// 于是磁盘一慢，连只读的 <see cref="Snapshot"/> 与 <see cref="IsWhitelisted"/> 都要排队。
    /// 现在读方法只取 <c>_gate</c>，完全不碰磁盘。
    /// </para>
    /// <para>
    /// 读-改-写整段仍由 <c>_io</c> 独占——这是必要的：白名单是"整表重写"语义，
    /// 两条并发的增删若各自读旧表再写，后写的那次会把先写的条目抹掉。
    /// </para>
    /// <para>锁序铁律：**先 <c>_io</c>、后 <c>_gate</c>，绝不反向**（与 AuditStore 同一约定）。</para>
    /// <para>
    /// 本类不实现 <c>IDisposable</c>、也不释放 <c>_io</c>：<c>SemaphoreSlim</c> 是纯托管对象
    /// （未访问 <c>AvailableWaitHandle</c> 时不申请非托管句柄），不释放不是泄漏（D-11c 明文）；
    /// 而释放它会引入新的失败模式——已通过闸门、正要 <c>Release()</c> 的线程会拿到
    /// <c>ObjectDisposedException</c>。这条规则全仓一致，登记在《设计取舍》。
    /// </para>
    /// </summary>
    private readonly SemaphoreSlim _io = new(1, 1);

    private readonly List<WhitelistEntry> _entries = new();
    private readonly string _filePath;

    public WhitelistStore(string programId)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Perisc", "Safety");
        _filePath = Path.Combine(dir, "whitelist-" + Sanitize(programId) + ".json");
        Load();
    }

    public string FilePath => _filePath;

    /// <summary>当前条目快照。</summary>
    public IReadOnlyList<WhitelistEntry> Snapshot()
    {
        lock (_gate)
        {
            return new List<WhitelistEntry>(_entries);
        }
    }

    /// <summary>新增条目；规则非法或超上限返回 BadArg / Limit，成功返回 Ok。</summary>
    public PssResult TryAdd(WhitelistEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var error = ValidateRule(entry.Rule);
        if (error is not null)
        {
            return new PssResult(PssCode.BadArg, error);
        }

        _io.Wait();
        try
        {
            List<WhitelistEntry>? snapshot = null;
            lock (_gate)
            {
                if (_entries.Count >= MaxEntries)
                {
                    return new PssResult(PssCode.Limit, $"白名单条目已达上限（{MaxEntries}）");
                }
                foreach (var existing in _entries)
                {
                    if (string.Equals(existing.Rule, entry.Rule, StringComparison.Ordinal))
                    {
                        // 6.1.5：条目按 Rule 全文相等去重；重复申报是幂等 no-op，不判 BadArg。
                        return new PssResult(PssCode.Ok, "no-op：已存在相同规则");
                    }
                }
                _entries.Add(entry);
                // 在状态锁内取快照，落盘用快照：**不**把 _entries 带出锁去序列化，
                // 否则序列化期间发生的增删会让写出的文件与内存状态不一致。
                snapshot = new List<WhitelistEntry>(_entries);
            }

            Save(snapshot);
            return new PssResult(PssCode.Ok, "白名单条目已添加");
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>按规则文本移除条目（幂等）。</summary>
    public PssResult Remove(string rule)
    {
        if (string.IsNullOrWhiteSpace(rule))
        {
            return new PssResult(PssCode.BadArg, "规则文本不能为空白");
        }

        _io.Wait();
        try
        {
            List<WhitelistEntry>? snapshot = null;
            lock (_gate)
            {
                var removed = _entries.RemoveAll(e => string.Equals(e.Rule, rule, StringComparison.Ordinal));
                if (removed > 0)
                {
                    snapshot = new List<WhitelistEntry>(_entries);
                }
            }
            if (snapshot is not null)
            {
                Save(snapshot);
            }
        }
        finally
        {
            _io.Release();
        }
        return new PssResult(PssCode.Ok, "no-op");
    }

    /// <summary>目标文本是否命中任一白名单规则（6.1.5：匹配语义与许可一致）。</summary>
    public bool IsWhitelisted(string target)
    {
        if (string.IsNullOrEmpty(target))
        {
            return false;
        }
        lock (_gate)
        {
            foreach (var entry in _entries)
            {
                if (TryParseRule(entry.Rule, out var mode, out var ruleTarget) &&
                    Matching.IsMatch(ruleTarget, mode, target))
                {
                    return true;
                }
            }
        }
        return false;
    }

    // ───────────────────────────── 规则解析 ─────────────────────────────

    /// <summary>
    /// 解析 "&lt;匹配模式&gt; &lt;对象文本&gt;"（6.1.5）：匹配模式的拼写**按 5.2 第 ⑥ 条**取
    /// <c>exact / pathPrefix / hostSuffix / hostPortRange</c>（C# 枚举名不得直接上线，P.4 会逐字比对）；非法返回 false。
    /// </summary>
    private static bool TryParseRule(string rule, out MatchMode mode, out string target)
    {
        mode = MatchMode.Exact;
        target = string.Empty;
        var firstSpace = rule.IndexOf(' ', StringComparison.Ordinal);
        if (firstSpace <= 0)
        {
            return false;
        }
        if (!PssText.TryParseMatchMode(rule[..firstSpace], out mode))
        {
            return false;
        }
        target = rule[(firstSpace + 1)..].Trim();
        return target.Length > 0 && target.Length <= 1024;
    }

    /// <summary>新增前的规则校验（6.1.5：语法与许可对象同规格）。</summary>
    private static string? ValidateRule(string rule)
    {
        if (string.IsNullOrWhiteSpace(rule))
        {
            return "规则文本为空白";
        }
        if (rule.Length > 1100)
        {
            return "规则文本超过长度上限";
        }
        if (!TryParseRule(rule, out _, out _))
        {
            return "规则必须为「<匹配模式> <对象文本>」，匹配模式为 exact / pathPrefix / hostSuffix / hostPortRange（6.1.5）";
        }
        return null;
    }

    // ───────────────────────────── 持久化 ─────────────────────────────

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(_filePath, Encoding.UTF8));
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                string rule = element.TryGetProperty("rule", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : string.Empty;
                string reason = element.TryGetProperty("reason", out var rs) && rs.ValueKind == JsonValueKind.String ? rs.GetString()! : string.Empty;
                string evidence = element.TryGetProperty("evidence", out var ev) && ev.ValueKind == JsonValueKind.String ? ev.GetString()! : string.Empty;
                if (rule.Length > 0)
                {
                    _entries.Add(new WhitelistEntry(rule, reason, evidence));
                }
            }
        }
        catch
        {
            // 文件损坏视为无白名单（fail-closed：不会因此放行任何未申报行为）。
            _entries.Clear();
        }
    }

    /// <summary>
    /// 把一份条目快照整表写入白名单文件（**调用方须已持 <c>_io</c>**）。
    /// 参数是快照而非直接读 <c>_entries</c>：落盘内容必须与调用方在状态锁内看到的那一份一致。
    /// </summary>
    private void Save(IReadOnlyList<WhitelistEntry> entries)
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var sb = new StringBuilder("[");
            for (var i = 0; i < entries.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                sb.Append("{\"rule\":").Append(JsonSerializer.Serialize(entries[i].Rule));
                sb.Append(",\"reason\":").Append(JsonSerializer.Serialize(entries[i].Reason));
                sb.Append(",\"evidence\":").Append(JsonSerializer.Serialize(entries[i].Evidence));
                sb.Append('}');
            }
            sb.Append(']');
            File.WriteAllText(_filePath, sb.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 吞掉的是"白名单落盘失败"（权限 / 磁盘满）；降级到"内存条目仍生效，下次修改时重试"。
            // 方向是安全的：内存里的白名单才是裁决依据，落盘只影响重启后是否还记得。
            // 何时应传播：不需要——4.10 第 1 条要求以返回值表达结果；且放行判定不得因写盘失败而中断。
        }
    }

    private static string Sanitize(string programId)
    {
        var sb = new StringBuilder(programId.Length);
        foreach (var ch in programId)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '.' ? ch : '_');
        }
        var text = sb.ToString();
        return text.Length > 64 ? text[..64] : text;
    }
}

/// <summary>
/// 模块二（IM）：拦截层（4.5，规范性）。
/// 程序内未被申报（Ask）直接发生的敏感行为，由本层兜底：未申报一律 Deny（2.1 默认拒绝）。
/// - 命中误报白名单（6.1.5）→ 放行（仍写审计）；
/// - 命中有效许可（2.3：不得对已 allow 且在有效期内的行为重复阻断）→ 放行；
/// - 其余 → Deny + 写 Undeclared 审计（拦截层兜底裁决本身不是申报，不产生许可）。
/// </summary>
public sealed class InterceptClient : IDisposable
{
    private static readonly object s_gate = new();
    private static InterceptClient? s_current;

    private readonly InterceptOptions _options;
    private readonly SafetyProcessState _runtime;
    private readonly WhitelistStore _whitelist;
    private bool _disposed;

    private InterceptClient(InterceptOptions options, SafetyProcessState runtime, WhitelistStore whitelist)
    {
        _options = options;
        _runtime = runtime;
        _whitelist = whitelist;
    }

    /// <summary>本进程已注册的拦截层；未注册为 null。</summary>
    public static InterceptClient? Current
    {
        get
        {
            lock (s_gate)
            {
                return s_current is { _disposed: false } ? s_current : null;
            }
        }
    }

    /// <summary>注册拦截层（每进程一次；重复注册返回同一实例）。</summary>
    public static InterceptClient Register(InterceptOptions? options = null)
    {
        lock (s_gate)
        {
            if (s_current is { _disposed: false })
            {
                return s_current;
            }
            var opts = options ?? new InterceptOptions();
            // 与 SafetyClient.Create 同一入口：Acquire 记一份持有，注销时归还（D-12）。
            var runtime = SafetyEnvironment.Acquire();
            var whitelist = new WhitelistStore(runtime.ProgramId);
            s_current = new InterceptClient(opts, runtime, whitelist);
            return s_current;
        }
    }

    /// <summary>拦截层是否在位（程序可用此判断自检口径）。</summary>
    public bool IsActive => !_disposed;

    /// <summary>误报白名单（模块管理界面展示 / 增删用）。</summary>
    public WhitelistStore Whitelist => _disposed ? throw new ObjectDisposedException(nameof(InterceptClient)) : _whitelist;

    /// <summary>
    /// 拦截层裁决（4.5 Report）：拦截点在行为发生处调用；结果只读，不产生许可。
    /// 签名与 4.5 一致：<c>Report(string action, string target, string? stackSummary = null)</c>；
    /// 只抛 ArgumentNullException（编程错误，4.10 第 1 条），业务结果走返回值。
    /// 许可与白名单的匹配按 4.7.1 的规范化规则判定（精确匹配口径），<paramref name="stackSummary"/> 仅用于留痕。
    /// </summary>
    public InterceptReport Report(string action, string target, string? stackSummary = null)
    {
        var matchMode = MatchMode.Exact;
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(target);
        if (_disposed)
        {
            return new InterceptReport(DecisionStatus.Deny, PssCode.NoModule, "拦截层已注销，按拒绝处理");
        }

        // 用本实例持有的那一份运行时，而不是 SafetyEnvironment.Runtime：
        // 后者是"无主借用"，会把运行时钉到显式 Shutdown；本类已在 Register 时记了一份持有，
        // 归还点明确（Dispose），没有必要再去钉一次。
        var runtime = _runtime;

        // ① 行为编号必须落在 3.6 目录内（未知行为视为未申报）。
        var item = BehaviorCatalog.Find(action);
        if (item is null)
        {
            Audit(runtime, action, target, AuditDecision.Undeclared, PssCode.BadArg,
                $"拦截到目录外行为编号：{action}", requestId: null, cached: false);
            return new InterceptReport(DecisionStatus.Deny, PssCode.BadArg, $"行为编号不存在（3.6 目录 63 项）：{action}");
        }

        // ② 误报白名单（6.1.5）：命中放行，仍写审计。
        if (_whitelist.IsWhitelisted(target))
        {
            Audit(runtime, action, target, AuditDecision.Allow, PssCode.Ok,
                "拦截层命中误报白名单，放行", requestId: null, cached: true);
            return new InterceptReport(DecisionStatus.Allow, PssCode.Ok, "命中误报白名单，放行");
        }

        // ③ 有效许可（2.3）：放行且不重复扣减——扣减只发生在申报路径（6.1.3 模块权威计数）。
        if (runtime.Permits.TryGetActivePermit(
                action, Matching.Normalize(target), matchMode,
                runtime.Now, out var rid, out var _))
        {
            Audit(runtime, action, target, AuditDecision.Allow, PssCode.Ok,
                $"拦截层命中有效许可（{rid}），放行", requestId: rid, cached: true);
            return new InterceptReport(DecisionStatus.Allow, PssCode.Ok, "命中已授予许可，放行");
        }

        // ④ 未申报：默认拒绝（2.1），写 undeclared 审计。
        // 栈摘要只记"是否提供"，不落内容（2.1 最小采集 / 12.2 单条大小）。
        var stackNote = string.IsNullOrWhiteSpace(stackSummary) ? string.Empty : "，附调用点摘要";
        Audit(runtime, action, target, AuditDecision.Undeclared, PssCode.Denied,
            $"拦截到未申报行为（{item.Category}，{item.Title}），按默认拒绝处理{stackNote}", requestId: null, cached: false);
        return new InterceptReport(DecisionStatus.Deny, PssCode.Denied, "行为未申报，按默认拒绝处理（先经 Ask 申报）");
    }

    /// <summary>
    /// 注销拦截层：摘掉本进程的注册并归还一份运行时持有。
    /// <para>与模块一同样不撤销许可、不删除审计、不丢弃待写队列（4.10 第 5 条）。</para>
    /// </summary>
    public void Dispose()
    {
        var already = false;
        lock (s_gate)
        {
            already = _disposed;
            _disposed = true;
            if (ReferenceEquals(s_current, this))
            {
                s_current = null;
            }
        }
        if (already)
        {
            return;
        }
        SafetyEnvironment.Release(_runtime);
    }

    // ───────────────────────────── 内部 ─────────────────────────────

    /// <summary>写一条拦截层审计；策略（Deny/Buffer/Escalate）由审计存储内部处置（2.6.1）。</summary>
    private static void Audit(SafetyProcessState runtime, string action, string target,
                              AuditDecision decision, PssCode code, string detail,
                              string? requestId, bool cached)
    {
        var audit = runtime.Audit;
        audit?.TryAppend(new AuditRecord(
            runtime.ProgramId, action, Matching.Normalize(target),
            decision, code, AuditLayer.Intercept,
            detail: detail, requestId: requestId, cached: cached));
    }
}
