using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Perisc.Safety;

// 写入与补写（AuditStore 的 partial；拆分依据见《工程改进方案》§4.8 / D-06）。
internal sealed partial class AuditStore : IDisposable
{
    // ───────────────────────────── 写入 ─────────────────────────────

    /// <summary>
    /// 追加一条审计记录（4.4：同一调用栈内完成，不得异步滞后到进程退出）。
    /// 返回 Written / Buffered / Failed，由调用方按策略处置。
    /// </summary>
    public AuditWriteOutcome TryAppend(AuditRecord record)
    {
        var line = AuditJson.Serialize(record);

        lock (_gate)
        {
            if (_permanentUnavailable)
            {
                return AuditWriteOutcome.Failed;
            }
        }

        // 落盘：只取 _io，不持 _gate——磁盘慢时状态查询（State/Degraded/PendingCount）不受牵连。
        var failure = TryWriteToDisk(record, line);
        if (failure is null)
        {
            lock (_gate)
            {
                _consecutiveFailures = 0;
                _degradedSince = null;
            }
            return AuditWriteOutcome.Written;
        }

        var (outcome, escalateNow) = HandleWriteFailure(record);
        if (escalateNow)
        {
            // 在锁外回调，避免与宿主的锁形成环；异常不影响裁决与审计。
            try
            {
                Escalated?.Invoke("second-degradation");
            }
            catch
            {
                // 吞掉的是宿主回调抛出的异常；降级状态与审计记录已落地，回调用以"通知"而非"决定"。
                // 传播反而会把一次界面处置失败升级成审计链路故障，故此处不传播（4.10 第 1 条同一取向）。
            }
        }
        return outcome;
    }

