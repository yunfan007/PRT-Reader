using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Prt.Tools.LicenseGen;

/// <summary>签发结果。</summary>
internal sealed record GenerateResult(string Code, string Summary);

/// <summary>校验结果。</summary>
internal sealed record VerifyResult(bool Ok, string Detail);

/// <summary>密钥生成结果。</summary>
internal sealed record KeygenResult(string PrivatePath, string PublicPath);

/// <summary>
/// 激活码核心逻辑（图形界面与命令行共用）。
/// <para>
/// 与编辑器共用同一套格式：「PRTL1.«payload».«signature»」，其中 payload 为
/// 「PRTA1|用户名|邮箱|等级|到期日（yyyy-MM-dd 或 PERPETUAL）」的 UTF-8 字节，
/// signature 为其 SHA256 的 RSA-2048 签名。签名用私钥，验证只需公钥——
/// 编辑器内嵌公钥，全程离线。
/// </para>
/// </summary>
internal static class LicenseCore
{
    public const string CodePrefix = "PRTL1.";
    public const string PayloadMarker = "PRTA1";

    /// <summary>改名前的旧前缀：生成器始终签发新前缀，此常量仅用于界面提示。</summary>
    public const string LegacyCodePrefix = "MDPL1.";

    /// <summary>随工具分发的默认公钥（与编辑器内嵌公钥一致，用于校验的缺省口径）。</summary>
    public const string DefaultPublicKeyXml =
        "<RSAKeyValue><Modulus>r5ajwAotQvfJdBg7HYXel9/ah7WI9TLdrwUBpPfec/Wkq/8Ro1oqYaU7u3HYM0nRSQiN3ebvwR3lPp3gIKGZf5j8CRSyixiKxIgViB9HoXIaXATWeBshfluqnFIFJlLAvnmew2HiruKCqFA24Aj+PhXhFSzYfG6fKRfmjinZwBNe1L5NbNNELFBc51ZQLWV2UWsv03RkPSzLwR9YTyVbwtjJFP+ZX552NbhwtiHoBZugfs5wAShk4da1gd7GL3eJJG3wooKzs0SkxboNM4ruk7MOOl6zXwj4SFXijqTgv+mlEsVqOwdoXgXvK0SUkhPOD1lWgC+14EcgjvMkF+en0Q==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

    public static readonly string[] ValidLevels = { "Free", "Standard", "Professional" };

    // ─────────────────────────────── keygen ───────────────────────────────

    public static KeygenResult Keygen(string directory, bool force)
    {
        Directory.CreateDirectory(directory);

        var privatePath = Path.Combine(directory, "license.private.xml");
        var publicPath = Path.Combine(directory, "license.public.xml");

        if (File.Exists(privatePath) && !force)
        {
            throw new InvalidOperationException(privatePath + " 已存在。如需覆盖请先确认（覆盖后旧激活码全部失效）。");
        }

        using var rsa = RSA.Create(2048);
        File.WriteAllText(privatePath, rsa.ToXmlString(true), new UTF8Encoding(false));
        File.WriteAllText(publicPath, rsa.ToXmlString(false), new UTF8Encoding(false));

        return new KeygenResult(privatePath, publicPath);
    }

    // ─────────────────────────────── gen ───────────────────────────────

