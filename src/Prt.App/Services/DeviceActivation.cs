using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Prt.App.Services;

/// <summary>
/// 本副本激活记录：按激活码哈希累计「本副本激活次数」，用于在激活页与关于页展示
/// 「授权量 N 台 · 本副本已激活 x 次」。
/// <para>
/// **明确的口径**：这里只统计**本副本**（可执行文件同级）的激活次数，不做任何联网统计，
/// 也不上传任何数据——因此它不等于"全世界已用掉几台"。想真正跨机器计数必须有服务端，
/// 本程序刻意不做。
/// </para>
/// <para>
/// 计入的文件是程序同级的 <c>activations.json</c>，与授权文件 <c>license.key</c> 同目录、
/// 同一套「纯便携」存储策略（不使用 %APPDATA%，各副本互不可见）。
/// </para>
/// </summary>
internal static class DeviceActivation
{
    private const string StorageFileName = "activations.json";

    /// <summary>状态锁：只保护 <see cref="_cache"/> 引用与其中条目的增删（纯内存，绝不夹带磁盘动作）。</summary>
    private static readonly object Gate = new();

    /// <summary>
    /// I/O 闸门：独占「记录文件的读与写」。
    /// <para>
    /// 与状态锁分开的理由就是 D-28：原先 <c>Load</c> / <c>Save</c> 都在 <c>Gate</c> 内，
    /// 于是磁盘一慢，连"读一下本副本已激活几次"（<see cref="CountOf"/>）都要排队等盘。
    /// 现在冷启动后的 <see cref="CountOf"/> 只碰内存，根本不进闸门。
    /// </para>
    /// <para>锁序铁律：**先 <c>Io</c>、后 <c>Gate</c>，绝不反向**（与 AuditStore 同一约定）。</para>
    /// </summary>
    private static readonly SemaphoreSlim Io = new(1, 1);

    private static Dictionary<string, int>? _cache;

    /// <summary>记录一次激活并返回本副本累计次数（含本次）。</summary>
    public static int Record(string code)
    {
        var key = Hash(code); // 纯计算，不进闸门

        Io.Wait();
        try
        {
            var map = LoadLocked();
            int count;
            lock (Gate)
            {
                count = map.TryGetValue(key, out var existing) ? existing + 1 : 1;
                map[key] = count;
            }
            SaveLocked(map);
            return count;
        }
        finally
        {
            Io.Release();
        }
    }

    /// <summary>读取本副本累计激活次数；没有记录时返回 0。</summary>
    public static int CountOf(string code)
    {
        var key = Hash(code);

        // 快路径：缓存已就绪时只碰内存——这正是"状态查询不被磁盘拖住"要保住的性质。
        lock (Gate)
        {
            if (_cache is not null)
            {
                return _cache.TryGetValue(key, out var cached) ? cached : 0;
            }
        }

        // 冷启动：缓存还没建起来，需要读一次盘。这一条无可避免，且只发生一次。
        Io.Wait();
        try
        {
            var map = LoadLocked();
            return map.TryGetValue(key, out var count) ? count : 0;
        }
        finally
        {
            Io.Release();
        }
    }

    /// <summary>激活码哈希（只存前 16 位十六进制，避免把完整激活码抄进记录文件）。</summary>
    private static string Hash(string code)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)))[..16].ToLowerInvariant();

    /// <summary>本副本的激活计数文件路径（可执行文件同级）。</summary>
    public static string StoragePath => Path.Combine(AppContext.BaseDirectory, StorageFileName);

    /// <summary>
    /// 取记录表（**需已持 <c>Io</c>**）：缓存已就绪时直接返回，不读盘。
    /// 读盘期间**不持 <c>Gate</c>**——否则又回到"持状态锁做 I/O"。
    /// </summary>
    private static Dictionary<string, int> LoadLocked()
    {
        lock (Gate)
        {
            if (_cache is not null)
            {
                return _cache;
            }
        }

        var loaded = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            // 激活计数文件落程序目录、是本程序自己写出的运行期数据，按 PSS 4.6 不在 63 项内，
            // 故不经 SRT 申报（理由见 PortableStorage 类注释）。这条路径在**启动期**：
            // 走 SRT 会因为安全模块尚未接入而被一律拒绝。
            if (PortableStorage.TryReadText(StoragePath, out var text) && text is not null)
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, int>>(text);
                if (map is { Count: > 0 })
                {
                    loaded = new Dictionary<string, int>(map, StringComparer.Ordinal);
                }
            }
        }
        catch
        {
            // 吞掉的是"记录文件读不出 / JSON 解析失败"；降级到"视同没有记录"（计数从 0 起算），
            // 不影响激活本身的成败——授权能否生效由 Activation 依据 license.key 单独判定。
            // 何时应传播：不需要——只读的历史计数不能让激活失败。
        }

        // Io 是独占的，到这里不可能有第二个线程进来写 _cache，故无需比较交换。
        lock (Gate)
        {
            return _cache ??= loaded;
        }
    }

    /// <summary>落盘（**需已持 <c>Io</c>**）。</summary>
    private static void SaveLocked(Dictionary<string, int> map)
    {
        string json;
        lock (Gate)
        {
            json = JsonSerializer.Serialize(map, JsonOptions);
        }

        try
        {
            // 同 Load：程序目录内的运行期数据，不经 SRT 申报。
            PortableStorage.TryWriteText(StorageFileName, json, "写入激活计数文件", out _);
        }
        catch
        {
            // 吞掉的是"计数文件写不进"（只读介质 / 权限不足 / 磁盘满）；降级到"本次计入内存、
            // 下次启动丢失"——它只是一个展示用的计数，不参与任何裁决。
            // 何时应传播：不需要——授权落盘失败会由 Activation.Activate 单独回报（那条路径必须可见）。
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };
}
