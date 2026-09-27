using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Perisc.Safety;

/// <summary>
/// 模块三（SafeGuard）客户端（4.6 / 5.2 / 6.1.4）：
/// - 命名管道 <c>\\.\pipe\Perisc.Safety.Guard.v1</c>（5.2 第 ① 条）；
/// - 帧格式：4 字节小端长度 + UTF-8 JSON，单帧上限 1 MiB（5.2 第 ② 条）；
/// - 信封：<c>{"protocolVersion":"1.0","id":"m-N","method":…,"params":…}</c>，
///   应答为 <c>result</c> 或 <c>error{code,detail}</c>（5.2 第 ③④ 条）；
/// - 心跳：默认 5 秒、上限 30 秒（12.2），由 <see cref="StartHeartbeatAsync"/> 显式启动（4.9 第 ① 步）；
/// - 终止通知：收到 <c>terminate</c> 后触发 <see cref="Terminating"/>，窗口取
///   <c>min(程序声明值, 守护服务策略值, 2000 ms)</c>（2.4 / 5.2 第 ⑤ 条）。
/// 接入模式 A（Native）下守护为可选增强：连不上**不抛异常**，返回实例的
/// <see cref="IsConnected"/> 为 false，申报/拦截路径照常降级（2.6 / 4.6）。
/// </summary>
public sealed partial class SafeGuardClient : IDisposable
{
    /// <summary>默认端点全名（4.6 / 5.2 第 ① 条）；协议主版本变化时随主版本递增。</summary>
    public const string DefaultPipeName = @"\\.\pipe\Perisc.Safety.Guard.v1";

    /// <summary>本实现遵循的线协议版本（5.2 / 7.6）。</summary>
    private const string LocalProtocolVersion = "1.0";

    private const int MaxFrameBytes = 1024 * 1024;  // 单帧上限 1 MiB（5.2 第 ② 条）
    private const int MaxNoticeWindowMs = 2000;      // 通知窗口硬上限（2.4）
    private const int DefaultHeartbeatMs = 5000;     // 心跳默认 5 s（12.2）
    private const int MinHeartbeatMs = 500;
    private const int MaxHeartbeatMs = 30000;        // 心跳上限 30 s（12.2）
    private const int ShutdownWaitMs = 2000;         // Dispose 等在途循环退出的上限（见 Dispose）

    private readonly SafeGuardOptions _options;
    private readonly SafetyProcessState _runtime;
    private readonly string _programId;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RpcReply>> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();

    private NamedPipeClientStream? _pipe;
    private SemaphoreSlim? _writeLock;   // 纯托管闸门：随客户端一起被丢弃，不显式释放（见 Dispose / TeardownAsync）
    private Task? _receiveLoop;
    private Task? _heartbeatLoop;
    private CancellationTokenSource? _heartbeatCts;
    private int _seq;
    private int _messageId;
    private int _heartbeatIntervalMs = DefaultHeartbeatMs;
    private bool _heartbeatRequested;
    private bool _handshakeCompleted;
    private bool _disposed;

    /// <summary>
    /// 构造：运行时作为**依赖传入**，不再在每个调用点现取 <c>SafetyEnvironment.Runtime</c>。
    /// <para>
    /// 这一点同时修掉两个问题：① 每次现取都会把进程运行时"钉住"（见 <c>SafetyEnvironment</c>），
    /// 让宿主失去按引用计数自动拆除的机会；② 拿到的运行时可能在两次调用之间被换掉，
    /// 于是"连接时用的审计存储"和"断线时写审计用的存储"可以不是同一个。
    /// 现在一份客户端只认一份运行时，归还点明确（<see cref="Dispose"/>）。
    /// </para>
    /// </summary>
    private SafeGuardClient(SafetyProcessState runtime, string programId, SafeGuardOptions options)
    {
        _runtime = runtime;
        _programId = programId;
        _options = options;
        NoticeWindowMs = ClampNoticeWindow(options.TerminationNoticeWindowMs);
    }

