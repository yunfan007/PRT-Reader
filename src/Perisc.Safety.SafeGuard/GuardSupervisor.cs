using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Perisc.Safety.SafeGuard;

/// <summary>
/// 宿主启动器的监督与终止执行（PSS 标准 3.4、6.2.2、6.2.4，规范性）。
/// <para>
/// 职责：① 记录心跳（<b>由子进程主动上报</b>，父进程不得轮询——3.4 三）；
/// ② 按七条规则编号判定越权；③ 命中后按固定三步执行：审计 → 通知 → 终止。
/// </para>
/// <para>
/// 心跳超时判定用单调计时（<see cref="Environment.TickCount64"/>），
/// 避免系统休眠或时钟调整造成误判（6.2.1）。
/// </para>
/// </summary>
internal sealed class GuardSupervisor : IDisposable
{
    /// <summary>通知窗口的宿主策略值（3.4：宿主可为某程序收紧，不得放宽超过 2000 ms）。</summary>
    public const int NoticeWindowCeilingMs = 2000;

    private sealed class Entry
    {
        public string ProgramId = "unknown-program";
        public int Pid;
        public long LastHeartbeatTicks;
        public TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
        public int NoticeWindowMs = 200;
        public bool ReProbeSent;
        public long ReProbeSentTicks;
        public bool Terminated;
        public string LastHeartbeatEvidence = string.Empty;
        public Func<TerminationNotice, Task>? Notify;
    }

    private readonly GuardStore _store;
    private readonly GuardJob _job;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Timer _patrol;
    private bool _disposed;

