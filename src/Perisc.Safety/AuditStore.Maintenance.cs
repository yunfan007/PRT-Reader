using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Perisc.Safety;

// 元记录 / 待写日志 / 升级与永久不可用（AuditStore 的 partial；拆分依据见《工程改进方案》§4.8 / D-06）。
internal sealed partial class AuditStore : IDisposable
{
    /// <summary>发现损坏行后写一条元记录（每个进程只写一次）。</summary>
    private void ReportDamageOnce()
    {
        if (DamagedLines == 0)
        {
            return;
        }
        lock (_gate)
        {
            if (_damageReported)
            {
                return;
            }
            _damageReported = true;
        }

        AppendInternal(new AuditRecord(
            _programId(), action: string.Empty, target: string.Empty,
            AuditDecision.Allow, PssCode.Ok, AuditLayer.Arm,
            detail: $"audit-damaged-lines：读取时跳过 {DamagedLines} 行无法解析的记录（文件被外部改动或损坏）",
            // 时刻必须取存储注入的时钟（6.1.3）：默认会落 SafetyEnvironment.Clock（真实进程时钟），
            // 元记录会被路由进真实日期的分片而非当前分片，篡改留痕就与数据分片脱节了（2026-09-25 自检实踩）。
            time: _clock.Now()));
    }

    private void TryWriteFailureNotice(AuditRecord record)
    {
        // Deny 策略：只记一条失败审计（2.6.1）。这一条本身写不进时静默放弃——
        // 通道已判失败，再写失败通知只会产生"失败的通知"，没有可降级的状态。
        try
        {
            var notice = new AuditRecord(
                record.Program, record.Action, record.Target,
                AuditDecision.Deny, PssCode.AuditUnavailable, AuditLayer.Arm,
                detail: "audit-unavailable：审计写入失败，按 Deny 策略拒绝该行为",
                time: _clock.Now());
            _io.Wait();
            try
            {
                _shards.Append(notice.Time, AuditJson.Serialize(notice));
            }
            finally
            {
                _io.Release();
            }
        }
        catch
        {
            // 吞掉的是"失败通知也写不进"；降级状态已由 HandleWriteFailure 的计数承担
            // （连续 3 次即判永久不可用）。何时应传播：不需要——此处已无更下游的处置方。
        }
    }

