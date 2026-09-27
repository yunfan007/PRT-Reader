using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Prt.App.Services;

/// <summary>授权等级。等级只做向上兼容：Professional 拥有全部能力。</summary>
public enum LicenseLevel
{
    Free = 0,
    Standard = 1,
    Professional = 2,
}

/// <summary>一份已通过签名验证的授权信息。</summary>
public sealed class LicenseInfo
{
    public required string Username { get; init; }

    public required string Email { get; init; }

    public required string LevelName { get; init; }

    public required LicenseLevel Level { get; init; }

    /// <summary>到期日（本地时区日期）；null 表示永久授权。</summary>
    public DateTime? ExpiresOn { get; init; }

    /// <summary>签发时间（ISO 文本，形如 <c>2026-09-18T21:30:00</c>）；旧版激活码为 null。</summary>
    public string? IssuedAt { get; init; }

    /// <summary>授权量（可激活设备数）；旧版激活码为 null（不作限制）。</summary>
    public int? Seats { get; init; }

    /// <summary>签发人；旧版激活码为 null。</summary>
    public string? IssuedBy { get; init; }

    /// <summary>签发理由；可为空。</summary>
    public string? Reason { get; init; }

    /// <summary>原始激活码（回存用）。</summary>
    public required string Code { get; init; }

    /// <summary>是否为「带完整签发信息」的新版激活码。</summary>
    public bool HasIssuerInfo => Seats is not null;

    public bool IsPerpetual => ExpiresOn is null;

    public bool IsExpired => !IsPerpetual && ExpiresOn!.Value.Date < AppClock.Today;

    public string ExpiryText => IsPerpetual
        ? Localizer.T("license.expiry.perpetual")
        : Localizer.T("license.expiry.date", ExpiresOn!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    /// <summary>
    /// 界面展示用的授权名：非永久（限时）激活码统一显示为 <b>VIP</b>（附到期日）；
    /// 永久激活码沿用签发等级名（Standard / Professional）。
    /// </summary>
    public string DisplayLevelName => IsPerpetual ? LevelName : "VIP";
}

/// <summary>
/// 离线激活码：格式为「PRTL1.«payload»».«signature»」。
/// <para>
/// payload 是以竖线分隔的字段串（PRTA1|用户名|邮箱|等级|到期日）的 UTF-8 字节，
/// signature 是对其 SHA256 的 RSA-2048 签名。验证只依赖内嵌公钥，全程离线；
/// 私钥仅保存在激活码生成器（Prt.Tools.LicenseGen）一侧。
/// </para>
/// </summary>
public static class Activation
{
    public const string CodePrefix = "PRTL1.";

    /// <summary>载荷首字段标记（当前版本）。</summary>
    private const string PayloadMarker = "PRTA1";

    /// <summary>改名前的旧前缀（PSR 时代 / MDP 时代各一）：仍接受，改名之前签发的码继续可用。</summary>
    private static readonly string[] LegacyCodePrefixes = ["PSRL1.", "MDPL1."];

    /// <summary>改名前的旧载荷标记（同上）。</summary>
    private static readonly string[] LegacyPayloadMarkers = ["PSRA1", "MDPA1"];

    /// <summary>可接受的激活码前缀（新在前、旧在后）。</summary>
    private static readonly string[] AcceptedPrefixes = [CodePrefix, .. LegacyCodePrefixes];

    /// <summary>可接受的载荷标记（新在前、旧在后）。</summary>
    private static readonly string[] AcceptedMarkers = [PayloadMarker, .. LegacyPayloadMarkers];

    private const string StorageFileName = "license.key";

    /// <summary>
    /// 授权验证公钥（RSA-2048，XML 格式）。私钥绝不进入本程序：
    /// 激活码由 Prt.Tools.LicenseGen 用私钥离线签发。
    /// </summary>
    private const string PublicKeyXml =
        "<RSAKeyValue><Modulus>r5ajwAotQvfJdBg7HYXel9/ah7WI9TLdrwUBpPfec/Wkq/8Ro1oqYaU7u3HYM0nRSQiN3ebvwR3lPp3gIKGZf5j8CRSyixiKxIgViB9HoXIaXATWeBshfluqnFIFJlLAvnmew2HiruKCqFA24Aj+PhXhFSzYfG6fKRfmjinZwBNe1L5NbNNELFBc51ZQLWV2UWsv03RkPSzLwR9YTyVbwtjJFP+ZX552NbhwtiHoBZugfs5wAShk4da1gd7GL3eJJG3wooKzs0SkxboNM4ruk7MOOl6zXwj4SFXijqTgv+mlEsVqOwdoXgXvK0SUkhPOD1lWgC+14EcgjvMkF+en0Q==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

    private static LicenseInfo? _current;

    /// <summary>当前授权；null 表示未激活（按免费版运行）。</summary>
    public static LicenseInfo? Current => _current;

    /// <summary>当前授权等级；未激活或已过期按免费版计。</summary>
    public static LicenseLevel CurrentLevel
    {
        get
        {
            if (_current is { } license && !license.IsExpired)
            {
                return license.Level;
            }

            return LicenseLevel.Free;
        }
    }

