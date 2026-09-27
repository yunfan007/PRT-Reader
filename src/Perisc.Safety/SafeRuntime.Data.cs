using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Perisc.Safety;

/// <summary>
/// 模块四 SRT · 外设与媒体（DEV）：剪贴板读取 / 写入。
/// <para>
/// <b>本类别未覆盖的方向</b>：摄像头（DEV-01）、麦克风（DEV-02）、屏幕捕获（DEV-03）、
/// USB/串口（DEV-06）、打印机（DEV-07）、位置（DEV-08）——本程序不使用这些能力，
/// 按 3.8「未覆盖的类别不触发 10.4.4 的封顶」，但若日后使用仍需履行 4.4 的申报义务
/// （须另行补齐函数，不得直接改调 BCL）。
/// </para>
/// </summary>
public sealed class SafeDevice
{
    /// <summary>
    /// 读取剪贴板文本（DEV-04）。取代 <c>Clipboard.GetText</c>。
    /// <para>
    /// <b>两种"没有文本"必须分开</b>：<see cref="SafeResult{T}.Value"/> 为 <c>null</c> 表示剪贴板里
    /// 根本没有文本格式（调用方应放弃粘贴，不要拿空串去覆盖选区）；为 <c>string.Empty</c>
    /// 表示确实有一段空文本。平台 API 本身就是这么区分的，本函数原样保留——
    /// 否则调用方为了分辨这两件事只能再申报一次「剪贴板是否含文本」，
    /// 而两次申报在单次许可下会变成两次弹窗（4.3.1 的分组去重只在同批次内生效）。
    /// </para>
    /// </summary>
    public SafeResult<string?> GetClipboardText(string? reason = null)
        => SafeExecutor.Run<string?>("DEV-04", "clipboard", Reason(reason, "读取剪贴板文本"),
            () => WindowsClipboard.GetText(), MatchMode.Exact);

    /// <summary>写入剪贴板文本（DEV-05）。取代 <c>Clipboard.SetText</c>。</summary>
    public SafeResult SetClipboardText(string text, string? reason = null)
        => SafeExecutor.Run("DEV-05", "clipboard", Reason(reason, "写入剪贴板文本"),
            () => WindowsClipboard.SetText(text), MatchMode.Exact);

    /// <summary>剪贴板是否含文本（DEV-04）。取代 <c>Clipboard.ContainsText</c>。</summary>
    public SafeResult<bool> ClipboardContainsText(string? reason = null)
        => SafeExecutor.Run("DEV-04", "clipboard", Reason(reason, "检查剪贴板是否含文本"),
            () => WindowsClipboard.ContainsText(), MatchMode.Exact);

    private static string Reason(string? reason, string fallback)
        => string.IsNullOrWhiteSpace(reason) ? fallback : reason;
}

/// <summary>
/// 模块四 SRT · 凭据与密钥（CRED）：读私钥 / 生成或导出密钥 / 加解密用户数据。
/// <para><b>本类别未覆盖的方向</b>：读系统凭据存储（CRED-01）、采集口令（CRED-04）、
/// 读其它程序凭据（CRED-06）、平台密钥链（CRED-07）——本程序不使用，理由同上。</para>
/// </summary>
public sealed class SafeCredential
{
    /// <summary>读取私钥文件（CRED-02）。取代直接 <c>File.ReadAllText</c> 读密钥文件。</summary>
    public SafeResult<string> ReadPrivateKey(string path, string? reason = null)
        => SafeExecutor.Run("CRED-02", path, Reason(reason, "读取私钥文件"),
            () => File.ReadAllText(path), MatchMode.Exact);

    /// <summary>读取私钥字节（CRED-02）。</summary>
    public SafeResult<byte[]> ReadPrivateKeyBytes(string path, string? reason = null)
        => SafeExecutor.Run("CRED-02", path, Reason(reason, "读取私钥文件"),
            () => File.ReadAllBytes(path), MatchMode.Exact);

    /// <summary>生成密钥对并导出 PEM 文本（CRED-03）。取代直接 <c>RSA.Create()</c> 后自行导出。</summary>
    public SafeResult<KeyPairExport> GenerateKeyPair(int keySizeInBits = 2048, string? reason = null)
        => SafeExecutor.Run("CRED-03", "rsa:" + keySizeInBits.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Reason(reason, "生成 RSA 密钥对"),
            () =>
            {
                using var rsa = RSA.Create(keySizeInBits);
                return new KeyPairExport(rsa.ExportRSAPublicKeyPem(), rsa.ExportRSAPrivateKeyPem());
            }, MatchMode.Exact);

