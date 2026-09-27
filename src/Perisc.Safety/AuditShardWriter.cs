using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Perisc.Safety;

/// <summary>
/// 审计分片的**字节级读写**（6.1.6）：分片文件、校验文件、待写日志的落盘与读回。
/// <para>
/// 为什么从 <c>AuditStore</c> 里切出来（9.6.4 / D-06）：原先一个类型同时承担
/// 「失败策略状态机」与「分段文件 + 区间哈希 + 滚动片 + 待写日志」四件事，
/// 前者是判定与降级，后者是纯字节搬运，二者的变更理由完全不同。
/// 切开之后，本类只认路径与字节，不认识"策略""降级""永久不可用"这些概念。
/// </para>
/// <para>
/// <b>并发契约：本类的所有方法都不取任何锁</b>。审计目录的独占闸门（<c>AuditStore._io</c>）
/// 由调用方持有——闸门的所有权留在状态机一侧，这样"谁在持锁做 I/O"只有一处需要看，
/// 也避免了闸门被两个类型各自持有一次（<c>SemaphoreSlim</c> 不可重入，重复取会自锁）。
/// </para>
/// <para>
/// <b>为什么读方法用 <c>out</c> 而不改写字段</b>：损坏行计数是"审计被外部改过"的证据，
/// 属于状态机的事实（<c>AuditStore.DamagedLines</c>），不该由搬运字节的类顺手去改。
/// 因此 <see cref="ReadShard"/> 与 <see cref="LoadPending"/> 把损坏行数**返回**给调用方。
/// </para>
/// </summary>
internal sealed class AuditShardWriter
{
    private const long MaxShardBytes = 8L * 1024 * 1024; // 单日分片滚动阈值

    private readonly string _directory;

    public AuditShardWriter(string directory) => _directory = directory;

    /// <summary>审计目录（6.1.6 参考路径的落点）。</summary>
    public string Directory => _directory;

    // ───────────────────────────── 路径 ─────────────────────────────

    /// <summary>按记录时刻取分片日期戳（本地日期，与文件名一致）。</summary>
    public static string ShardStamp(DateTimeOffset time)
        => time.LocalDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>基础分片路径（<c>audit-YYYYMMDD.jsonl</c>）。</summary>
    public string ShardPath(string stamp) => Path.Combine(_directory, $"audit-{stamp}.jsonl");

    /// <summary>待写日志路径（<c>audit-buffer.jsonl</c>）。</summary>
    public string PendingFilePath => Path.Combine(_directory, "audit-buffer.jsonl");

    /// <summary>当日全部分片（含滚动片）。返回 List 而非惰性序列：调用方会遍历它，避免 D-26 的重复枚举。</summary>
    public IReadOnlyList<string> ShardFiles() => EnumerateShards("audit-*.jsonl");

    /// <summary>指定日期的全部分片（含滚动片，如 <c>audit-20260924-1.jsonl</c>）。</summary>
    public IReadOnlyList<string> ShardFilesForStamp(string stamp) => EnumerateShards($"audit-{stamp}*.jsonl");