    /// <summary>授权发生变化（激活 / 启动加载）后触发。</summary>
    public static event EventHandler? LicenseChanged;

    /// <summary>启动时从磁盘恢复授权状态。</summary>
    public static void LoadStoredLicense()
    {
        var code = ReadStoredCode();
        if (code is null)
        {
            return;
        }

        if (TryVerify(code, out var license, out _))
        {
            _current = license;
            LicenseChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 校验激活码（签名 + 字段 + 有效期）。任何一步失败都会给出可直接展示的错误说明。
    /// </summary>
    public static bool TryVerify(string code, out LicenseInfo license, out string error)
    {
        license = null!;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(code))
        {
            error = "请输入激活码。";
            return false;
        }

        code = code.Trim();

        // 兼容改名前的旧前缀（MDPL1.）：存量激活码无需重新签发即可继续使用。
        var prefix = Array.Find(AcceptedPrefixes, p => code.StartsWith(p, StringComparison.Ordinal));
        if (prefix is null)
        {
            error = "激活码格式不正确：应以 " + CodePrefix + " 开头。";
            return false;
        }

        var parts = code[prefix.Length..].Split('.');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            error = "激活码格式不正确：缺少授权内容或签名。";
            return false;
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
            error = "激活码不是有效的编码文本，可能已被改动或抄录不完整。";
            return false;
        }

        using var rsa = RSA.Create();
        rsa.FromXmlString(PublicKeyXml);
        if (!rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        {
            error = "激活码签名验证失败：内容与签名不匹配。";
            return false;
        }

        return TryParsePayload(Encoding.UTF8.GetString(payload), code, out license, out error);
    }

    /// <summary>
    /// 解析授权载荷（在签名校验通过之后调用）。
    /// <para>
    /// 支持两种字段布局（竖线分隔）：
    /// **5 段**（旧码）＝标记|用户名|邮箱|等级|到期日；
    /// **9 段**（新码）＝标记|用户名|邮箱|等级|到期日|签发时间|授权量|签发人|签发理由。
    /// 末尾的签发理由允许为空。
    /// </para>
    /// <para>
    /// 本方法单列出来，是为了让启动自检能直接验证两种布局的解析结果，而不必持有签发私钥。
    /// </para>
    /// </summary>
    internal static bool TryParsePayload(string payloadText, string code, out LicenseInfo license, out string error)
    {
        license = null!;
        error = string.Empty;

        var fields = payloadText.Split('|');
        if (fields.Length is not (5 or 9) || Array.IndexOf(AcceptedMarkers, fields[0].Trim()) < 0)
        {
            error = "激活码内容不符合本程序的授权格式。";
            return false;
        }

        var extended = fields.Length == 9;

        var username = fields[1].Trim();
        var email = fields[2].Trim();
        var levelName = fields[3].Trim();
        var expiryText = fields[4].Trim();

        if (username.Length == 0)
        {
            error = "激活码中缺少用户名。";
            return false;
        }

        if (!email.Contains('@', StringComparison.Ordinal) || !email.Contains('.', StringComparison.Ordinal))
        {
            error = "激活码中的邮箱地址不合法。";
            return false;
        }

        if (!Enum.TryParse(levelName, ignoreCase: true, out LicenseLevel level))
        {
            error = "激活码中的授权等级无法识别：" + levelName;
            return false;
        }

        DateTime? expiresOn;
        if (string.Equals(expiryText, "PERPETUAL", StringComparison.OrdinalIgnoreCase))
        {
            expiresOn = null;
        }
        else if (DateTime.TryParseExact(
                     expiryText,
                     "yyyy-MM-dd",
                     CultureInfo.InvariantCulture,
                     DateTimeStyles.None,
                     out var parsed))
        {
            expiresOn = parsed;
        }
        else
        {
            error = "激活码中的有效期格式不合法。";
            return false;
        }

        if (expiresOn is { } date && date.Date < AppClock.Today)
        {
            error = $"激活码已于 {date:yyyy-MM-dd} 过期。";
            return false;
        }

        int? seats = null;
        if (extended)
        {
            var seatsText = fields[6].Trim();
            if (seatsText.Length > 0)
            {
                if (!int.TryParse(seatsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSeats) || parsedSeats < 1)
                {
                    error = "激活码中的授权量不合法：" + seatsText;
                    return false;
                }

                seats = parsedSeats;
            }
        }

        license = new LicenseInfo
        {
            Username = username,
            Email = email,
            LevelName = levelName,
            Level = level,
            ExpiresOn = expiresOn,
            IssuedAt = extended ? fields[5].Trim() : null,
            Seats = seats,
            IssuedBy = extended ? fields[7].Trim() : null,
            Reason = extended ? fields[8].Trim() : null,
            Code = code,
        };
        return true;
    }

    /// <summary>
    /// 激活（校验通过后调用）：缓存、记录本副本激活次数并落盘。
    /// <para>
    /// 落盘失败（程序目录只读）时**本次运行仍然生效**，但返回 <c>false</c> 并给出原因，
    /// 调用方必须把该原因展示出来——否则用户会以为激活已保存、重启后却发现恢复成未激活。
    /// </para>
    /// </summary>
    public static bool Activate(LicenseInfo license, out string error)
    {
        _current = license;

        // 本副本激活次数：用于展示「本副本已激活 x 次」。只记本地、不联网。
        // 授权文件写不进去时连同计数一并跳过，避免出现「计数有、授权无」的不一致。
        var saved = TryWriteStoredCode(license.Code, out error);
        if (saved)
        {
            DeviceActivation.Record(license.Code);
        }

        LicenseChanged?.Invoke(null, EventArgs.Empty);
        return saved;
    }

    /// <summary>等级的中文名称与说明。</summary>
    public static string DescribeLevel(LicenseLevel level) => level switch
    {
        LicenseLevel.Standard => Localizer.T("license.level.standard"),
        LicenseLevel.Professional => Localizer.T("license.level.professional"),
        _ => Localizer.T("license.level.free"),
    };

    /// <summary>当前授权的一句话摘要（用于「关于」与授权对话框）。限时激活码显示为 VIP。</summary>
    public static string CurrentSummary()
    {
        if (Current is not { } info)
        {
            return Localizer.T("activation.badge.none") + " · " + DescribeLevel(LicenseLevel.Free);
        }

        var parts = new List<string>
        {
            $"{info.Username} <{info.Email}>",
            info.DisplayLevelName,
            info.ExpiryText,
        };

        if (info.Seats is { } seats)
        {
            parts.Add(Localizer.T("activation.quota.value", seats));
            parts.Add(Localizer.T("activation.used", DeviceActivation.CountOf(info.Code)));
        }

        if (!string.IsNullOrWhiteSpace(info.IssuedBy))
        {
            parts.Add(Localizer.T("license.summary.issuer", info.IssuedBy));
        }

        return string.Join(" · ", parts);
    }

    // ─────────────────────────────── 持久化 ───────────────────────────────

    /// <summary>
    /// 授权文件的唯一位置：**可执行文件同级**的 <c>license.key</c>。
    /// <para>
    /// 存储策略＝纯便携（green / portable），刻意**不**使用 <c>%APPDATA%</c>：
    /// 本机若存在多份程序副本，各自只认自己目录下的授权文件，一份激活不会点亮其余副本
    /// （历史版本会把授权回退写到 <c>%APPDATA%\PRT 阅读器</c>，导致同机所有副本互相串扰）。
    /// </para>
    /// <para>
    /// 代价是程序目录必须可写；只读目录（如 <c>Program Files</c>）下的激活无法留存，
    /// <see cref="Activate"/> 会明确回报该失败原因，而不是静默丢弃。
    /// </para>
    /// </summary>
    public static string StoragePath => Path.Combine(AppContext.BaseDirectory, StorageFileName);

    /// <summary>读取程序同级已存储的激活码；不存在或不可读时返回 null（按未激活处理）。</summary>
    private static string? ReadStoredCode()
    {
        // 授权文件落在程序目录、是本程序自己写出的运行期数据，按 PSS 4.6 不在 63 项内，
        // 因而不经 SRT 申报（理由与边界见 PortableStorage 类注释）。
        // 这条路径在**启动期**（App.OnStartup → Activation.LoadStoredLicense）：若改走 SRT，
        // 安全模块尚未接入时会被一律拒绝，授权就永远读不出来。
        // 读不到视同未激活——授权本就在本地校验，不因读取失败而中断启动。
        if (PortableStorage.TryReadText(StoragePath, out var code) && code is { Length: > 0 })
        {
            return code.Trim();
        }

        return null;
    }

    /// <summary>把激活码写入程序同级；失败时给出可直接展示的原因。</summary>
    private static bool TryWriteStoredCode(string code, out string error)
    {
        // 同 ReadStoredCode：程序目录内的运行期数据，不经 SRT 申报。
        // 于是失败只剩磁盘与权限一种来源（目录只读、被占用、磁盘满），原因可直接给用户看。
        if (PortableStorage.TryWriteText(StorageFileName, code, "写入授权文件", out error))
        {
            return true;
        }

        error = "无法写入授权文件 " + StoragePath + "：" + error
            + "。请把程序放到可写目录（或改用绿色免安装目录）后重试。";
        return false;
    }

    // ─────────────────────────────── 编码 ───────────────────────────────

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        // 补位按「原长度 %4」判断：2 → 补两个 =，3 → 补一个 =。
        // （旧实现用 (4 - 长度%4) %4 当余数，导致长度 %4==3 的段一个 = 都不补、
        //   FromBase64String 必抛——凡载荷字节数 %3==2 的激活码全部无法激活。）
        var remainder = padded.Length % 4;
        padded += remainder == 2 ? "==" : remainder == 3 ? "=" : string.Empty;
        return Convert.FromBase64String(padded);
    }
}
