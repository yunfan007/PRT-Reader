using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Perisc.Safety.SafeGuard;

/// <summary>
/// 宿主启动器存储（PSS 标准 6.2.1，规范性）：<c>%LOCALAPPDATA%\Perisc\SafeGuard\</c>。
/// - events\events-YYYYMMDD.jsonl：事件日志（连接 / 断开 / 心跳丢失 / 终止 …），只追加，按日分片；
/// - whitelist\whitelist-&lt;程序标识&gt;.json：各程序声明的误报白名单；
/// - baseline.json：**运行期**登记的哈希基准变更快照（3.4 六：自更新后以下一次心跳的 versionHash 为新基准）；
/// - exemptions.json：用户确认为误报后的本地豁免（仅对同一规则与同一对象生效）。
/// <para>
/// <b>为什么必须是 LOCALAPPDATA 而不是 PROGRAMDATA</b>：宿主启动器与主程序以<b>同一用户权限</b>运行
/// （3.4 一、7.1），不安装为系统服务、不要求管理员权限。放在全机可写的 PROGRAMDATA 下，
/// 等于让别的用户也能改写「终止谁、豁免谁」的凭据——那正是 6.2.1 要排除的形态
/// （目录权限：仅当前用户可写、可读；<b>不得</b>放宽为全机可写）。
/// </para>
/// 证据（6.2.3）：事件行内容规范的 sha256 摘要，explain 时一并返回。
/// </summary>
public sealed class GuardStore
{
    /// <summary>
    /// I/O 闸门：独占「事件日志与白名单文件」的读写。
    /// <para>
    /// 这里**没有状态锁**——本类不持有任何内存状态，闸门存在的唯一目的是让并发的文件读写串行化，
    /// 不是 D-28 要消除的形态（那说的是"持**状态**锁做 I/O"：结果是磁盘一慢，
    /// 状态查询也被牵连）。既然没有状态可被牵连，就不该摆一把看起来像状态锁的 <c>object</c>——
    /// 那会让下一个读代码的人以为这里有共享内存要保护。
    /// </para>
    /// <para>
    /// 闸门内只做文件动作，不做纯计算：见 <see cref="AppendEvent"/> 把 sha256 挪到闸门外，
    /// 以及 <see cref="FindEvent"/> 只把目录枚举留在闸门内。
    /// </para>
    /// <para>
    /// 本类不实现 <c>IDisposable</c>：除了这把纯托管的闸门，它不持有任何需要显式释放的资源
    /// （无定时器、无取消令牌、无 OS 句柄——文件都是即开即关）。闸门本身不释放的理由
    /// 与 <c>AuditStore</c> / <c>WhitelistStore</c> 一致，登记在《设计取舍》。
    /// </para>
    /// </summary>
    private readonly SemaphoreSlim _io = new(1, 1);

    public string Root { get; }
    public string EventsDir { get; }
    public string WhitelistDir { get; }

    public GuardStore()
    {
        Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Perisc", "SafeGuard");
        EventsDir = Path.Combine(Root, "events");
        WhitelistDir = Path.Combine(Root, "whitelist");
        Directory.CreateDirectory(EventsDir);
        Directory.CreateDirectory(WhitelistDir);
    }

    // ───────────────────────────── 事件 ─────────────────────────────

    /// <summary>事件类型（6.2.2 可实现集合；编号不得自造）。</summary>
    public const string RuleHeartbeatLost = "HEARTBEAT-LOST";
    public const string RuleSessionConnected = "CONNECTED";
    public const string RuleSessionDisconnected = "DISCONNECTED";
    public const string RuleWhitelistDeclared = "WHITELIST-DECLARED";

    /// <summary>心跳上报的 versionHash 发生变化（2.4：自更新后的新哈希基准）。</summary>
    public const string RuleHashBaseline = "HASH-BASELINE";