    private IReadOnlyList<string> EnumerateShards(string pattern)
    {
        try
        {
            var files = new List<string>();
            foreach (var file in System.IO.Directory.GetFiles(_directory, pattern))
            {
                // 通配模式挡不住待写缓冲：audit-buffer.jsonl 同样匹配 audit-*.jsonl。
                // 必须按名字再筛一道，否则 Query 会把**尚未落盘**的降级记录当成已落盘审计返回，
                // 用户看到的"审计"里会混进一批其实还在缓冲区里的记录。
                if (IsShardFile(Path.GetFileName(file)))
                {
                    files.Add(file);
                }
            }
            return files;
        }
        catch
        {
            // 吞掉的是"目录列不出"（目录不存在或权限不足）；降级到空集合 = 视为当日无审计，
            // 与"当日还没有分片"是同一可观测状态，调用方无需区分。
            // 何时应传播：不需要——读取失败不应让查询本身失败（6.1.1 要求以返回值表达结果）。
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 判定文件名是否为分片：<c>audit-YYYYMMDD.jsonl</c> 或 <c>audit-YYYYMMDD-N.jsonl</c>。
    /// 待写缓冲 <c>audit-buffer.jsonl</c> 与审计导出文件都可能落在同一目录，一律排除。
    /// </summary>
    private static bool IsShardFile(string name)
    {
        const string prefix = "audit-";
        const string extension = ".jsonl";
        if (name.Length < prefix.Length + 8 + extension.Length ||
            !name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (var i = prefix.Length; i < prefix.Length + 8; i++)
        {
            if (!char.IsAsciiDigit(name[i]))
            {
                return false;
            }
        }

        var rest = name[(prefix.Length + 8)..];
        if (string.Equals(rest, extension, StringComparison.OrdinalIgnoreCase))
        {
            return true; // 基础片
        }

        // 滚动片：-N.jsonl（N 为纯数字）
        return rest.Length > extension.Length + 1
            && rest[0] == '-'
            && rest.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            && rest[1..^extension.Length].All(char.IsAsciiDigit);
    }

    public void EnsureDirectory()
    {
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
        }
        catch
        {
            // 吞掉的是"建目录失败"（权限/磁盘满/路径被占）；降级到"首次写入时按失败处理"，
            // 由 Append 的异常走 2.6.1 策略，因此这里不必重复上报。
            // 何时应传播：不需要——此处无调用方可承接，抛出只会把"可降级"变成"启动失败"。
        }
    }

    // ───────────────────────────── 写 ─────────────────────────────

    /// <summary>
    /// 追加一行到当日分片，并把"本次追加的字节区间哈希 + 区间"追加到校验文件（6.1.6）。
    /// **调用方必须已持有审计目录的闸门**。
    /// </summary>
    public void Append(DateTimeOffset time, string line)
    {
        var stamp = ShardStamp(time);
        var file = ShardPath(stamp);
        var fi = new FileInfo(file);
        if (fi.Exists && fi.Length > MaxShardBytes)
        {
            // 滚动新片（后缀 -1、-2…），不得因分片丢失记录（6.1.6）。
            var roll = 1;
            while (File.Exists(Path.Combine(_directory, $"audit-{stamp}-{roll}.jsonl"))) roll++;
            file = Path.Combine(_directory, $"audit-{stamp}-{roll}.jsonl");
        }

        long start;
        long end;
        using (var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            start = stream.Length;
            writer.WriteLine(line);
            writer.Flush();          // D-11b：写流必须显式 Flush，否则崩溃时丢数据
            end = stream.Length;
        }

        // 校验文件：逐条记录「本次追加的字节区间的哈希 + 区间」（6.1.6 参考方式的等效实现，
        // 校验数据不与数据文件同体存放；分片 audit-YYYYMMDD[-N].jsonl ↔ audit-YYYYMMDD[-N].sha256）。
        // 只哈希新增区间：若每次都重算整片，分片增大后单次追加退化为 O(片长)（整体 O(n²)）。
        var hashPath = ChecksumPathFor(file);
        var length = (int)(end - start);
        var buffer = new byte[length];
        using (var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            source.Position = start;
            source.ReadExactly(buffer, 0, length);
        }
        var digest = SHA256.HashData(buffer);
        File.AppendAllText(hashPath, $"{Convert.ToHexString(digest).ToLowerInvariant()} {start}-{end}{Environment.NewLine}", new UTF8Encoding(false));
    }

    private static string ChecksumPathFor(string shardFile) => Path.Combine(
        Path.GetDirectoryName(shardFile)!,
        Path.GetFileNameWithoutExtension(shardFile) + ".sha256");

    // ───────────────────────────── 读 ─────────────────────────────

    /// <summary>
    /// 读取一个分片内的全部记录。
    /// <paramref name="damaged"/> 回传跳过的损坏行数：单行不可解析时跳过该行
    /// （否则一行坏数据会让整次查询失败），但必须让调用方知道"跳过了多少行"（D-18）。
    /// </summary>
    public static IReadOnlyList<AuditRecord> ReadShard(string file, out int damaged)
    {
        var records = new List<AuditRecord>();
        var broken = 0;
        try
        {
            foreach (var line in File.ReadAllLines(file))
            {
                if (line.Length == 0) continue;
                if (AuditJson.TryDeserialize(line, out var record))
                {
                    records.Add(record);
                }
                else
                {
                    broken++;
                }
            }
        }
        catch
        {
            // 吞掉的是"分片读不出"；降级到该分片按空处理。完整性校验另行报告（VerifyIntegrity）。
            // 何时应传播：不需要——审计读取是只读路径，失败不得影响裁决与写入（4.4 只规定写入语义）。
        }
        damaged = broken;
        return records;
    }

    // ───────────────────────────── 完整性 ─────────────────────────────

    /// <summary>完整性校验（2.6.2 第 4 条）：当日各分片与校验文件逐区间重读比对。</summary>
    public bool VerifyIntegrity(string stamp)
    {
        try
        {
            if (!System.IO.Directory.Exists(_directory))
            {
                return true;
            }
            var files = ShardFilesForStamp(stamp);
            if (files.Count == 0)
            {
                return true; // 当日还没有分片
            }

            foreach (var file in files)
            {
                var hashPath = ChecksumPathFor(file);
                if (!File.Exists(hashPath))
                {
                    return new FileInfo(file).Length == 0; // 有数据无校验 = 被篡改
                }

                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                var checkedRanges = 0;
                foreach (var line in File.ReadAllLines(hashPath))
                {
                    if (line.Length == 0) continue;
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2 || parts[0].Length != 64) return false;
                    var range = parts[1].Split('-');
                    if (range.Length != 2 || !long.TryParse(range[0], out var from) || !long.TryParse(range[1], out var to)) return false;
                    if (from < 0 || to < from || stream.Length < to) return false;

                    // 只读取并哈希该区间：整体代价与分片大小同阶（不再是 O(n²)）。
                    stream.Position = from;
                    using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var remaining = to - from;
                    var buffer = new byte[(int)Math.Min(Math.Max(remaining, 1), 8192)];
                    while (remaining > 0)
                    {
                        var want = (int)Math.Min(buffer.Length, remaining);
                        stream.ReadExactly(buffer, 0, want);
                        hasher.AppendData(buffer, 0, want);
                        remaining -= want;
                    }
                    if (!Convert.ToHexString(hasher.GetHashAndReset()).Equals(parts[0], StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                    checkedRanges++;
                }
                if (checkedRanges == 0 && new FileInfo(file).Length > 0)
                {
                    return false; // 有数据却没有任何校验区间
                }
            }
            return true;
        }
        catch
        {
            // 吞掉的是校验过程中的磁盘/IO 异常；降级到"判为不完整"（return false）。
            // 这是**保守方向**：宁可把完好文件误判为不完整（触发升级），
            // 也不把已损坏的文件当成完好（那会让 2.6.2 的整套判定失去意义）。
            return false;
        }
    }

    public void RebuildChecksum(string stamp)
    {
        // 清空后重建当日各分片的校验文件（Purge 用）：整片压成一条区间记录（等效且紧凑）。
        try
        {
            if (!System.IO.Directory.Exists(_directory)) return;
            foreach (var file in ShardFilesForStamp(stamp))
            {
                var hashPath = ChecksumPathFor(file);
                using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                var digest = SHA256.HashData(source);
                File.WriteAllText(hashPath, $"{Convert.ToHexString(digest).ToLowerInvariant()} 0-{source.Length}{Environment.NewLine}", new UTF8Encoding(false));
            }
        }
        catch
        {
            // 吞掉的是"校验文件重建失败"；降级到"该分片的校验区间与实际不符"，
            // 下一次 VerifyIntegrity 会据此判为不完整（保守方向，同上）。
        }
    }

    // ───────────────────────────── 待写日志 ─────────────────────────────

    /// <summary>
    /// 把一行**追加**到待写日志（不重写整个文件）。
    /// <para>
    /// 改造前这里是 <c>File.WriteAllText</c> 整文件重写：每次入队都重写全队列，
    /// 队列越长代价越大（整体 O(n²)）。现改为只追加新增行，队列达到容量上限时才做一次低频压缩。
    /// </para>
    /// </summary>
    public void AppendPending(string line)
    {
        try
        {
            File.AppendAllText(PendingFilePath, line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch
        {
            // 吞掉的是"待写日志落盘失败"；降级到"内存队列仍在"，进程存活期内记录不丢，
            // 但崩溃时会丢这一批（4.10 第 5 条的边界）。持续失败由失败计数推向永久不可用判定。
            // 何时应传播：不需要——磁盘故障已由 2.6.2 的判定路径承接。
        }
    }

    /// <summary>
    /// 用给定内容**整体重写**待写日志（低频压缩路径）。
    /// 只在补写失败、队列中部被移除时调用（追加式日志无法表达中部删除）；
    /// 入队路径不调用它——那正是改造前 O(n²) 写入的来源。
    /// </summary>
    public void WritePending(IEnumerable<string> lines)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var line in lines)
            {
                sb.AppendLine(line);
            }
            File.WriteAllText(PendingFilePath, sb.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 吞掉的是"压缩落盘失败"；降级到"日志内容比内存队列多出已补写过的行"——
            // 下次启动会重复补写这部分。重复写审计优于丢记录（与 4.10 第 5 条同向）。
        }
    }

    public void DeletePending()
    {
        try
        {
            if (File.Exists(PendingFilePath))
            {
                File.Delete(PendingFilePath);
            }
        }
        catch
        {
            // 吞掉的是"待写文件删不掉"；降级到"下次启动会把它当遗留队列接管"——
            // 重复补写的代价小于丢记录，故这一侧偏向保守（与 4.10 第 5 条一致）。
        }
    }

    /// <summary>
    /// 读回遗留的待写日志。**调用方必须已持有审计目录的闸门**（本方法只做磁盘读，不再取锁，
    /// 否则 <c>SemaphoreSlim</c> 不可重入会自锁）。
    /// <para>
    /// 只负责"读出来"，**不负责删文件**：文件该不该删取决于读回了几条，
    /// 那是调用方（状态机）的判断。上一版把删除写在这里，读与删分居两个类型时极易漏改。
    /// </para>
    /// <para>
    /// <paramref name="readFailed"/> 把"整份读不出"与"读回来了但没有可解析的行"分开：
    /// 前者不得删文件（删掉是永久丢失），后者才是该删的噪声。
    /// </para>
    /// </summary>
    public IReadOnlyList<(string Line, AuditRecord Record)> LoadPending(out int damaged, out bool readFailed)
    {
        var entries = new List<(string Line, AuditRecord Record)>();
        var broken = 0;
        readFailed = false;
        try
        {
            if (!File.Exists(PendingFilePath))
            {
                damaged = 0;
                return entries;
            }
            foreach (var line in File.ReadAllLines(PendingFilePath))
            {
                if (line.Length == 0) continue;
                if (AuditJson.TryDeserialize(line, out var record))
                {
                    entries.Add((line, record));
                }
                else
                {
                    broken++;
                }
            }
        }
        catch
        {
            // 吞掉的是"遗留待写文件读不出"；降级到"这一批历史待写记录本次会话不可恢复"，
            // 由 readFailed 告知调用方（调用方据此**保留**文件，下次启动重试）。
            // 何时应传播：不需要——启动路径不应因历史文件损坏而失败。
            readFailed = true;
        }
        damaged = broken;
        return entries;
    }
}