    /// <summary>加密用户数据（CRED-05）：AES-CBC + 随机 IV，输出「IV‖密文」。</summary>
    public SafeResult<byte[]> EncryptUserData(byte[] plaintext, byte[] key, string? reason = null)
        => SafeExecutor.Run("CRED-05", "user-data:aes", Reason(reason, "加密用户数据"),
            () =>
            {
                using var aes = Aes.Create();
                aes.Key = key;
                aes.GenerateIV();
                using var transform = aes.CreateEncryptor();
                var body = transform.TransformFinalBlock(plaintext, 0, plaintext.Length);
                var output = new byte[aes.IV.Length + body.Length];
                Buffer.BlockCopy(aes.IV, 0, output, 0, aes.IV.Length);
                Buffer.BlockCopy(body, 0, output, aes.IV.Length, body.Length);
                return output;
            }, MatchMode.Exact);

    /// <summary>解密用户数据（CRED-05）：输入需为「IV‖密文」。</summary>
    public SafeResult<byte[]> DecryptUserData(byte[] packed, byte[] key, string? reason = null)
        => SafeExecutor.Run("CRED-05", "user-data:aes", Reason(reason, "解密用户数据"),
            () =>
            {
                using var aes = Aes.Create();
                aes.Key = key;
                var iv = new byte[aes.BlockSize / 8];
                Buffer.BlockCopy(packed, 0, iv, 0, iv.Length);
                aes.IV = iv;
                using var transform = aes.CreateDecryptor();
                return transform.TransformFinalBlock(packed, iv.Length, packed.Length - iv.Length);
            }, MatchMode.Exact);

    private static string Reason(string? reason, string fallback)
        => string.IsNullOrWhiteSpace(reason) ? fallback : reason;
}

/// <summary>密钥导出结果（PEM 文本）；属性对外只读。</summary>
public sealed class KeyPairExport
{
    public KeyPairExport(string publicKeyPem, string privateKeyPem)
    {
        PublicKeyPem = publicKeyPem;
        PrivateKeyPem = privateKeyPem;
    }

    /// <summary>公钥 PEM。</summary>
    public string PublicKeyPem { get; internal set; }

    /// <summary>私钥 PEM。</summary>
    public string PrivateKeyPem { get; internal set; }
}

/// <summary>
/// 模块四 SRT · 代码执行与反射（CODE）：加载程序集 / 反射非公开成员 / 反序列化。
/// <para><b>本类别未覆盖的方向</b>：动态求值或编译（CODE-01）、脚本注入宿主（CODE-02）、
/// 运行随包脚本（CODE-06）、修改自身映像（CODE-07）——本程序不使用；
/// 且按 1.5 的安全边界，动态代码执行不在本实现提供的能力范围内。</para>
/// </summary>
public sealed class SafeCode
{
    /// <summary>加载程序集（CODE-05）。取代 <c>Assembly.LoadFrom</c> / <c>LoadFile</c>。</summary>
    public SafeResult<System.Reflection.Assembly> LoadAssembly(string path, string? reason = null)
        => SafeExecutor.Run("CODE-05", path, Reason(reason, "加载程序集"),
            () => System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(
                Path.GetFullPath(path)), MatchMode.Exact);

    /// <summary>反射调用非公开成员（CODE-03）。取代 <c>Type.InvokeMember</c> 的非公开路径。</summary>
    public SafeResult<object?> InvokeNonPublic(object instance, string memberName, object?[]? args = null, string? reason = null)
        => SafeExecutor.Run("CODE-03", instance.GetType().FullName + "." + memberName,
            Reason(reason, "反射调用非公开成员 " + memberName),
            () =>
            {
                var type = instance.GetType();
                var method = type.GetMethod(memberName,
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public);
                if (method is null)
                {
                    throw new MissingMethodException(type.FullName, memberName);
                }
                return method.Invoke(instance, args ?? Array.Empty<object?>());
            }, MatchMode.Exact);

    /// <summary>反序列化不受信文本为对象（CODE-04）：仅接受 JSON，不接受可执行对象图。</summary>
    public SafeResult<T?> DeserializeJson<T>(string json, string? reason = null)
        => SafeExecutor.Run("CODE-04", typeof(T).FullName ?? typeof(T).Name,
            Reason(reason, "反序列化 JSON 为 " + typeof(T).Name),
            () => JsonSerializer.Deserialize<T>(json), MatchMode.Exact);

    private static string Reason(string? reason, string fallback)
        => string.IsNullOrWhiteSpace(reason) ? fallback : reason;
}

/// <summary>
/// 模块四 SRT · 用户数据与隐私（PRIV）：读文档库 / 上报。
/// <para><b>本类别未覆盖的方向</b>：通讯录与日历（PRIV-02）、浏览器数据（PRIV-03）、
/// 崩溃上报（PRIV-05）、其它程序数据目录（PRIV-06）——本程序不使用，理由同上。</para>
/// </summary>
public sealed class SafePrivacy
{
    /// <summary>枚举用户库目录中的文件（PRIV-01）。取代对文档库目录的直接 <c>Directory.GetFiles</c>。</summary>
    public SafeResult<string[]> EnumerateUserLibrary(string directory, string searchPattern = "*", string? reason = null)
        => SafeExecutor.Run("PRIV-01", directory, Reason(reason, "列出用户库中的文件"),
            () => Directory.GetFiles(directory, searchPattern), MatchMode.PathPrefix);

