using System;
using System.IO;
using System.Security.Cryptography;

namespace Perisc.Safety;

/// <summary>
/// 本程序身份（实现内部）：供守护服务做 HASH-MISMATCH 判定与自更新白名单基准
/// （PSS 标准 2.4：<c>versionHash</c> 是哈希不一致判定的唯一数据源）。
/// </summary>
internal static class ProgramIdentity
{
    private static readonly Lazy<string> s_versionHash = new(ComputeVersionHash);

    /// <summary>
    /// 运行体哈希，格式 <c>sha256:&lt;64 位十六进制&gt;</c>（6.2.3）。
    /// 取当前进程主模块（可执行文件）的 SHA-256；取不到时返回空串——
    /// 空串表示"本程序未声明运行体"，守护服务不得据此放行（fail-closed）。
    /// </summary>
    public static string VersionHash => s_versionHash.Value;

    private static string ComputeVersionHash()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return string.Empty;
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var digest = SHA256.HashData(stream);
            return "sha256:" + Convert.ToHexString(digest).ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }
}
