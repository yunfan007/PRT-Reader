using System;
using System.IO;
using System.Text;

namespace Perisc.Safety;

/// <summary>
/// 匹配模式与规范化（PSS 标准 4.7.1，规范性）。
/// Validate* 用于申报时校验（6.1.1 第 4/5 条）；IsMatch 用于许可缓存命中与拦截层判定。
/// </summary>
internal static class Matching
{
    /// <summary>
    /// 校验 (Action 类别, Target, MatchMode) 组合是否合法。
    /// 返回 null 表示合法；否则返回 BadArg 的 detail 文本。
    /// </summary>
    public static string? ValidateTarget(string category, string target, MatchMode mode)
    {
        switch (mode)
        {
            case MatchMode.Exact:
                return null; // 全类别可用：规范化后全文相等，无额外推断

            case MatchMode.PathPrefix:
                if (category != "FS")
                {
                    return "PathPrefix 仅可用于 FS 全部项（CFG 目录类对象除外，本实现未开放）";
                }
                return ValidatePathPrefix(target);

            case MatchMode.HostSuffix:
                if (category != "NET")
                {
                    return "HostSuffix 仅可用于 NET 全部项";
                }
                return ValidateHostSuffix(target);

            case MatchMode.HostPortRange:
                if (category != "NET")
                {
                    return "HostPortRange 仅可用于 NET 全部项";
                }
                return ValidateHostPortRange(target);

            default:
                return "未知的匹配模式";
        }
    }

    /// <summary>判定实际对象 actual 是否落在声明 (target, mode) 的范围内。</summary>
    public static bool IsMatch(string target, MatchMode mode, string actual)
    {
        if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(actual))
        {
            return false;
        }