    /// <summary>读取用户库中的文本文件（PRIV-01）。</summary>
    public SafeResult<string> ReadUserDocument(string path, string? reason = null)
        => SafeExecutor.Run("PRIV-01", path, Reason(reason, "读取用户文档"),
            () => File.ReadAllText(path), MatchMode.Exact);

    /// <summary>
    /// 上报使用统计（PRIV-04）。
    /// <para>本实现<b>不发起任何网络连接</b>：上报内容只落到审计记录（detail 内），
    /// 与 3.1「最小采集」一致——审计只记敏感行为本身，不记输入内容或屏幕内容。
    /// 真正的外发需经 NET 类别（本实现未覆盖，见 <see cref="SafeRuntime"/> 的类型族说明）。</para>
    /// </summary>
    public SafeResult ReportTelemetry(string summary, string? reason = null)
        => SafeExecutor.Run("PRIV-04", "telemetry", Reason(reason, "上报使用统计"),
            () => { /* 记录即上报：见类注释，本实现不发起网络连接 */ }, MatchMode.Exact);

    private static string Reason(string? reason, string fallback)
        => string.IsNullOrWhiteSpace(reason) ? fallback : reason;
}

/// <summary>
/// 模块四 SRT · 资源占用（RES）：后台常驻 / 密集计算。
/// 这两项在 4.6 中的默认裁决为 <c>ask</c>（交用户决定），因此本类的函数主要用于
/// 「事前申报并获准」，而非替代某个 BCL 调用——调用方应据返回值决定是否进入该模式。
/// </summary>
public sealed class SafeResource
{
    /// <summary>声明进入长时间后台常驻（RES-01）；返回 Ok 方可常驻。</summary>
    public SafeResult BeginBackgroundResidency(string reason)
        => SafeExecutor.Run("RES-01", "process:background", Reason(reason, "进入后台常驻"),
            () => { /* 申报即义务：获准后由调用方自行维持常驻 */ }, MatchMode.Exact);

    /// <summary>声明进入密集计算（RES-02）；返回 Ok 方可开始。</summary>
    public SafeResult BeginIntensiveCompute(string scope, string reason)
        => SafeExecutor.Run("RES-02", "compute:" + scope, Reason(reason, "开始密集计算"),
            () => { /* 同上：获准后由调用方自行执行计算 */ }, MatchMode.Exact);

    private static string Reason(string? reason, string fallback)
        => string.IsNullOrWhiteSpace(reason) ? fallback : reason;
}

/// <summary>
/// Windows 剪贴板的最小封装（平台特定实现）。
/// 直接 P/Invoke user32.dll 而不是引用 WPF/WinForms，是为了让本库保持 net8.0（不绑定 UI 框架）。
/// 非 Windows 上这些入口不存在，调用会抛 <see cref="DllNotFoundException"/>，由 SRT 转成 Internal 返回。
/// </summary>
internal static class WindowsClipboard
{
    private const uint CfUnicodeText = 13;

    public static bool ContainsText()
    {
        if (!Native.OpenClipboard(IntPtr.Zero))
        {
            return false;
        }
        try
        {
            return Native.IsClipboardFormatAvailable(CfUnicodeText);
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    public static string? GetText()
    {
        if (!Native.OpenClipboard(IntPtr.Zero))
        {
            return null;
        }
        try
        {
            var handle = Native.GetClipboardData(CfUnicodeText);
            if (handle == IntPtr.Zero)
            {
                return null;
            }
            var pointer = Native.GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                Native.GlobalUnlock(handle);
            }
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    public static void SetText(string text)
    {
        if (!Native.OpenClipboard(IntPtr.Zero))
        {
            throw new InvalidOperationException("无法打开剪贴板。");
        }
        try
        {
            Native.EmptyClipboard();
            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            var handle = Native.GlobalAlloc(Native.GmemMoveable, (nuint)bytes.Length);
            if (handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("剪贴板内存分配失败。");
            }
            var pointer = Native.GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                Native.GlobalFree(handle);
                throw new InvalidOperationException("无法锁定剪贴板内存。");
            }
            try
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
            }
            finally
            {
                Native.GlobalUnlock(handle);
            }
            if (Native.SetClipboardData(CfUnicodeText, handle) == IntPtr.Zero)
            {
                Native.GlobalFree(handle);
                throw new InvalidOperationException("写入剪贴板失败。");
            }
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    private static class Native
    {
        public const uint GmemMoveable = 0x0002;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EmptyClipboard();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("user32.dll")]
        public static extern IntPtr GetClipboardData(uint format);

        [DllImport("user32.dll")]
        public static extern IntPtr SetClipboardData(uint format, IntPtr hMem);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GlobalFree(IntPtr hMem);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalUnlock(IntPtr hMem);
    }
}