    /// <summary>守护连接是否在位；连接失败时为 false（4.6：不抛异常）。</summary>
    public bool IsConnected { get; private set; }

    /// <summary>
    /// 服务侧协商后的线协议版本（7.6）；未连接时为 null（4.6）。
    /// </summary>
    public Version? ProtocolVersion { get; private set; }

    /// <summary>
    /// 实际生效的终止通知窗口（2.4：<c>min(程序声明值, 守护服务策略值, 2000 ms)</c>）。
    /// 实现级补充，供宿主展示与收尾预算使用。
    /// </summary>
    public int NoticeWindowMs { get; private set; }

    /// <summary>本程序运行体哈希（2.4 的 versionHash）。</summary>
    public string VersionHash => ProgramIdentity.VersionHash;

    /// <summary>守护失联（连接建立后掉线）时触发；重连成功后不再重复触发直到再次失联。</summary>
    public event EventHandler? ConnectionLost;

    /// <summary>收到守护终止通知（2.4）：通知窗口内程序可展示解释界面；窗口到期由守护强制终止。</summary>
    public event EventHandler<TerminationNotice>? Terminating;

    // ───────────────────────────── 连接 ─────────────────────────────

    /// <summary>
    /// 连接本地守护服务（4.6）。连接失败**不抛异常**，返回的实例 <see cref="IsConnected"/> 为 false，
    /// 后续 <c>guard.*</c> 调用返回 <see cref="PssCode.GuardUnavailable"/>；不得阻断程序正常功能（2.6）。
    /// </summary>
    public static SafeGuardClient Connect(SafeGuardOptions? options = null) => RunSync(() => ConnectAsync(options));

    /// <summary>连接（异步；用于在启动流程中等待服务就绪）。同样不抛异常，失败返回未连接的实例。</summary>
    public static Task<SafeGuardClient> ConnectAsync(SafeGuardOptions? options = null,
                                                     CancellationToken cancellationToken = default)
        => ConnectAsync(programId: null, options, cancellationToken);

    /// <summary>连接（实现级扩展：显式声明本程序标识，便于同一进程内多程序标识的场景）。</summary>
    public static SafeGuardClient Connect(string programId, SafeGuardOptions? options = null)
        => RunSync(() => ConnectAsync(programId, options));

    /// <summary>连接（实现级扩展：显式声明本程序标识；null 表示按运行时登记值与进程名解析）。</summary>
    public static async Task<SafeGuardClient> ConnectAsync(string? programId, SafeGuardOptions? options = null,
                                                           CancellationToken cancellationToken = default)
    {
        var opts = options ?? new SafeGuardOptions();
        // 记一份运行时持有（D-12）：连接过程本身要读程序标识，断线留痕要写审计，
        // 两者都必须落在同一份运行时上；Dispose 时归还。
        var runtime = SafetyEnvironment.Acquire();
        SafeGuardClient client;
        try
        {
            client = new SafeGuardClient(runtime, ResolveProgramId(runtime, programId), opts);
        }
        catch
        {
            // 构造失败也要还回持有，否则这份引用就永远回不到零（拆除时机被无声推迟）。
            SafetyEnvironment.Release(runtime);
            throw;
        }
        await client.TryConnectAsync(cancellationToken).ConfigureAwait(false);
        return client;
    }