    /// <summary>追加一条事件并返回其 eventId 与 sha256 证据。</summary>
    public (string EventId, string Evidence) AppendEvent(string type, string programId, int pid, string detail)
    {
        var time = GuardClock.Now();
        var eventId = "evt-" + time.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8];
        var line = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["time"] = time.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
            ["eventId"] = eventId,
            ["type"] = type,
            ["programId"] = programId,
            ["pid"] = pid,
            ["detail"] = detail,
        });
        var file = Path.Combine(EventsDir, "events-" + time.ToString("yyyyMMdd") + ".jsonl");

        _io.Wait();
        try
        {
            // 只追加、不开读改写：追加语义下两把并发写只会互相抢文件句柄，不会互相覆盖。
            File.AppendAllText(file, line + Environment.NewLine, new UTF8Encoding(false));
        }
        finally
        {
            _io.Release();
        }

        // 证据 = 事件行的 sha256（6.2.3：证据必须可复核）。
        // 放在闸门之外：这是纯粹的 CPU 计算，占着 I/O 闸门算哈希只会无谓地拖住其他写入。
        // 输入用的是刚写下去的那一行文本，故与文件内容天然一致，不依赖读回。
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(line), digest);
        return (eventId, "sha256:" + Convert.ToHexString(digest).ToLowerInvariant());
    }

    /// <summary>按 eventId 检索事件（explain 用）；找不到返回 null。</summary>
    public (string Type, string ProgramId, DateTimeOffset Time, string Detail, string Evidence)? FindEvent(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return null;
        }

        // 只有**目录枚举**留在闸门内（要与追加互斥，避免列到一半文件被换掉）；
        // 逐行读取与 JSON 解析一律在闸门外——事件文件可能很大，
        // 把整段扫描关进闸门等于让所有写入排队等它读完，那正是要避免的形态。
        string[] files;
        _io.Wait();
        try
        {
            if (!Directory.Exists(EventsDir))
            {
                return null;
            }
            files = Directory.GetFiles(EventsDir, "events-*.jsonl");
        }
        finally
        {
            _io.Release();
        }

        foreach (var file in files.Reverse())
        {
            foreach (var line in File.ReadLines(file))
            {
                if (!line.Contains(eventId, StringComparison.Ordinal))
                {
                    continue;
                }
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (Wire.Str(root, "eventId") != eventId)
                    {
                        continue;
                    }
                    var time = root.TryGetProperty("time", out var t) && DateTimeOffset.TryParse(t.GetString(), out var tm)
                        ? tm
                        : GuardClock.Now();
                    return (Wire.Str(root, "type"), Wire.Str(root, "programId"), time,
                            Wire.Str(root, "detail"),
                            EvidenceOf(line));
                }
                catch
                {
                    // 吞掉的是"某一行的 JSON 不可解析"；降级到"跳过该行、继续扫其余行"，
                    // 使单行损坏不会让整次事件查询失败（未命中时返回 null，由调用方处置）。
                    // 何时应传播：不需要——只读查询不得因历史数据损坏而失败。
                }
            }
        }
        return null;
    }

    /// <summary>复核证据：重算该事件行的 sha256（6.2.3）。</summary>
    private static string EvidenceOf(string line)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(line), digest);
        return "sha256:" + Convert.ToHexString(digest).ToLowerInvariant();
    }

    // ───────────────────────────── 白名单 ─────────────────────────────

    /// <summary>
    /// 白名单里是否存在以该哈希为凭据的条目（3.4 六：自更新类条目须携带可核对凭据——
    /// 更新后的运行体哈希或发布签名者）。命中即认可该哈希为新的运行体基准。
    /// </summary>
    public bool WhitelistHasEvidence(string programId, string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return false;
        }
        return LoadWhitelist(WhitelistFileOf(programId))
            .Any(entry => string.Equals(entry.Evidence, hash, StringComparison.OrdinalIgnoreCase));
    }

    private string WhitelistFileOf(string programId)
        => Path.Combine(WhitelistDir, "whitelist-" + Sanitize(programId) + ".json");

    /// <summary>合并声明白名单条目（按规则文本去重），返回接受数与总数。</summary>
    public (int Accepted, int Total) MergeWhitelist(string programId, IEnumerable<(string Rule, string Reason, string Evidence)> entries)
    {
        var file = Path.Combine(WhitelistDir, "whitelist-" + Sanitize(programId) + ".json");

        // 读-改-写整段在闸门内：白名单是整表重写语义，两条并发合并若各自读旧表再写，
        // 后写的那次会把先写的条目抹掉。闸门内不做别的（去重是内存操作，很快）。
        _io.Wait();
        try
        {
            var rules = new List<(string Rule, string Reason, string Evidence)>(LoadWhitelist(file));

            var accepted = 0;
            foreach (var (rule, reason, evidence) in entries)
            {
                if (rules.Any(r => string.Equals(r.Rule, rule, StringComparison.Ordinal)))
                {
                    continue;
                }
                rules.Add((rule, reason, evidence));
                accepted++;
            }
            if (accepted > 0)
            {
                var sb = new StringBuilder("[");
                for (var i = 0; i < rules.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"rule\":").Append(JsonSerializer.Serialize(rules[i].Rule));
                    sb.Append(",\"reason\":").Append(JsonSerializer.Serialize(rules[i].Reason));
                    sb.Append(",\"evidence\":").Append(JsonSerializer.Serialize(rules[i].Evidence));
                    sb.Append('}');
                }
                sb.Append(']');
                File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
            }
            return (accepted, rules.Count);
        }
        finally
        {
            _io.Release();
        }
    }

    private static IEnumerable<(string Rule, string Reason, string Evidence)> LoadWhitelist(string file)
    {
        var loaded = new List<(string Rule, string Reason, string Evidence)>();
        try
        {
            if (File.Exists(file))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in doc.RootElement.EnumerateArray())
                    {
                        var rule = Wire.Str(element, "rule");
                        if (rule.Length > 0)
                        {
                            loaded.Add((rule, Wire.Str(element, "reason"), Wire.Str(element, "evidence")));
                        }
                    }
                }
            }
        }
        catch
        {
            // 吞掉的是"白名单持久文件读不出或解析失败"；降级到"返回已加载的部分"（可能为空集）。
            // 方向是保守的：白名单缺失只会让守护更严格（少放行），不会放宽限制。
            // 何时应传播：不需要——启动时不得因历史文件损坏而拒绝服务。
        }
        return loaded;
    }

    // ───────────────────────────── 哈希基准 ─────────────────────────────

    /// <summary>
    /// 记录运行期确认过的哈希基准变更（3.4 六：自更新完成后，以下一次心跳上报的 versionHash 作为新的哈希基准）。
    /// <para>
    /// <b>启动校验不读本文件</b>：启动时的基准取自随发布包分发的 <c>Safe.data</c>
    /// （见 <see cref="GuardIntegrityBaseline"/>）。本机登记值与发布声明不同源，若以它为准，
    /// 篡改本机文件即可让被改过的运行体过关——那正是启动校验要防的事。
    /// 这里保留写入，是把它当作<b>运行期留痕</b>：事件日志是追加流水，本文件是"当前基准"的快照，
    /// 两者互为补充，也对应 6.2.1 列明的存储项。
    /// </para>
    /// </summary>
    public void SaveHashBaseline(string programId, string hash)
    {
        _io.Wait();
        try
        {
            var map = ReadMap(BaselineFile);
            map[programId] = hash;
            WriteMap(BaselineFile, map);
        }
        finally
        {
            _io.Release();
        }
    }

    // ───────────────────────────── 误报豁免 ─────────────────────────────

    /// <summary>
    /// 是否存在本地豁免（6.2.4 三）：豁免<b>仅</b>对同一规则与同一对象生效，不得扩大到规则全集。
    /// </summary>
    public bool HasExemption(string programId, string rule, string target)
    {
        _io.Wait();
        try
        {
            return ReadExemptions().Any(e =>
                string.Equals(e.ProgramId, programId, StringComparison.Ordinal) &&
                string.Equals(e.Rule, rule, StringComparison.Ordinal) &&
                string.Equals(e.Target, target, StringComparison.Ordinal));
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>登记一条误报豁免（须核对 Evidence 凭据）。</summary>
    public void AddExemption(string programId, string rule, string target, string evidence, string reason)
    {
        _io.Wait();
        try
        {
            var items = ReadExemptions();
            items.Add(new Exemption(programId, rule, target, evidence, reason, GuardClock.Now()));
            File.WriteAllText(ExemptionFile,
                JsonSerializer.Serialize(items, IndentedOptions),
                new UTF8Encoding(false));
        }
        finally
        {
            _io.Release();
        }
    }

    // ───────────────────────────── 保留期清理 ─────────────────────────────

    /// <summary>
    /// 删除超出保留期的事件分片（6.2.4 二：事件记录保留<b>至少 90 天</b>）。
    /// 按分片文件名里的日期判定，不逐行读内容——分片本身即按日切分。
    /// </summary>
    public int PurgeExpiredEvents(TimeSpan retention)
    {
        var cutoff = GuardClock.Now().Date - retention;
        _io.Wait();
        try
        {
            if (!Directory.Exists(EventsDir))
            {
                return 0;
            }
            var removed = 0;
            foreach (var file in Directory.GetFiles(EventsDir, "events-*.jsonl"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var datePart = name["events-".Length..];
                if (!DateTime.TryParseExact(datePart, "yyyyMMdd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var fileDate))
                {
                    continue;
                }
                if (fileDate >= cutoff)
                {
                    continue;
                }
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch
                {
                    // 吞掉的是"某个分片删不掉（被占用）"；降级到"保留该分片，下次再试"——
                    // 保留过头只占空间，删错则是不可逆的取证损失，方向必须偏向保留。
                }
            }
            return removed;
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>豁免文件的序列化选项（缩进输出，便于人工核对凭据）；缓存实例以复用（CA1869）。</summary>
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    private string BaselineFile => Path.Combine(Root, "baseline.json");

    private string ExemptionFile => Path.Combine(Root, "exemptions.json");

    private static Dictionary<string, string> ReadMap(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file, Encoding.UTF8))
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch
        {
            // 吞掉的是"基准文件读不出或解析失败"；降级到"视为无基准"。
            // 方向是保守的：无基准会让启动时的哈希校验退化为"首次登记"，而不是放行。
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static void WriteMap(string file, Dictionary<string, string> map)
        => File.WriteAllText(file, JsonSerializer.Serialize(map), new UTF8Encoding(false));

    private List<Exemption> ReadExemptions()
    {
        try
        {
            if (!File.Exists(ExemptionFile))
            {
                return new List<Exemption>();
            }
            return JsonSerializer.Deserialize<List<Exemption>>(File.ReadAllText(ExemptionFile, Encoding.UTF8))
                   ?? new List<Exemption>();
        }
        catch
        {
            return new List<Exemption>();
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
/// 一条误报豁免（6.2.4 三）：用户确认为误报后登记，仅对同一规则与同一对象生效。
/// </summary>
internal sealed class Exemption
{
    public Exemption(string programId, string rule, string target, string evidence, string reason, DateTimeOffset time)
    {
        ProgramId = programId;
        Rule = rule;
        Target = target;
        Evidence = evidence;
        Reason = reason;
        Time = time;
    }

    public string ProgramId { get; set; }
    public string Rule { get; set; }
    public string Target { get; set; }

    /// <summary>核对凭据（6.2.3 的 sha256 摘要）；登记时须与事件证据一致。</summary>
    public string Evidence { get; set; }

    public string Reason { get; set; }
    public DateTimeOffset Time { get; set; }
}