    public GuardSupervisor(GuardStore store, GuardJob job)
    {
        _store = store;
        _job = job;
        // 巡检周期取 1 s：远短于默认心跳周期 5 s，足以分辨「周期级」的丢失；
        // 更细的粒度只增加开销，不提高判定的准确性。
        _patrol = new Timer(_ => Patrol(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// 登记一个受监护的程序（5.7 guard.connect 成功后调用）。
    /// </summary>
    /// <param name="noticeWindowMs">程序声明的通知窗口；实际生效值 = min(声明值, 宿主策略值, 2000)。</param>
    public void Register(string programId, int pid, TimeSpan heartbeatInterval,
                         int noticeWindowMs, Func<TerminationNotice, Task>? notify)
    {
        var entry = new Entry
        {
            ProgramId = programId,
            Pid = pid,
            LastHeartbeatTicks = Environment.TickCount64,
            // 实际生效心跳周期 = min(程序声明值, 30 s)（2.1 限额表的硬上限）。
            HeartbeatInterval = heartbeatInterval > TimeSpan.FromSeconds(30)
                ? TimeSpan.FromSeconds(30)
                : heartbeatInterval,
            NoticeWindowMs = Math.Min(Math.Max(noticeWindowMs, 0), NoticeWindowCeilingMs),
            Notify = notify,
        };
        _entries[programId] = entry;
    }

    /// <summary>收到一次心跳（由子进程主动上报）。</summary>
    public void OnHeartbeat(string programId, string? versionHash)
    {
        if (!_entries.TryGetValue(programId, out var entry))
        {
            return;
        }
        entry.LastHeartbeatTicks = Environment.TickCount64;
        entry.ReProbeSent = false;
        // 6.2.3：HEARTBEAT-LOST 的证据取「最近一次心跳报文」的摘要。
        entry.LastHeartbeatEvidence = Hash(programId + "|" + (versionHash ?? string.Empty) + "|" + entry.LastHeartbeatTicks);
    }

    /// <summary>程序断开或退出时注销；已终止的不再重新纳入巡检。</summary>
    public void Unregister(string programId) => _entries.TryRemove(programId, out _);

    /// <summary>
    /// 命中某条终止规则时的统一入口（6.2.2）：规则编号必须取自 <see cref="GuardRules"/>，
    /// 自造编号会让 8.5 的一致性验证无法通过。
    /// </summary>
    public void Escalate(string programId, string rule, string target)
    {
        if (!GuardRules.IsTerminationRule(rule))
        {
            // 规则编号不在全集内：按实现缺陷处理，不执行终止——
            // 终止不可撤销，用未定义的规则去终止程序比不终止更危险。
            _store.AppendEvent(GuardRules.EventFalsePositive, programId, 0,
                "拒绝执行未定义的终止规则：" + rule);
            return;
        }
        if (!_entries.TryGetValue(programId, out var entry) || entry.Terminated)
        {
            return;
        }
        _ = TerminateAsync(entry, rule, target);
    }

    /// <summary>巡检：只对心跳丢失做判定；其余规则由各自的触发方经 <see cref="Escalate"/> 进入。</summary>
    private void Patrol()
    {
        if (_disposed)
        {
            return;
        }
        var now = Environment.TickCount64;
        foreach (var entry in _entries.Values)
        {
            if (entry.Terminated)
            {
                continue;
            }
            var intervalMs = (long)entry.HeartbeatInterval.TotalMilliseconds;
            var silentMs = now - entry.LastHeartbeatTicks;

            if (!entry.ReProbeSent)
            {
                // 连续 3 个心跳周期无心跳 → 发起一次宽限重探（只做一次）。
                if (silentMs >= intervalMs * 3)
                {
                    entry.ReProbeSent = true;
                    entry.ReProbeSentTicks = now;
                }
                continue;
            }

            // 重探发出后再过一个心跳周期仍无心跳 → 判定成立。
            // 合计最长约 4 个心跳周期（默认 5 s 时约 20 s），与 6.2.2 的参数表一致。
            if (silentMs >= intervalMs * 4)
            {
                entry.Terminated = true;
                _ = TerminateAsync(entry, GuardRules.HeartbeatLost, "pid:" + entry.Pid.ToString());
            }
        }
    }

    /// <summary>
    /// 处置三步（3.4 五，规范性，顺序不得改变）：① 审计先行 ② 通知 ③ 终止。
    /// </summary>
    private async Task TerminateAsync(Entry entry, string rule, string target)
    {
        // ① 记录审计：终止之前，记录必须已经落盘。
        // 顺序不能反——被终止的程序再也没法说明它当时在做什么，记录是它唯一的辩白机会。
        var evidence = string.Equals(rule, GuardRules.HeartbeatLost, StringComparison.Ordinal)
            ? entry.LastHeartbeatEvidence
            : Hash(target);
        var members = SafeMemberList();
        var (eventId, storedEvidence) = _store.AppendEvent(
            rule, entry.ProgramId, entry.Pid,
            $"规则命中：{rule}；对象：{target}；进程树成员：{string.Join(",", members)}");

        // ② 通知：窗口 = min(程序声明值, 宿主策略值, 2000)；0 表示不通知、直接终止。
        var window = entry.NoticeWindowMs;
        if (window > 0 && entry.Notify is not null)
        {
            var notice = new TerminationNotice(eventId, rule, target,
                GuardClock.Now().AddMilliseconds(window));
            try
            {
                // 通知是**发给**而不是**商量**：协议不提供「取消终止」的响应语义，
                // 故这里只等窗口届满，不等待也不解析任何回复。
                await Task.WhenAny(entry.Notify(notice), Task.Delay(window)).ConfigureAwait(false);
            }
            catch
            {
                // 吞掉的是"通知发送失败（管道已断）"；降级到"窗口照样届满后终止"——
                // 通知失败不构成暂缓终止的理由（3.4 第 3 步：不得等待程序确认）。
            }
        }

        // ③ 终止：立即、彻底，含全部后代进程；终止前实时重枚举一次（上面已取成员清单）。
        entry.Terminated = true;
        _job.TerminateAll();
        _ = storedEvidence;
    }

    private IReadOnlyList<int> SafeMemberList()
    {
        try
        {
            return _job.EnumerateProcessIds();
        }
        catch
        {
            return Array.Empty<int>();
        }
    }

    private static string Hash(string text)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(text), digest);
        return "sha256:" + Convert.ToHexString(digest).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _patrol.Dispose();
    }
}