    /// <summary>尝试建立连接；失败返回 false（不抛异常）。</summary>
    private async Task<bool> TryConnectAsync(CancellationToken cancellationToken)
    {
        var interval = (int)(_options.HeartbeatInterval?.TotalMilliseconds ?? DefaultHeartbeatMs);
        _heartbeatIntervalMs = Math.Clamp(interval, MinHeartbeatMs, MaxHeartbeatMs);

        NamedPipeClientStream pipe;
        try
        {
            pipe = new NamedPipeClientStream(".", EndpointName(_options.PipeName), PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(2000);
        }
        catch
        {
            _runtime.SetGuardUnavailable(true);
            return false;
        }

        _pipe = pipe;
        _writeLock = new SemaphoreSlim(1, 1);

        // 必须先启动接收循环，再发 guard.connect：应答同样经接收循环按 id 派发，
        // 若把它留到握手之后才启动，握手应答永远无人读取，连接必然超时失败（4.6）。
        // 接收循环的存活期跟随本实例（_cts.Token），而不是单次调用的 cancellationToken：
        // 显式传 CancellationToken.None 表示"有意不传播调用方令牌"——否则调用方取消一次
        // 握手，就会把已经建立的接收循环一并掐断（CA2016 要求的正是这个显式声明）。
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);

        var reply = await CallAsync("guard.connect", new Dictionary<string, object>
        {
            ["programId"] = _programId,
            ["versionHash"] = VersionHash,
            ["mode"] = PssText.FromIntegrationMode(_options.Mode),
        }, _heartbeatIntervalMs, cancellationToken).ConfigureAwait(false);

        if (!reply.Ok)
        {
            await TeardownAsync().ConfigureAwait(false);
            _runtime.SetGuardUnavailable(true);
            return false;
        }

        // 5.2 第 ⑤ 条：guard.connect 的 result 携带 connected / protocolVersion / noticeWindowMs。
        if (reply.Result.ValueKind == JsonValueKind.Object &&
            reply.Result.TryGetProperty("connected", out var connectedProp) &&
            connectedProp.ValueKind == JsonValueKind.False)
        {
            await TeardownAsync().ConfigureAwait(false);
            _runtime.SetGuardUnavailable(true);
            return false;
        }

        ProtocolVersion = ReadProtocolVersion(reply.Result) ?? new Version(1, 0);
        if (reply.Result.ValueKind == JsonValueKind.Object &&
            reply.Result.TryGetProperty("noticeWindowMs", out var windowProp) &&
            windowProp.ValueKind == JsonValueKind.Number)
        {
            NoticeWindowMs = ClampNoticeWindow(Math.Min(_options.TerminationNoticeWindowMs, windowProp.GetInt32()));
        }

        IsConnected = true;
        _handshakeCompleted = true;
        _runtime.RegisterProgram(_programId);
        _runtime.SetGuardUnavailable(false);
        if (_heartbeatRequested)
        {
            StartHeartbeatLoopLocked();
        }
        return true;
    }

    private static Version? ReadProtocolVersion(JsonElement result)
        => result.ValueKind == JsonValueKind.Object &&
           result.TryGetProperty("protocolVersion", out var v) && v.ValueKind == JsonValueKind.String &&
           Version.TryParse(v.GetString(), out var version)
            ? version
            : null;

    private static int ClampNoticeWindow(int windowMs) => Math.Clamp(windowMs, 0, MaxNoticeWindowMs);

    /// <summary>命名管道构造只接受端点名（不含 <c>\\.\pipe\</c> 前缀）。</summary>
    private static string EndpointName(string pipeName)
    {
        const string prefix = @"\\.\pipe\";
        var name = string.IsNullOrWhiteSpace(pipeName) ? DefaultPipeName : pipeName.Trim();
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? name[prefix.Length..] : name;
    }

    private static string ResolveProgramId(SafetyProcessState runtime, string? explicitId)
    {
        if (!string.IsNullOrWhiteSpace(explicitId))
        {
            return explicitId.Trim();
        }
        var registered = runtime.ProgramId;
        if (!string.IsNullOrWhiteSpace(registered) && !string.Equals(registered, "unknown-program", StringComparison.Ordinal))
        {
            return registered;
        }
        try
        {
            var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name!;
            }
        }
        catch
        {
            // 吞掉的是"取自身进程名失败"（Environment.ProcessPath 为 null 或路径不可解析）。
            // 降级到下面的 "unknown-program"：程序标识本来就是尽力而为——4.6 只要求尽量给出可读标识，
            // 守护侧对 unknown 同样有处置路径。何时应传播：不需要，此处失败不影响连接与心跳。
        }
        return "unknown-program";
    }

}