        switch (mode)
        {
            case MatchMode.Exact:
                return string.Equals(target.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase);

            case MatchMode.PathPrefix:
                return PathSegmentsMatch(target, actual);

            case MatchMode.HostSuffix:
                return HostSuffixMatch(target, actual);

            case MatchMode.HostPortRange:
                return HostPortRangeMatch(target, actual);

            default:
                return false;
        }
    }

    // ───────────────────────────── PathPrefix ─────────────────────────────

    private static string? ValidatePathPrefix(string target)
    {
        if (!Path.IsPathRooted(target))
        {
            return "PathPrefix 的对象必须是绝对路径";
        }

        // `..` 必须在规范化**之前**按原文判定：Path.GetFullPath 会把它解析掉，之后再查永远查不到。
        foreach (var seg in target.Split('\\', '/'))
        {
            if (seg == "..")
            {
                return "PathPrefix 禁止 .. 穿越段";
            }
        }

        var full = Path.GetFullPath(target);
        var root = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(root) && string.Equals(full.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            return "PathPrefix 禁止驱动器根（如 C:\\、/）";
        }

        if (target.Contains('*', StringComparison.Ordinal) || target.Contains('?', StringComparison.Ordinal))
        {
            return "PathPrefix 禁止通配符";
        }

        return null;
    }

    /// <summary>
    /// 路径分段比较（4.7.1）：按目录分段对齐，禁止字符串前缀直比——
    /// C:\Data 不得匹配 C:\Database。符号链接解析失败按不匹配处理（fail-closed）。
    /// </summary>
    private static bool PathSegmentsMatch(string declared, string actual)
    {
        try
        {
            var declFull = Resolve(Path.GetFullPath(declared.Trim()));
            var actFull = Resolve(Path.GetFullPath(actual.Trim()));
            if (declFull is null || actFull is null)
            {
                return false; // 解析失败（不存在、权限不足）按 Deny
            }

            var declSegs = declFull.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var actSegs = actFull.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (actSegs.Length < declSegs.Length)
            {
                return false;
            }

            for (var i = 0; i < declSegs.Length; i++)
            {
                if (!string.Equals(declSegs[i], actSegs[i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }
        catch
        {
            return false; // 路径非法：不匹配（不得按"看起来在前缀内"放行）
        }
    }

    /// <summary>解析符号链接后的最终路径；解析失败返回 null（4.7.1）。</summary>
    private static string? Resolve(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                var dir = new DirectoryInfo(path);
                if (dir.Exists)
                {
                    var target = dir.ResolveLinkTarget(returnFinalTarget: true);
                    return target?.FullName ?? path;
                }
                return path; // 尚不存在的目标（如待创建文件）：按字面路径比较
            }

            var link = info.ResolveLinkTarget(returnFinalTarget: true);
            return link?.FullName ?? path;
        }
        catch
        {
            return null;
        }
    }

    // ───────────────────────────── HostSuffix ─────────────────────────────

    private static string? ValidateHostSuffix(string target)
    {
        var t = target.Trim();
        if (t.StartsWith("*.", StringComparison.Ordinal))
        {
            var domain = t.Substring(2);
            if (domain.Length == 0 || domain.Contains('*', StringComparison.Ordinal) || domain.Contains('?', StringComparison.Ordinal))
            {
                return "HostSuffix 只接受 *.域 或 域 两种写法";
            }
            if (!domain.Contains('.', StringComparison.Ordinal))
            {
                return "HostSuffix 禁止顶级域（如 *.com）";
            }
            if (domain.Split('.', StringSplitOptions.RemoveEmptyEntries).Length < 2)
            {
                return "HostSuffix 禁止顶级域（如 *.com）";
            }
            return null;
        }

        if (t.Contains('*', StringComparison.Ordinal) || t.Contains('?', StringComparison.Ordinal))
        {
            return "HostSuffix 只接受 *.域 或 域 两种写法";
        }
        if (t.Length == 0 || t.Contains('/', StringComparison.Ordinal) || t.Contains(':', StringComparison.Ordinal))
        {
            return "HostSuffix 的对象文本不应包含路径分隔符或端口（端口区间请用 HostPortRange）";
        }
        return null;
    }

    /// <summary>
    /// 主机名以声明后缀结尾且至少多一层：*.example.com 匹配 a.example.com，
    /// 不匹配 example.com（4.7.1）。声明为裸域时按精确匹配判定。
    /// </summary>
    private static bool HostSuffixMatch(string declared, string actual)
    {
        var declHost = SplitHostPort(actual.Trim()).Host;
        if (declHost.Length == 0)
        {
            return false;
        }

        var d = declared.Trim();
        if (d.StartsWith("*.", StringComparison.Ordinal))
        {
            var domain = d.Substring(2);
            if (declHost.Length <= domain.Length + 1)
            {
                return false; // 至少多一层
            }
            return declHost.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)
                && !declHost.Equals(domain, StringComparison.OrdinalIgnoreCase);
        }

        return declHost.Equals(d, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────────────────────── HostPortRange ─────────────────────────

    /// <summary>HostPortRange 声明格式：host:起-止（如 example.com:443-465）。</summary>
    private static string? ValidateHostPortRange(string target)
    {
        var t = target.Trim();
        var idx = t.LastIndexOf(':');
        if (idx <= 0 || idx == t.Length - 1)
        {
            return "HostPortRange 的对象文本格式应为 host:起-止";
        }

        var host = t.Substring(0, idx);
        var range = t.Substring(idx + 1);
        if (host.Contains('*', StringComparison.Ordinal) || host.Contains('?', StringComparison.Ordinal))
        {
            return "HostPortRange 的主机部分不得含通配符";
        }

        var dash = range.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0 || dash == range.Length - 1)
        {
            return "HostPortRange 的端口区间格式应为 起-止";
        }

        if (!int.TryParse(range.Substring(0, dash), out var lo) ||
            !int.TryParse(range.Substring(dash + 1), out var hi))
        {
            return "HostPortRange 的端口区间必须为数字";
        }

        if (lo < 1 || hi > 65535 || lo > hi)
        {
            return "HostPortRange 的端口区间必须在 1 ~ 65535 内且起 ≤ 止";
        }

        if (lo == 1 && hi == 65535)
        {
            return "HostPortRange 禁止 0-65535（等价于全端口）";
        }

        return null;
    }

    private static bool HostPortRangeMatch(string declared, string actual)
    {
        var t = declared.Trim();
        var idx = t.LastIndexOf(':');
        if (idx <= 0)
        {
            return false;
        }

        var declHost = t.Substring(0, idx);
        var range = t.Substring(idx + 1);
        var dash = range.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0 || !int.TryParse(range.Substring(0, dash), out var lo) || !int.TryParse(range.Substring(dash + 1), out var hi))
        {
            return false;
        }

        var (actHost, actPort) = SplitHostPort(actual.Trim());
        if (actPort is null || actPort < lo || actPort > hi)
        {
            return false;
        }

        // 主机按 Exact 判定（4.7.1：主机与端口必须分别判定）。
        return actHost.Equals(declHost, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把 "host:port" 拆开；端口混入主名字段是 4.7.1 明令禁止的比较错误。</summary>
    internal static (string Host, int? Port) SplitHostPort(string text)
    {
        var idx = text.LastIndexOf(':');
        if (idx > 0 && int.TryParse(text.Substring(idx + 1), out var port))
        {
            return (text.Substring(0, idx), port);
        }
        return (text, null);
    }

    /// <summary>供审计与展示用的规范化文本（去除首尾空白；不做其它推断）。</summary>
    internal static string Normalize(string text) => text.Trim();
}