    /// <summary>把一行写入当日分片；成功返回 null，失败返回异常（不抛给调用方）。</summary>
    private Exception? TryWriteToDisk(AuditRecord record, string line)
    {
        _io.Wait();
        try
        {
            _shards.Append(record.Time, line);
            return null;
        }
        catch (Exception ex)
        {
            // 吞掉的是磁盘异常本身，但**不丢弃信息**：异常随即交给失败策略结算（2.6.1），
            // 由 HandleWriteFailure 决定是拒绝该行为、入待写队列，还是判永久不可用。
            // 因此这里不记录日志也不算静默——失败是这条链路的正常输入之一。
            return ex;
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>
    /// 写入失败后的结算（2.6.1）。在 <c>_gate</c> 内只做状态与决策，
    /// 所有磁盘动作（失败通知、待写日志、升级记录）都推到锁外执行。
    /// </summary>
    /// <returns>写入结果，以及是否需要触发 Escalated 回调。</returns>
    private (AuditWriteOutcome Outcome, bool EscalateNow) HandleWriteFailure(AuditRecord record)
    {
        var needFailureNotice = false;
        string? pendingLine = null;
        var needEscalationRecord = false;
        var escalateNow = false;
        var permanentReason = string.Empty;
        AuditWriteOutcome outcome;

        lock (_gate)
        {
            _consecutiveFailures++;

            if (_policy == AuditFailurePolicy.Deny)
            {
                // Deny：仅记一条失败审计（detail 含 audit-unavailable），不产生行为记录（2.6.1）。
                needFailureNotice = true;
                if (_consecutiveFailures >= 3 && MarkPermanentLocked())
                {
                    permanentReason = "three-consecutive-failures";
                }
                outcome = AuditWriteOutcome.Failed;
            }
            else
            {
                // Buffer / Escalate：入待写队列（内存 + 落盘）。
                var degraded = (AuditRecord)record;
                degraded.Code = PssCode.AuditUnavailable;
                degraded.AuditDegraded = true;
                pendingLine = AuditJson.Serialize(degraded);
                _degradedSince ??= _clock.Now();
                _degradationEvents++;

                if (_pending.Count + 1 > MaxQueueEntries || _pendingBytes + pendingLine.Length > MaxQueueBytes)
                {
                    // 队列满：按 Deny 处理并升级判定为永久不可用（2.6.1 末条）。
                    if (MarkPermanentLocked())
                    {
                        permanentReason = "queue-full";
                    }
                    pendingLine = null;
                    outcome = AuditWriteOutcome.Failed;
                }
                else
                {
                    _pending.Enqueue((pendingLine, degraded));
                    _pendingBytes += pendingLine.Length;
                    if (_consecutiveFailures >= 3 && MarkPermanentLocked())
                    {
                        permanentReason = "three-consecutive-failures";
                    }

                    // 2.6.1 / Escalate：同一会话内**第二次**降级即把情形交模块三处置（Escalation）——
                    // 不能像 Buffer 一样只排队了事（否则守护服务永远收不到信号）。
                    if (_policy == AuditFailurePolicy.Escalate && _degradationEvents >= 2 && !_escalationRaised)
                    {
                        _escalationRaised = true;
                        escalateNow = true;
                        needEscalationRecord = true;
                    }

                    outcome = _permanentUnavailable ? AuditWriteOutcome.Failed : AuditWriteOutcome.Buffered;
                }
            }
        }

        // ── 锁外：全部磁盘动作。各自取 _io，绝不与 _gate 嵌套反向。──
        if (pendingLine is not null)
        {
            PersistPendingJournal(pendingLine);
        }
        if (needFailureNotice)
        {
            TryWriteFailureNotice(record);
        }
        if (permanentReason.Length > 0)
        {
            MarkPermanent(permanentReason);
        }
        if (needEscalationRecord)
        {
            WriteEscalation("audit-escalation",
                "同一会话内第二次审计降级（Escalate 策略），情形交模块三处置");
        }

        return (outcome, escalateNow);
    }

    /// <summary>
    /// 模块内部写入（元记录等），不经 Record 入参校验（6.1.1 末条）。
    /// 保留空 catch：6.1.6 规定元记录写不进不放大故障，交由补写循环与永久不可用判定处理。
    /// </summary>
    public void AppendInternal(AuditRecord record)
    {
        var line = AuditJson.Serialize(record);
        _io.Wait();
        try
        {
            _shards.Append(record.Time, line);
        }
        catch
        {
            // 吞掉的是元记录的磁盘异常；降级到"本次元记录缺失"，不影响业务裁决与既有审计。
            // 何时应传播：不需要——元记录本身不是合规内容，且通道健康度由失败计数独立跟踪。
        }
        finally
        {
            _io.Release();
        }
    }

    // ───────────────────────────── 补写 ─────────────────────────────

    private void TryBackfill()
    {
        // 与 Dispose 同锁判定：置位 _backfilling 与置位 _disposed 是两个互斥的事实，
        // 因此"开始一次补写"之后，Dispose 一定看得见 _backfilling = true 并等它退出。
        lock (_gate)
        {
            if (_disposed || _backfilling)
            {
                // 已释放（定时器回调可能仍在途）或上一次补写还没结束：丢弃本次触发。
                // 定时器周期可能短于一次补写的耗时，两条补写并行会互相清队列。
                return;
            }
            _backfilling = true;
        }
        try
        {
            BackfillCore();
        }
        finally
        {
            lock (_gate)
            {
                _backfilling = false;
            }
        }
    }

    private void BackfillCore()
    {
        List<(string Line, AuditRecord Record)> batch;
        lock (_gate)
        {
            if (_pending.Count == 0 || _permanentUnavailable)
            {
                return;
            }
            batch = new List<(string, AuditRecord)>(_pending);
        }

        var written = 0;
        var now = _clock.Now();
        var min = DateTimeOffset.MaxValue;
        var max = DateTimeOffset.MinValue;
        var integrityBroken = false;
        Exception? failure = null;

        _io.Wait();
        try
        {
            if (!_shards.VerifyIntegrity(TodayStamp()))
            {
                integrityBroken = true;
            }
            else
            {
                foreach (var (_, record) in batch)
                {
                    // 4.4：补写不得改写原始 time，只可附加 backfilledAt。
                    record.BackfilledAt = now;
                    var line = AuditJson.Serialize(record);
                    _shards.Append(record.Time, line);
                    written++;
                    if (record.Time < min) min = record.Time;
                    if (record.Time > max) max = record.Time;
                }

                // 已全部落盘：写元记录（4.4）。出队与清待写文件在出锁后统一做，
                // 避免"成功"与"失败"两条路径各出队一次（重复出队会误吞未落盘的记录）。
                var meta = new AuditRecord(
                    _programId(), action: string.Empty, target: string.Empty,
                    AuditDecision.Allow, PssCode.Ok, AuditLayer.Arm,
                    detail: $"audit-backfill：补写 {written} 条，时间范围 {min:yyyy-MM-dd'T'HH:mm:sszzz} ~ {max:yyyy-MM-dd'T'HH:mm:sszzz}",
                    time: now);
                _shards.Append(now, AuditJson.Serialize(meta));
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            _io.Release();
        }

        if (integrityBroken)
        {
            MarkPermanent("integrity-failed-before-backfill");
            return;
        }

        if (failure is null)
        {
            lock (_gate)
            {
                DropPendingLocked(written);
                _degradedSince = null;
                _consecutiveFailures = 0;
            }
            DeletePendingFile();
            return;
        }

        // 失败：已成功落盘的出队、未落盘的留在队列里并重写待写文件，
        // 既不丢记录（4.10 第 5 条），也不在下一轮重复补写已写过的记录。
        var permanent = false;
        lock (_gate)
        {
            DropPendingLocked(written);
            _consecutiveFailures++;
            permanent = _consecutiveFailures >= 3 ||
                        (_degradedSince is not null && _clock.Now() - _degradedSince >= PermanentAfter);
        }

        // 队列中部已被移除（已落盘的不再补写），追加式日志无法表达这种回退，必须整体重写。
        // 这是低频路径：入队路径只追加，整体重写已从热路径上移除（改造前每次入队都重写，整体 O(n²)）。
        CompactPendingJournal();
        if (permanent)
        {
            MarkPermanent("backfill-failure");
        }
    }

    /// <summary>从待写队列头部移除已成功补写的记录（调用方必须已持有 <c>_gate</c>）。</summary>
    private void DropPendingLocked(int count)
    {
        for (var i = 0; i < count && _pending.Count > 0; i++)
        {
            var item = _pending.Dequeue();
            _pendingBytes -= item.Line.Length;
        }
        if (_pendingBytes < 0)
        {
            _pendingBytes = 0;
        }
    }
}
