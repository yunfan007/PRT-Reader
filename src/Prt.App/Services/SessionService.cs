using System.Text.Json;

namespace Prt.App.Services;

/// <summary>
/// 会话记忆：把「上次退出时打开的文档与活动标签」落盘，供下次启动恢复。
/// <para>
/// 未保存的新建文档不进入会话记录；文件在下次启动时已不存在者自动跳过——
/// 此时若无任何可恢复的文档，主窗口直接开一个完全空白的标签。
/// </para>
/// <para>
/// 位置策略＝<b>纯便携</b>：只读写程序所在文件夹下的 <c>session.json</c>（见 <see cref="PortableStorage"/>），
/// 不再回退到 <c>%APPDATA%</c>；旧位置存量由 <see cref="LegacyConfigMigration"/> 首启迁移。
/// </para>
/// </summary>
public static class SessionService
{
    private const string StorageFileName = "session.json";

    private sealed record SessionData(string[] Paths, int ActiveIndex);

    /// <summary>会话记录的落点（程序所在文件夹内）。自检据此断言「纯便携」。</summary>
    public static string StoragePath => PortableStorage.FilePath(StorageFileName);

    /// <summary>
    /// 保存会话到程序目录。失败时给出可读原因（程序目录不可写…）。
    /// 会话不是关键数据，调用方不必打断用户，但也不该假装成功。
    /// </summary>
    public static bool TrySave(IReadOnlyList<string> orderedPaths, int activeIndex, out string error)
    {
        var data = new SessionData([.. orderedPaths], activeIndex);
        var json = JsonSerializer.Serialize(data, JsonOptions);
        return PortableStorage.TryWriteText(StorageFileName, json, "保存会话记录", out error);
    }

    /// <summary>尝试读取会话；文件不存在或内容损坏时返回 false。</summary>
    public static bool TryLoad(out IReadOnlyList<string> orderedPaths, out int activeIndex)
    {
        orderedPaths = [];
        activeIndex = 0;

        if (!PortableStorage.TryReadText(StoragePath, out var json) || string.IsNullOrEmpty(json))
        {
            return false;
        }

        try
        {
            var data = JsonSerializer.Deserialize<SessionData>(json, JsonOptions);
            if (data is null || data.Paths.Length == 0)
            {
                return false;
            }

            var ordered = data.Paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
            orderedPaths = ordered;
            activeIndex = Math.Clamp(data.ActiveIndex, 0, Math.Max(0, ordered.Length - 1));
            return ordered.Length > 0;
        }
        catch (Exception)
        {
            // 损坏的会话文件视同无会话——调用方随即开一个空白标签，不需要区分「损坏」与「不存在」。
        }

        return false;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };
}
