using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shell;
using Perisc.Safety;
using Prt.App.Models;
using Prt.App.Rendering;
using Prt.App.Views;
using Prt.Core;
using Prt.Core.Diagnostics;
using Prt.Core.Export;
using Prt.Core.Rendering;
using Prt.Core.Syntax;

namespace Prt.App.Services;

/// <summary>自检用例组：审计落盘 / 损坏行 / 并发 / 降级 / 接管（<see cref="SelfTest"/> 的 partial）。</summary>
internal static partial class SelfTest
{
    /// <summary>自检用例组：审计落盘 / 损坏行 / 并发 / 降级 / 接管。</summary>
    private static void RunAuditCases(CaseRunner runner)
    {
        // 35. 审计落盘：顺序与调用一致，且校验文件同步生成（4.4 / 6.1.6）。
        //     回归点——改造前待写队列每次入队都整文件重写（整体 O(n²)）且全程持状态锁；
        //     改成追加式之后，落盘顺序仍必须与调用顺序一致（审计文件的"时间升序"是查询前提）。
        runner.Check("审计落盘：顺序与调用一致且校验文件同步生成", () => AuditCase35());

        // 36. 审计损坏行：跳过 + 计数 + 留痕 + 不抛异常（D-18 / 6.1.6）。
        //     回归点——改造前用 DateTimeOffset.Parse 解析审计文件的每一行，异常被外层空 catch 吞掉，
        //     结果是"这行不见了"却没有任何痕迹：审计文件被改过也看不出来。
        runner.Check("审计损坏行：跳过、计数并留痕（D-18）", () => AuditCase36());

        // 37. 审计并发：状态查询不与磁盘 I/O 争同一把锁（D-28 的可观测效果）。
        //     回归点——改造前磁盘写入在状态锁内进行，磁盘一慢，连"读一下待写条数"都要排队等它。
        runner.Check("审计并发：状态查询不被磁盘 I/O 拖住（D-28）", () => AuditCase37());

        // 38. 审计写入失败：入待写队列、降级可见、释放后不丢（2.6.1 / 4.10 第 5 条）。
        //     造一个必然写不进的落点：把审计目录指到"以文件充当目录分量"的非法路径。
        runner.Check("审计写入失败：降级可见且释放不丢队列（2.6.1 / 4.10 第 5 条）", () => AuditCase38());

        // 39. 遗留待写队列接管（4.10 第 5 条）：崩溃 / 断电后未落盘的记录，下次启动要接着写。
        //     同时验"待写缓冲不被当成当日分片读入"——audit-buffer.jsonl 也匹配 audit-*.jsonl 的通配模式。
        runner.Check("审计遗留待写队列被接管且缓冲不被当作分片（4.10 第 5 条）", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "prt-selftest-audit-" + Guid.NewGuid().ToString("N"));
            var manual = new ManualClock(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.FromHours(8)));
            try
            {
                Fixture(SafeRuntime.File.CreateDirectory(dir, "创建自检临时目录"));

                // 预置一份"上次进程留下的"待写日志：两行合法 + 一行损坏。
                var pending = new StringBuilder();
                for (var i = 0; i < 2; i++)
                {
                    pending.AppendLine(AuditJson.Serialize(new AuditRecord(
                        "selftest", "FS-01", "C:/selftest.txt", AuditDecision.Allow, PssCode.AuditUnavailable,
                        AuditLayer.Arm, detail: "buffered-" + i.ToString(CultureInfo.InvariantCulture),
                        auditDegraded: true, time: manual.Now())));
                }
                pending.AppendLine("{ 损坏的一行");
                Fixture(SafeRuntime.File.WriteText(Path.Combine(dir, "audit-buffer.jsonl"), pending.ToString(), "预置待写审计缓冲"));

                using var store = new AuditStore(AuditFailurePolicy.Buffer, manual, () => "selftest", dir);
                store.Initialize();

                if (store.PendingCount != 2)
                {
                    throw new InvalidOperationException($"接管到的待写条数应为 2，实际 {store.PendingCount}");
                }
                if (store.DamagedLines != 1)
                {
                    throw new InvalidOperationException($"待写日志里的损坏行应计 1 行，实际 {store.DamagedLines}");
                }

                // 缓冲是"尚未落盘"，不得出现在查询结果里——否则用户看到的审计会混进一批没落地的东西。
                var records = store.Query(new AuditQuery());
                if (records.Count != 0)
                {
                    throw new InvalidOperationException(
                        $"待写缓冲被当作分片读入：查询返回了 {records.Count} 条尚未落盘的记录");
                }

                return "接管 2 条待写记录、计 1 行损坏；缓冲未被当作分片读入";
            }
            finally
            {
                try
                {
                    Fixture(SafeRuntime.File.DeleteDirectory(dir, recursive: true, reason: "清理自检临时目录"));
                }
                catch
                {
                    // 清理失败不影响结论。
                }
            }
        });
    }

    /// <summary>用例 35：审计落盘：顺序与调用一致且校验文件同步生成</summary>
    private static string AuditCase35()
    {
            var dir = Path.Combine(Path.GetTempPath(), "prt-selftest-audit-" + Guid.NewGuid().ToString("N"));
            var manual = new ManualClock(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.FromHours(8)));
            try
            {
                Fixture(SafeRuntime.File.CreateDirectory(dir, "创建自检临时目录"));
                using var store = new AuditStore(AuditFailurePolicy.Buffer, manual, () => "selftest", dir);
                store.Initialize();

                const int count = 100;
                for (var i = 0; i < count; i++)
                {
                    manual.Advance(TimeSpan.FromMilliseconds(10));
                    var outcome = store.TryAppend(new AuditRecord(
                        "selftest", "FS-01", "C:/selftest.txt", AuditDecision.Allow, PssCode.Ok, AuditLayer.Arm,
                        detail: "seq-" + i.ToString("D3", CultureInfo.InvariantCulture), time: manual.Now()));
                    if (outcome != AuditWriteOutcome.Written)
                    {
                        throw new InvalidOperationException($"第 {i} 条未即时落盘：{outcome}");
                    }
                }

                var shards = Fixture(SafeRuntime.File.EnumerateFiles(dir, "audit-*.jsonl", "枚举审计分片"));
                if (shards.Length != 1)
                {
                    throw new InvalidOperationException($"当日分片数量异常：{shards.Length}");
                }
                if (!Fixture(SafeRuntime.File.Exists(Path.ChangeExtension(shards[0], ".sha256"))))
                {
                    throw new InvalidOperationException("分片没有配套校验文件（6.1.6 完整性的前提）");
                }

                var order = new List<string>();
                foreach (var line in Fixture(SafeRuntime.File.ReadLines(shards[0])))
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }
                    if (!AuditJson.TryDeserialize(line, out var record))
                    {
                        throw new InvalidOperationException("自检写入的审计行反解析失败：" + line);
                    }
                    order.Add(record.Detail);
                }

                if (order.Count != count)
                {
                    throw new InvalidOperationException($"落盘条数 {order.Count}，期望 {count}");
                }
                for (var i = 0; i < count; i++)
                {
                    var expected = "seq-" + i.ToString("D3", CultureInfo.InvariantCulture);
                    if (!string.Equals(order[i], expected, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"第 {i} 条落盘顺序不符：{order[i]}，期望 {expected}");
                    }
                }

                return $"{count} 条按调用顺序落盘，分片与校验文件同步生成";
            }
            finally
            {
                try
                {
                    Fixture(SafeRuntime.File.DeleteDirectory(dir, recursive: true, reason: "清理自检临时目录"));
                }
                catch
                {
                    // 清理失败不影响结论。
                }
            }
    }

    /// <summary>用例 36：审计损坏行：跳过、计数并留痕（D-18）</summary>
    private static string AuditCase36()
    {
            var dir = Path.Combine(Path.GetTempPath(), "prt-selftest-audit-" + Guid.NewGuid().ToString("N"));
            var manual = new ManualClock(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.FromHours(8)));
            try
            {
                Fixture(SafeRuntime.File.CreateDirectory(dir, "创建自检临时目录"));
                using (var writer = new AuditStore(AuditFailurePolicy.Buffer, manual, () => "selftest", dir))
                {
                    writer.Initialize();
                    writer.TryAppend(new AuditRecord(
                        "selftest", "FS-01", "C:/selftest.txt", AuditDecision.Allow, PssCode.Ok, AuditLayer.Arm,
                        detail: "good-1", time: manual.Now()));
                    manual.Advance(TimeSpan.FromSeconds(1));
                    writer.TryAppend(new AuditRecord(
                        "selftest", "FS-01", "C:/selftest.txt", AuditDecision.Allow, PssCode.Ok, AuditLayer.Arm,
                        detail: "good-2", time: manual.Now()));
                }

                var shard = Fixture(SafeRuntime.File.EnumerateFiles(dir, "audit-*.jsonl", "枚举审计分片"))[0];
                // 三类损坏：根本不是 JSON、time 不是时刻、time 是数字（字段类型不符）。
                Fixture(SafeRuntime.File.AppendText(shard, "{ 这不是 JSON\n", "在分片里植入损坏行"));
                Fixture(SafeRuntime.File.AppendText(shard, "{\"time\":\"不是时刻\",\"program\":\"x\"}\n", "在分片里植入损坏行"));
                Fixture(SafeRuntime.File.AppendText(shard, "{\"time\":12345,\"program\":\"x\"}\n", "在分片里植入损坏行"));

                // 此处**不**调 Initialize：分片已被改动，完整性校验会（正确地）判为不完整并触发升级，
                // 那会再往分片里写记录，干扰本用例的计数。这里只验"读的时候怎么处理坏行"。
                using var reader = new AuditStore(AuditFailurePolicy.Buffer, manual, () => "selftest", dir);
                var records = reader.Query(new AuditQuery());

                if (records.Count != 2)
                {
                    throw new InvalidOperationException($"完好记录应为 2 条，实际 {records.Count} 条");
                }
                if (reader.DamagedLines != 3)
                {
                    throw new InvalidOperationException($"损坏行计数应为 3，实际 {reader.DamagedLines}");
                }

                // 元记录必须已经落到分片里——"文件被改过"这件事要留痕，否则篡改无从察觉。
                var reported = false;
                foreach (var line in Fixture(SafeRuntime.File.ReadLines(shard)))
                {
                    if (line.Contains("audit-damaged-lines", StringComparison.Ordinal))
                    {
                        reported = true;
                    }
                }
                if (!reported)
                {
                    throw new InvalidOperationException("发现损坏行后未写入 audit-damaged-lines 元记录");
                }

                return "3 行损坏被跳过并计数，元记录已留痕，查询未抛异常";
            }
            finally
            {
                try
                {
                    Fixture(SafeRuntime.File.DeleteDirectory(dir, recursive: true, reason: "清理自检临时目录"));
                }
                catch
                {
                    // 清理失败不影响结论。
                }
            }
    }

    /// <summary>用例 37：审计并发：状态查询不被磁盘 I/O 拖住（D-28）</summary>
    private static string AuditCase37()
    {
            var dir = Path.Combine(Path.GetTempPath(), "prt-selftest-audit-" + Guid.NewGuid().ToString("N"));
            var manual = new ManualClock(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.FromHours(8)));
            try
            {
                Fixture(SafeRuntime.File.CreateDirectory(dir, "创建自检临时目录"));

                // 造一份足够大的分片，让"一次扫描"真的持续一段时间（约数百毫秒）。
                // 否则扫描瞬间结束，本用例失去区分力。
                const int filler = 40000;
                var stamp = manual.Now().LocalDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                var shard = Path.Combine(dir, $"audit-{stamp}.jsonl");
                var text = new StringBuilder(filler * 192);
                for (var i = 0; i < filler; i++)
                {
                    text.AppendLine(AuditJson.Serialize(new AuditRecord(
                        "selftest", "FS-01", "C:/filler.txt", AuditDecision.Allow, PssCode.Ok, AuditLayer.Arm,
                        detail: "filler-" + i.ToString("D6", CultureInfo.InvariantCulture), time: manual.Now())));
                }
                Fixture(SafeRuntime.File.WriteText(shard, text.ToString(), "预置审计并发用例的大分片"));

                using var store = new AuditStore(AuditFailurePolicy.Buffer, manual, () => "selftest", dir);

                var scanned = -1;
                var ioMs = 0.0;
                Exception? workerError = null;
                var worker = new Thread(() =>
                {
                    try
                    {
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        scanned = store.Query(new AuditQuery()).Count;
                        watch.Stop();
                        ioMs = watch.Elapsed.TotalMilliseconds;
                    }
                    catch (Exception ex)
                    {
                        workerError = ex;
                    }
                })
                {
                    IsBackground = true,
                };
                worker.Start();

                // 采样：每 256 次取一个，既覆盖整个扫描窗口，也不必存下全部样本。
                var latencies = new List<long>(8192);
                var iterations = 0;
                while (worker.IsAlive)
                {
                    iterations++;
                    if ((iterations & 0xFF) != 0)
                    {
                        continue;
                    }

                    var tick = System.Diagnostics.Stopwatch.GetTimestamp();
                    _ = store.PendingCount;
                    _ = store.Degraded;
                    _ = store.HasPendingBacklog;
                    var micros = (System.Diagnostics.Stopwatch.GetTimestamp() - tick) * 1_000_000L
                                 / System.Diagnostics.Stopwatch.Frequency;
                    if (latencies.Count < 8192)
                    {
                        latencies.Add(micros);
                    }
                }
                worker.Join();

                if (workerError is not null)
                {
                    throw new InvalidOperationException("并发扫描抛出异常：" + workerError.Message);
                }
                if (scanned != filler)
                {
                    throw new InvalidOperationException($"扫描条数 {scanned}，期望 {filler}");
                }
                if (latencies.Count < 200)
                {
                    throw new InvalidOperationException($"并发窗口过短（仅采样 {latencies.Count} 次），本用例无法判定");
                }

                // 取 p95 而非最大值：偶发的一次线程调度延迟不代表锁被争用。
                // 若状态查询与磁盘 I/O 共用一把锁，则**落在扫描窗口内的每一次**读取都要等整次扫描结束，
                // p95 会直接跳到与扫描同量级——这正是本用例要区分的情形。
                latencies.Sort();
                var p95 = latencies[(int)(latencies.Count * 0.95)];
                if (ioMs >= 20 && (p95 > 20_000 || p95 > ioMs * 1000.0 * 0.25))
                {
                    throw new InvalidOperationException(
                        $"状态查询被磁盘 I/O 拖住：p95 {p95 / 1000.0:0.0} ms，" +
                        $"占整次扫描（{ioMs:0} ms）的 {p95 / (ioMs * 1000.0):P0}——两者仍在同一临界区");
                }

                return $"扫描 {filler} 条耗时 {ioMs:0} ms；其间采样 {latencies.Count} 次状态查询，" +
                       $"p95 {p95 / 1000.0:0.00} ms、最慢 {latencies[^1] / 1000.0:0.00} ms（未随 I/O 阻塞）";
            }
            finally
            {
                try
                {
                    Fixture(SafeRuntime.File.DeleteDirectory(dir, recursive: true, reason: "清理自检临时目录"));
                }
                catch
                {
                    // 清理失败不影响结论。
                }
            }
    }

    /// <summary>用例 38：审计写入失败：降级可见且释放不丢队列（2.6.1 / 4.10 第 5 条）</summary>
    private static string AuditCase38()
    {
            var root = Path.Combine(Path.GetTempPath(), "prt-selftest-audit-" + Guid.NewGuid().ToString("N"));
            var manual = new ManualClock(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.FromHours(8)));
            try
            {
                Fixture(SafeRuntime.File.CreateDirectory(root, "创建自检临时目录"));
                var blocker = Path.Combine(root, "blocker");
                Fixture(SafeRuntime.File.WriteText(blocker, "x", "造一个以文件充当目录分量的落点"));
                var dir = Path.Combine(blocker, "audit");

                var store = new AuditStore(AuditFailurePolicy.Buffer, manual, () => "selftest", dir);
                store.Initialize();

                var outcome = store.TryAppend(new AuditRecord(
                    "selftest", "FS-01", "C:/selftest.txt", AuditDecision.Allow, PssCode.Ok, AuditLayer.Arm,
                    detail: "degraded-1", time: manual.Now()));

                if (outcome != AuditWriteOutcome.Buffered)
                {
                    throw new InvalidOperationException($"写入失败时期望 Buffered（Buffer 策略），实际 {outcome}");
                }
                if (store.PendingCount != 1)
                {
                    throw new InvalidOperationException($"待写条数应为 1，实际 {store.PendingCount}");
                }
                if (!store.Degraded)
                {
                    throw new InvalidOperationException("写入失败后状态未置为降级（6.1.4）");
                }
                if (!store.HasPendingBacklog)
                {
                    throw new InvalidOperationException("待写队列非空，但 HasPendingBacklog 为假");
                }

                store.Dispose();
                store.Dispose(); // 幂等
                if (store.PendingCount != 1)
                {
                    throw new InvalidOperationException(
                        $"释放丢弃了待写队列（剩 {store.PendingCount} 条）——违反 4.10 第 5 条");
                }

                return "失败判为 Buffered、降级状态可见；两次释放后待写队列仍为 1 条";
            }
            finally
            {
                try
                {
                    Fixture(SafeRuntime.File.DeleteDirectory(root, recursive: true, reason: "清理自检临时目录"));
                }
                catch
                {
                    // 清理失败不影响结论。
                }
            }
    }
}