    /// <summary>
    /// 把一行**追加**到待写日志（不重写整个文件）。
    /// <para>
    /// 改造前这里是 <c>File.WriteAllText</c> 整文件重写：每次入队都重写全队列，
    /// 队列越长代价越大（整体 O(n²)），且都在锁内。现改为只追加新增行，
    /// 队列达到容量上限时再做一次低频压缩。
    /// </para>
    /// <para>
    /// 落盘失败仍在字节级一侧吞掉并降级（内存队列不丢），理由与边界见
    /// <see cref="AuditShardWriter.AppendPending"/> 的说明——那里才是 <c>catch</c> 的所在。
    /// </para>
    /// </summary>
    private void PersistPendingJournal(string line)
    {
        _io.Wait();
        try
        {
            _shards.AppendPending(line);
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>
    /// 用内存队列**整体重写**待写日志（低频压缩路径）。
    /// 只在补写失败、队列中部被移除时调用（追加式日志无法表达中部删除）；
    /// 入队路径不调用它——那正是改造前 O(n²) 写入的来源。
    /// <para>先在 <c>_gate</c> 内取快照、再出锁写盘：写盘期间发生的增删不会让文件与内存不一致。</para>
    /// </summary>
    private void CompactPendingJournal()
    {
        List<string> lines;
        lock (_gate)
        {
            lines = new List<string>(_pending.Count);
            foreach (var (line, _) in _pending)
            {
                lines.Add(line);
            }
        }

        _io.Wait();
        try
        {
            _shards.WritePending(lines);
        }
        finally
        {
            _io.Release();
        }
    }

    private void DeletePendingFile()
    {
        _io.Wait();
        try
        {
            _shards.DeletePending();
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>当日日期戳（分片命名与完整性校验都按它取）。</summary>
    private string TodayStamp() => _clock.Now().ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>
    /// 计入损坏行数（由字节级读取方交回，见 <see cref="AuditShardWriter.ReadShard"/>）。
    /// <para>
    /// 用 <c>Interlocked</c> 而不是 <c>_gate</c>：这是一个只增不减的计数器，
    /// 而调用点有的在 <c>_io</c> 内、有的在锁外——为它去取状态锁只会平白扩大临界区。
    /// </para>
    /// </summary>
    private void AddDamaged(int count)
    {
        if (count > 0)
        {
            Interlocked.Add(ref _damagedLines, count);
        }
    }

    /// <summary>
    /// 接管遗留的待写队列文件（4.10 第 5 条：Dispose/崩溃不丢记录）。
    /// **调用方必须已持有 <c>_io</c>**——本方法只做磁盘读，不再取 <c>_io</c>，
    /// 否则 <c>SemaphoreSlim</c> 不可重入会自锁。
    /// <para>
    /// 读的是**上一次进程**留下的 <c>audit-buffer.jsonl</c>，因此它属于外部输入：
    /// 整份读不出（占用 / 权限 / 介质错误）与"文件里没有可解析的行"是两件事，
    /// 前者**不能删文件**——删掉是永久丢失，保留只是下次启动再试。
    /// 字节级一侧据此用 <c>readFailed</c> 把两种情形分开交回。
    /// </para>
    /// </summary>
    private void AdoptPendingFromDiskLocked()
    {
        if (!File.Exists(_shards.PendingFilePath))
        {
            return;
        }

        var entries = _shards.LoadPending(out var damaged, out var readFailed);
        AddDamaged(damaged);

        if (readFailed)
        {
            // 吞掉的是"遗留待写文件整份读不出"；降级到"这一批历史待写记录本次会话不可恢复"，
            // 但文件保留在磁盘上，下次启动继续尝试。
            // 何时应传播：不需要——启动路径不应因历史文件（可能来自被篡改或被占用的目录）失败。
            return;
        }

        if (entries.Count == 0)
        {
            // 待写文件为空或整份不可解析：删掉它（否则每次启动都会重新接管一串噪声）。
            // 这里直接调字节级一侧、不经 DeletePendingFile()——调用方已持 _io，_io 不可重入。
            _shards.DeletePending();
            return;
        }

        lock (_gate)
        {
            foreach (var entry in entries)
            {
                _pending.Enqueue(entry);
                _pendingBytes += entry.Line.Length;
            }
            _degradedSince ??= _clock.Now();
        }
    }

    /// <summary>置永久不可用（纯状态变更，不做 I/O）。返回 true 表示本次是首次判定。</summary>
    private bool MarkPermanentLocked()
    {
        if (_permanentUnavailable) return false;
        _permanentUnavailable = true;
        _degradedSince ??= _clock.Now();
        return true;
    }

    /// <summary>
    /// 置永久不可用（2.6 / 2.6.2）并尽力写一条 Escalation。
    /// **调用方不得持有 <c>_io</c> 或 <c>_gate</c>**（本方法内部会在锁外写记录）。
    /// </summary>
    private void MarkPermanent(string reason)
    {
        bool first;
        lock (_gate)
        {
            first = MarkPermanentLocked();
        }
        if (!first)
        {
            return;
        }

        // 审计通道永久不可用 = 安全设施被破坏：记 Escalation（尽力而为）并交守护处置。
        WriteEscalation("audit-unavailable",
            $"审计通道永久不可用（{reason}），按 AUDIT-CHANNEL-BROKEN 升级");
    }

    /// <summary>
    /// 写一条 Escalation 记录（2.6.1 / 2.6.2；Layer = Arm）：证据取被破坏对象（审计目录）的
    /// sha256 最小摘要（6.2.3）。**调用方不得持有 <c>_io</c>**；落盘失败不放大故障。
    /// </summary>
    private void WriteEscalation(string tag, string detail)
    {
        try
        {
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(Encoding.UTF8.GetBytes(Directory), digest);
            var escalation = new AuditRecord(
                _programId(), action: string.Empty, target: "audit-channel",
                AuditDecision.Escalation, PssCode.AuditUnavailable, AuditLayer.Arm,
                detail: $"{tag}：{detail}",
                evidence: "sha256:" + Convert.ToHexString(digest).ToLowerInvariant(),
                time: _clock.Now());
            _io.Wait();
            try
            {
                _shards.Append(escalation.Time, AuditJson.Serialize(escalation));
            }
            finally
            {
                _io.Release();
            }
        }
        catch
        {
            // 吞掉的是"升级记录写不进"；降级状态（_permanentUnavailable / _degradedSince）已在内存置位，
            // 补写循环与下一次判定会继续尝试。何时应传播：不需要——通道已不可用，
            // 抛出只会把"记录失败"变成"裁决失败"，与 1.5「不得以实现差异降低审计粒度」相悖。
        }
    }
}
