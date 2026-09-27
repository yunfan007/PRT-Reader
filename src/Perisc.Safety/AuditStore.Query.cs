using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Perisc.Safety;

// 查询 / 导出 / 清空（AuditStore 的 partial；拆分依据见《工程改进方案》§4.8 / D-06）。
internal sealed partial class AuditStore : IDisposable
{
    /// <summary>审计类别码全集（4.4）。提成字段：IsValidCategory 每次查询都会调用，避免重复分配数组。</summary>
    private static readonly string[] AuditCategories =
        { "FS", "NET", "PROC", "CFG", "DEV", "CRED", "CODE", "PRIV", "RES" };

    // ───────────────────────────── 查询 / 导出 / 清空 ─────────────────────────

    /// <summary>按条件查询（4.4）：时间升序；非法 Category / From &gt; To 返回空结果集（6.1.1）。</summary>
    public IReadOnlyList<AuditRecord> Query(AuditQuery query)
    {
        if (!IsValidCategory(query.Category) || (query.From is not null && query.To is not null && query.From > query.To))
        {
            return Array.Empty<AuditRecord>();
        }

        var result = new List<AuditRecord>();
        _io.Wait();
        try
        {
            foreach (var file in _shards.ShardFiles())
            {
                var shard = AuditShardWriter.ReadShard(file, out var damaged);
                AddDamaged(damaged);
                foreach (var record in shard)
                {
                    if (query.From is not null && record.Time < query.From) continue;
                    if (query.To is not null && record.Time > query.To) continue;
                    if (query.Category is not null && BehaviorCatalog.Find(record.Action)?.Category != query.Category) continue;
                    if (query.Action is not null && record.Action != query.Action) continue;
                    if (query.Decision is not null && record.Decision != query.Decision) continue;
                    if (query.DegradedOnly is true && !record.AuditDegraded) continue;
                    result.Add(record);
                }
            }
        }
        finally
        {
            _io.Release();
        }

        result.Sort((a, b) => a.Time.CompareTo(b.Time));
        ReportDamageOnce();
        return result;
    }

    /// <summary>
    /// 导出 JSON Lines（4.4 / 7.4）：只读取、不删除。
    /// D-18：唯一的外部分析型异常（磁盘）在此被转成返回值，不抛给调用方（4.10 第 1 条）。
    /// </summary>
    public PssResult Export(AuditExportOptions options)
    {
        if (!options.JsonLines)
        {
            return new PssResult(PssCode.BadArg, "目前仅支持 JSON Lines 导出");
        }

        try
        {
            var records = Query(new AuditQuery(options.From, options.To));
            var dir = Path.GetDirectoryName(Path.GetFullPath(options.Path));
            if (!string.IsNullOrEmpty(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }
            var sb = new StringBuilder();
            foreach (var record in records)
            {
                sb.AppendLine(AuditJson.Serialize(record));
            }

            _io.Wait();
            try
            {
                File.WriteAllText(options.Path, sb.ToString(), new UTF8Encoding(false));
            }
            finally
            {
                _io.Release();
            }
            return new PssResult(PssCode.Ok, $"已导出 {records.Count} 条");
        }
        catch (Exception ex)
        {
            return new PssResult(PssCode.Internal, "导出失败：" + ex.Message);
        }
    }

    /// <summary>删除审计记录（用户数据必须可删除，4.4）；删除后写 audit-purge 元记录。</summary>
    public PssResult Purge(AuditQuery query)
    {
        var victims = Query(query);
        var min = DateTimeOffset.MaxValue;
        var max = DateTimeOffset.MinValue;
        var stamps = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in victims)
        {
            stamps.Add(AuditShardWriter.ShardStamp(record.Time));
            if (record.Time < min) min = record.Time;
            if (record.Time > max) max = record.Time;
        }

        try
        {
            _io.Wait();
            try
            {
                foreach (var stamp in stamps)
                {
                    // 按日期取**全部分片**（含滚动片）：只认基础片会让滚动出去的那部分记录
                    // 永远删不掉——用户点了"删除"，界面说删了，文件里还在。
                    foreach (var file in _shards.ShardFilesForStamp(stamp))
                    {
                        var keep = new StringBuilder();
                        var shard = AuditShardWriter.ReadShard(file, out var damaged);
                        AddDamaged(damaged);
                        foreach (var record in shard)
                        {
                            var inRange = query.From is null || record.Time >= query.From;
                            if (query.To is not null && record.Time > query.To) inRange = false;
                            if (query.Category is not null && BehaviorCatalog.Find(record.Action)?.Category != query.Category) inRange = false;
                            if (query.Action is not null && record.Action != query.Action) inRange = false;
                            if (query.Decision is not null && record.Decision != query.Decision) inRange = false;
                            if (query.DegradedOnly is true && !record.AuditDegraded) inRange = false;
                            if (!inRange)
                            {
                                keep.AppendLine(AuditJson.Serialize(record));
                            }
                        }
                        File.WriteAllText(file, keep.ToString(), new UTF8Encoding(false));
                        _shards.RebuildChecksum(stamp);
                    }
                }
            }
            finally
            {
                _io.Release();
            }

            var range = victims.Count == 0 ? "空" : $"{min:yyyy-MM-dd HH:mm:ss} ~ {max:yyyy-MM-dd HH:mm:ss}";
            AppendInternal(new AuditRecord(
                _programId(), action: string.Empty, target: string.Empty,
                AuditDecision.Allow, PssCode.Ok, AuditLayer.Arm,
                detail: $"audit-purge：删除 {victims.Count} 条，时间范围 {range}"));
            return new PssResult(PssCode.Ok, $"已删除 {victims.Count} 条（不可恢复）");
        }
        catch (Exception ex)
        {
            return new PssResult(PssCode.Internal, "删除失败：" + ex.Message);
        }
    }

    private static bool IsValidCategory(string? category) =>
        category is null || Array.IndexOf(AuditCategories, category) >= 0;
}
