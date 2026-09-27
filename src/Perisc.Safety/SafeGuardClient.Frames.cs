using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Perisc.Safety;

// RPC 收发、帧解析与终止通知处置（SafeGuardClient 的 partial；D-06）
// 拆分依据：《工程改进方案》§4.8 / CRS 9.6.4 D-06；拆分**只做位置迁移**，未改行为。
public sealed partial class SafeGuardClient : IDisposable
{
    // ───────────────────────────── 收发与帧 ─────────────────────────────

    private sealed class RpcReply
    {
        /// <summary>未送达 / 未应答（管道不通、超时、帧损坏）——一律按 GuardUnavailable 处理（5.2 第 ⑩ 条）。</summary>
        public bool TransportFailed { get; init; }

        public bool Ok { get; init; }
        public PssCode Code { get; init; } = PssCode.Internal;
        public string Detail { get; init; } = string.Empty;
        public JsonElement Result { get; init; }

        public static RpcReply Failed(string detail) => new()
        {
            TransportFailed = true,
            Ok = false,
            Code = PssCode.GuardUnavailable,
            Detail = detail,
        };
    }

    /// <summary>调用一个守护方法；超时/管道不通返回 TransportFailed（不抛异常）。</summary>
    private async Task<RpcReply> CallAsync(string method, object parameters, int timeoutMs, CancellationToken cancellationToken)
    {
        var id = "m-" + Interlocked.Increment(ref _messageId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var tcs = new TaskCompletionSource<RpcReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            // 5.2 第 ③ 条：请求信封固定带 protocolVersion。
            var sent = await SendAsync(new Dictionary<string, object>
            {
                ["protocolVersion"] = LocalProtocolVersion,
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters,
            }).ConfigureAwait(false);
            if (!sent)
            {
                return RpcReply.Failed("守护服务未连接或帧写出失败");
            }

            var timeout = Task.Delay(timeoutMs, cancellationToken);
            var finished = await Task.WhenAny(tcs.Task, timeout).ConfigureAwait(false);
            if (finished != tcs.Task)
            {
                return RpcReply.Failed("守护服务未在限时内应答");
            }
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return RpcReply.Failed("调用已取消");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private static PssResult ToPssResult(RpcReply reply, string okDetail)
    {
        if (reply.TransportFailed)
        {
            return new PssResult(PssCode.GuardUnavailable, reply.Detail, degraded: true);
        }
        return reply.Ok
            ? new PssResult(PssCode.Ok, string.IsNullOrEmpty(reply.Detail) ? okDetail : reply.Detail)
            : new PssResult(reply.Code, reply.Detail, degraded: reply.Code == PssCode.GuardUnavailable);
    }

    private async Task<bool> SendAsync(object envelope)
    {
        var pipe = _pipe;
        var gate = _writeLock;
        if (pipe is null || gate is null || !pipe.IsConnected)
        {
            return false;
        }
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(envelope);
            if (payload.Length > MaxFrameBytes)
            {
                return false;
            }
            await gate.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                var head = new byte[4];
                head[0] = (byte)payload.Length;
                head[1] = (byte)(payload.Length >> 8);
                head[2] = (byte)(payload.Length >> 16);
                head[3] = (byte)(payload.Length >> 24);
                await pipe.WriteAsync(head, _cts.Token).ConfigureAwait(false);
                await pipe.WriteAsync(payload, _cts.Token).ConfigureAwait(false);
                await pipe.FlushAsync(_cts.Token).ConfigureAwait(false);
                return true;
            }
            finally
            {
                gate.Release();
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>接收循环：逐帧读取并分发（应答按 id 完成等待方；terminate 触发 Terminating）。</summary>
    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var pipe = _pipe;
        if (pipe is null)
        {
            return;
        }
        try
        {
            var head = new byte[4];
            while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
            {
                if (!await ReadExactAsync(pipe, head, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
                var length = head[0] | (head[1] << 8) | (head[2] << 16) | (head[3] << 24);
                if (length <= 0 || length > MaxFrameBytes)
                {
                    break; // 帧损坏：按失联处理（5.2 第 ② 条）
                }
                var payload = new byte[length];
                if (!await ReadExactAsync(pipe, payload, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }

                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (root.TryGetProperty("method", out var methodProp) &&
                    methodProp.ValueKind == JsonValueKind.String &&
                    methodProp.GetString() == "terminate")
                {
                    HandleTerminate(root);
                    continue;
                }
                if (root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                {
                    var id = idProp.GetString()!;
                    if (_pending.TryRemove(id, out var tcs))
                    {
                        tcs.TrySetResult(ParseReply(root));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            // 读循环异常：按失联处理。
        }

        // 只有「握手成功过」的连接才报告失联：握手期失败已由 TryConnectAsync 自行判定，
        // 此处若一并报告会重复记账并叠出第二层重连循环（2.6 / 4.6）。
        if (_handshakeCompleted && !_disposed)
        {
            await OnConnectionLostAsync().ConfigureAwait(false);
        }
    }

    /// <summary>解析应答信封（5.2 第 ④ 条）：<c>result</c> 或 <c>error{code,detail}</c>。</summary>
    private static RpcReply ParseReply(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            return new RpcReply
            {
                Ok = false,
                Code = PssText.ParseCode(GetString(error, "code")),
                Detail = GetString(error, "detail"),
            };
        }
        if (root.TryGetProperty("result", out var result))
        {
            return new RpcReply { Ok = true, Code = PssCode.Ok, Result = result.Clone() };
        }
        return new RpcReply { Ok = false, Code = PssCode.Internal, Detail = "应答缺少 result / error 分支" };
    }

    private void HandleTerminate(JsonElement root)
    {
        try
        {
            var p = root.TryGetProperty("params", out var pp) ? pp : root;
            var eventId = GetString(p, "eventId");
            var rule = GetString(p, "rule");
            var target = GetString(p, "target");
            var deadline = p.TryGetProperty("deadline", out var d) && DateTimeOffset.TryParse(d.GetString(), out var dl)
                ? dl
                : _runtime.Now().AddMilliseconds(NoticeWindowMs);

            var notice = new TerminationNotice(eventId, rule, target, deadline);

            // 本地留痕：终止事件属安全设施处置，必须可追溯（2.4）。
            try
            {
                _runtime.Audit?.TryAppend(new AuditRecord(
                    _runtime.ProgramId, action: string.Empty, target,
                    AuditDecision.Escalation, PssCode.Internal, AuditLayer.Guard,
                    detail: $"termination-notice：守护终止通知（规则 {rule}，事件 {eventId}，截止 {deadline:HH:mm:ss.fff}）",
                    evidence: null));
            }
            catch
            {
                // 吞掉的是"终止通知的留痕写不进"；降级到"仅内存通知"——下一行仍会回调 Terminating，
                // 宿主与守护的处置路径不受影响（2.4 的终止由守护侧执行，不依赖客户端留痕成功）。
                // 何时应传播：不需要——终止已在推进，客户端留痕失败不改变终止结果。
            }

            var handler = Terminating;
            handler?.Invoke(this, notice);
        }
        catch
        {
            // 通知解析失败不影响守护自身按窗口强制终止。
        }
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : string.Empty;
}
