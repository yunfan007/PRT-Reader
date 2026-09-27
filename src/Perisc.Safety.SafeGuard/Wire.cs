using System.Text;
using System.Text.Json;

namespace Perisc.Safety.SafeGuard;

/// <summary>
/// 线上错误码全集（PSS 标准 4.8，10 值）：<c>error.code</c> **必须**与 PssCode 同名，
/// 不得自造（5.2 第 ⑨ 条，否则 7.5 的 P.7 无法通过）。
/// 守护服务是独立进程、不引用 C# 绑定库，故在此以常量显式写出线上拼写。
/// </summary>
public static class GuardCode
{
    public const string Ok = "Ok";
    public const string NoModule = "NoModule";
    public const string Denied = "Denied";
    public const string Timeout = "Timeout";
    public const string BadArg = "BadArg";
    public const string AuditUnavailable = "AuditUnavailable";
    public const string GuardUnavailable = "GuardUnavailable";
    public const string Limit = "Limit";
    public const string ApiMismatch = "ApiMismatch";
    public const string Internal = "Internal";
}

/// <summary>
/// 线协议帧与信封（PSS 标准 5.2，规范性）：
/// - 帧格式：4 字节小端长度 + UTF-8 JSON，单帧上限 1 MiB（5.2 第 ② 条）；
/// - 请求：<c>{"protocolVersion":"1.0","id":"m-1","method":"…","params":{…}}</c>（第 ③ 条）；
/// - 应答：<c>{"protocolVersion":"1.0","id":"m-1","result":{…}}</c> 或
///   <c>{"protocolVersion":"1.0","id":"m-1","error":{"code":"…","detail":"…"}}</c>（第 ④ 条）。
/// 错误码**必须**与 4.8 的 PssCode 同名，不得自造（第 ⑨ 条）。
/// </summary>
public static class Wire
{
    /// <summary>单帧长度硬上限（5.2 第 ② 条）；12.2 的 64 KiB 为常见规模。</summary>
    public const int MaxFrameBytes = 1024 * 1024;

    /// <summary>服务端遵循的线协议版本（5.2 第 ⑧ 条 / 7.6）。</summary>
    public const string ServerProtocolVersion = "1.0";

    /// <summary>默认终止通知窗口（2.4）：0 ~ 2000 ms。</summary>
    public const int NoticeWindowMs = 200;

    /// <summary>从流读取一帧；流关闭或帧损坏返回 null。</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var head = new byte[4];
        if (!await ReadExactAsync(stream, head, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        var length = head[0] | (head[1] << 8) | (head[2] << 16) | (head[3] << 24);
        if (length <= 0 || length > MaxFrameBytes)
        {
            return null;
        }
        var payload = new byte[length];
        if (!await ReadExactAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return payload;
    }

    /// <summary>向流写出一帧。</summary>
    public static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        var head = new byte[4];
        head[0] = (byte)payload.Length;
        head[1] = (byte)(payload.Length >> 8);
        head[2] = (byte)(payload.Length >> 16);
        head[3] = (byte)(payload.Length >> 24);
        await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static byte[] Serialize(object envelope) => JsonSerializer.SerializeToUtf8Bytes(envelope);

    /// <summary>成功应答信封（5.2 第 ④ 条）。</summary>
    public static object Ack(string id, object result) => new Dictionary<string, object>
    {
        ["protocolVersion"] = ServerProtocolVersion,
        ["id"] = id,
        ["result"] = result,
    };

    /// <summary>错误应答信封（5.2 第 ④ ⑨ 条）：<c>code</c> 取 4.8 的错误码名称。</summary>
    public static object Err(string id, string code, string detail) => new Dictionary<string, object>
    {
        ["protocolVersion"] = ServerProtocolVersion,
        ["id"] = id,
        ["error"] = new Dictionary<string, object>
        {
            ["code"] = code,
            ["detail"] = detail,
        },
    };

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                return false;
            }
            read += n;
        }
        return true;
    }

    /// <summary>解码请求信封；非法帧返回 false（5.2 第 ② ③ 条）。</summary>
    public static bool TryParseRequest(byte[] payload, out string id, out string method,
                                       out JsonElement parameters, out string protocolVersion)
    {
        id = string.Empty;
        method = string.Empty;
        protocolVersion = string.Empty;
        parameters = default;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("method", out var methodProp) || methodProp.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            id = idProp.GetString()!;
            method = methodProp.GetString()!;
            protocolVersion = Str(root, "protocolVersion");
            parameters = root.TryGetProperty("params", out var p) ? p.Clone() : default;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读取字符串参数。</summary>
    public static string Str(JsonElement element, string name, string fallback = "") =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : fallback;

    /// <summary>读取整型参数；缺失或类型不符返回 null。</summary>
    public static int? Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : null;

    /// <summary>
    /// 协议主版本一致性（5.2 第 ⑧ 条）：主版本不一致 → 服务端返回 ApiMismatch 并关闭连接；
    /// 次版本不一致 → 以较低者为准，忽略较新的可选字段（不得失败）。
    /// </summary>
    public static bool ProtocolMajorMatches(string? clientVersion)
    {
        if (string.IsNullOrWhiteSpace(clientVersion))
        {
            return false;
        }
        return clientVersion.Split('.')[0].Trim() == ServerProtocolVersion.Split('.')[0];
    }

    /// <summary>matchMode 的线上拼写（5.2 第 ⑥ 条，大小写敏感）。</summary>
    public static bool IsMatchMode(string text) =>
        text is "exact" or "pathPrefix" or "hostSuffix" or "hostPortRange";
}