    public static GenerateResult Generate(
        string name,
        string email,
        string level,
        int? days,
        bool perpetual,
        string? keyPath,
        string issuedBy,
        int seats,
        string? reason)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(level))
        {
            throw new InvalidOperationException("用户名、邮箱、等级三项都必须填写。");
        }

        if (string.IsNullOrWhiteSpace(issuedBy))
        {
            throw new InvalidOperationException("签发人必须填写（用于追溯是谁签的这张码）。");
        }

        if (seats < 1)
        {
            throw new InvalidOperationException("授权量至少为 1 台设备。");
        }

        if (!ValidLevels.Contains(level, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("等级必须是 " + string.Join(" / ", ValidLevels) + " 之一。");
        }

        if (!email.Contains('@', StringComparison.Ordinal) || !email.Contains('.', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("邮箱格式不合法。");
        }

        var resolvedDays = perpetual ? null : days;
        var expiry = resolvedDays is null
            ? "PERPETUAL"
            : ToolClock.Now().LocalDateTime.Date.AddDays(resolvedDays.Value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var issuedAt = ToolClock.Now().ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        // 字段顺序（9 段，竖线分隔）：
        //   标记|用户名|邮箱|等级|到期日|签发时间|授权量|签发人|签发理由
        // 末尾的签发理由允许为空（Split 会保留末尾空字段），其余字段必填。
        var privateKeyXml = ReadPrivateKey(keyPath);
        var payload = PayloadMarker
                      + "|" + name.Trim()
                      + "|" + email.Trim()
                      + "|" + level
                      + "|" + expiry
                      + "|" + issuedAt
                      + "|" + seats.ToString(CultureInfo.InvariantCulture)
                      + "|" + issuedBy.Trim()
                      + "|" + (reason?.Trim() ?? string.Empty);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        using var rsa = RSA.Create();
        rsa.FromXmlString(privateKeyXml);
        var signature = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var code = CodePrefix + Base64Url(payloadBytes) + "." + Base64Url(signature);
        var summary = "用户名 " + name.Trim() + " · 邮箱 " + email.Trim() + " · 等级 " + level
                      + " · " + (resolvedDays is null ? "永久有效" : "自今日起 " + resolvedDays + " 天（至 " + expiry + "）")
                      + "\r\n　　　签发时间 " + issuedAt + " · 授权量 " + seats + " 台设备 · 签发人 " + issuedBy.Trim()
                      + (string.IsNullOrWhiteSpace(reason) ? string.Empty : " · 理由 " + reason.Trim())
                      + "\r\n　　　适用：PRT 阅读器（旧版 PSR 工具请改用前缀 PSRL1.，更早的 MDP 工具请用 " + LegacyCodePrefix + "）";
        return new GenerateResult(code, summary);
    }

    // ─────────────────────────────── verify ───────────────────────────────

    public static VerifyResult Verify(string code, string? publicPath)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("请先粘贴要校验的激活码。");
        }

        var publicKeyXml = DefaultPublicKeyXml;
        if (publicPath is not null)
        {
            publicKeyXml = File.ReadAllText(publicPath, new UTF8Encoding(false));
        }

        if (!code.StartsWith(CodePrefix, StringComparison.Ordinal))
        {
            return new VerifyResult(false, "激活码应以 " + CodePrefix + " 开头。");
        }

        var parts = code[CodePrefix.Length..].Split('.');
        if (parts.Length != 2)
        {
            return new VerifyResult(false, "激活码格式不完整。");
        }

        byte[] payload;
        byte[] signature;
        try
        {
            payload = Base64UrlDecode(parts[0]);
            signature = Base64UrlDecode(parts[1]);
        }
        catch (FormatException)
        {
            // 与编辑器侧同口径：抄录不完整 / 混入无关字符时给出中文说明，不把 Base64 异常原文亮给用户。
            return new VerifyResult(false, "激活码不是有效的编码文本，可能已被改动或抄录不完整。");
        }

        using var rsa = RSA.Create();
        rsa.FromXmlString(publicKeyXml);
        if (!rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        {
            return new VerifyResult(false, "签名不匹配：激活码被改动或由其它密钥签发。");
        }

        var fields = Encoding.UTF8.GetString(payload).Split('|');

        // 5 段＝改名前的旧码；9 段＝加入签发时间 / 授权量 / 签发人 / 签发理由后的新码。
        if (fields.Length is not (5 or 9))
        {
            return new VerifyResult(false, "授权内容字段数应为 5（旧版）或 9（新版），实际 " + fields.Length + "。");
        }

        var detail = "签名有效。授权明细：\r\n"
                     + "  用户名   " + (fields.Length > 1 ? fields[1] : "?") + "\r\n"
                     + "  邮箱     " + (fields.Length > 2 ? fields[2] : "?") + "\r\n"
                     + "  等级     " + (fields.Length > 3 ? fields[3] : "?") + "\r\n"
                     + "  到期     " + (fields.Length > 4 ? fields[4] : "?");

        if (fields.Length == 9)
        {
            detail += "\r\n"
                      + "  签发时间 " + (string.IsNullOrWhiteSpace(fields[5]) ? "（未填）" : fields[5]) + "\r\n"
                      + "  授权量   " + (string.IsNullOrWhiteSpace(fields[6]) ? "（未填）" : fields[6] + " 台设备") + "\r\n"
                      + "  签发人   " + (string.IsNullOrWhiteSpace(fields[7]) ? "（未填）" : fields[7]) + "\r\n"
                      + "  签发理由 " + (string.IsNullOrWhiteSpace(fields[8]) ? "（未填）" : fields[8]);
        }
        else
        {
            detail += "\r\n  （旧版激活码：不含签发时间 / 授权量 / 签发人 / 签发理由）";
        }

        return new VerifyResult(true, detail);
    }

    // ─────────────────────────────── 私钥定位 ───────────────────────────────

    /// <summary>
    /// 私钥查找顺序：显式路径 → 当前目录 → 程序目录 → 向上回溯源码树的 keys 目录。
    /// </summary>
    public static string ReadPrivateKey(string? explicitPath)
    {
        var candidates = new List<string>();
        if (explicitPath is not null)
        {
            candidates.Add(explicitPath);
        }

        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "license.private.xml"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "license.private.xml"));

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && directory is not null; i++)
        {
            candidates.Add(Path.Combine(directory.FullName, "keys", "license.private.xml"));
            directory = directory.Parent;
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate, new UTF8Encoding(false));
            }
        }

        throw new InvalidOperationException(
            "未找到私钥文件 license.private.xml。请先生成密钥对，或在私钥框中指定路径。");
    }

    // ─────────────────────────────── 辅助 ───────────────────────────────

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        // 补位按「原长度 %4」判断：2 → 补两个 =，3 → 补一个 =。
        // （旧实现用 (4 - 长度%4) %4 当余数，导致长度 %4==3 的段一个 = 都不补、
        //   FromBase64String 必抛——凡载荷字节数 %3==2 的激活码全部无法验证。）
        var remainder = padded.Length % 4;
        padded += remainder == 2 ? "==" : remainder == 3 ? "=" : string.Empty;
        return Convert.FromBase64String(padded);
    }
}
