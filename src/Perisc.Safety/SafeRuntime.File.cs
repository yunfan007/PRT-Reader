using System;
using System.Collections.Generic;
using System.IO;

namespace Perisc.Safety;

/// <summary>
/// 模块四 SRT · 文件系统（FS）：为文件与目录的敏感动作提供语义等价的替代函数。
/// <para>
/// 覆盖 5.4 ② 对照表 FS 行要求的全部方向：创建 / 写入 / 删除 / 改名与移动 / 目录枚举 / 符号链接创建。
/// 每个函数都完成「申报 → 裁决 → 执行 → 审计」四步（3.8 ②），返回结构化结果，不抛异常。
/// </para>
/// <para>
/// <b>关于读取</b>：4.6 的 FS 九项只管「改动」类动作（写、删、改、遍历、符号链接），
/// 读取文件内容落在 <c>PRIV-01</c>（读取文档库）。因此本类的读取函数以 <c>PRIV-01</c> 申报，
/// 使「读」与「写」在同一处收口，而编号仍可逐一追溯到 4.6（3.8「等效不增强」）。
/// </para>
/// </summary>
public sealed class SafeFile
{
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".bat", ".cmd", ".com", ".msi", ".ps1", ".sh", ".vbs", ".js", ".scr"
    };

    /// <summary>写出文本文件（整体覆盖）。取代 <c>File.WriteAllText</c>。</summary>
    public SafeResult WriteText(string path, string content, string? reason = null)
        => SafeExecutor.Run(ClassifyWrite(path), path, Reason(reason, "写入文件"),
            () => File.WriteAllText(path, content), MatchMode.Exact);

    /// <summary>追加文本文件。取代 <c>File.AppendAllText</c>。</summary>
    public SafeResult AppendText(string path, string content, string? reason = null)
        => SafeExecutor.Run(ClassifyWrite(path), path, Reason(reason, "追加内容到文件"),
            () => File.AppendAllText(path, content), MatchMode.Exact);

    /// <summary>写出二进制文件。取代 <c>File.WriteAllBytes</c>。</summary>
    public SafeResult WriteBytes(string path, byte[] bytes, string? reason = null)
        => SafeExecutor.Run(ClassifyWrite(path), path, Reason(reason, "写出二进制文件"),
            () => File.WriteAllBytes(path, bytes), MatchMode.Exact);

    /// <summary>删除文件。取代 <c>File.Delete</c>。</summary>
    public SafeResult Delete(string path, string? reason = null)
        => SafeExecutor.Run("FS-04", path, Reason(reason, "删除文件"),
            () => File.Delete(path), MatchMode.Exact);

    /// <summary>改名或移动文件。取代 <c>File.Move</c>。</summary>
    public SafeResult Move(string sourcePath, string destinationPath, string? reason = null)
        => SafeExecutor.Run("FS-05", sourcePath, Reason(reason, "移动文件到 " + Path.GetFileName(destinationPath)),
            () => File.Move(sourcePath, destinationPath), MatchMode.Exact);

    /// <summary>复制文件；目标另建，属创建类动作。取代 <c>File.Copy</c>。</summary>
    public SafeResult Copy(string sourcePath, string destinationPath, bool overwrite = false, string? reason = null)
        => SafeExecutor.Run(ClassifyWrite(destinationPath), destinationPath,
            Reason(reason, "复制文件到 " + Path.GetFileName(destinationPath)),
            () => File.Copy(sourcePath, destinationPath, overwrite), MatchMode.Exact);

    /// <summary>创建目录。取代 <c>Directory.CreateDirectory</c>。</summary>
    public SafeResult CreateDirectory(string path, string? reason = null)
        => SafeExecutor.Run("FS-01", path, Reason(reason, "创建目录"),
            () => { Directory.CreateDirectory(path); }, MatchMode.PathPrefix);

    /// <summary>删除目录。取代 <c>Directory.Delete</c>。</summary>
    public SafeResult DeleteDirectory(string path, bool recursive = false, string? reason = null)
        => SafeExecutor.Run("FS-04", path, Reason(reason, "删除目录"),
            () => Directory.Delete(path, recursive), MatchMode.PathPrefix);

    /// <summary>
    /// 枚举目录下的文件。取代 <c>Directory.GetFiles</c> / <c>EnumerateFiles</c>。
    /// 对象按目录申报（PathPrefix），使同目录的后续枚举可复用同一份许可。
    /// </summary>
    public SafeResult<string[]> EnumerateFiles(string directory, string searchPattern = "*", string? reason = null)
        => SafeExecutor.Run(ClassifyEnumerate(directory), directory,
            Reason(reason, "列出目录中的文件"),
            () => Directory.GetFiles(directory, searchPattern), MatchMode.PathPrefix);

    /// <summary>创建符号链接。取代 <c>File.CreateSymbolicLink</c>。</summary>
    public SafeResult CreateSymbolicLink(string linkPath, string targetPath, string? reason = null)
        => SafeExecutor.Run("FS-09", linkPath, Reason(reason, "创建符号链接指向 " + targetPath),
            () => { File.CreateSymbolicLink(linkPath, targetPath); }, MatchMode.Exact);

    // ── 读取（以 PRIV-01 申报，见类注释）──

    /// <summary>读取文本文件全部内容。取代 <c>File.ReadAllText</c>。</summary>
    public SafeResult<string> ReadText(string path, string? reason = null)
        => SafeExecutor.Run("PRIV-01", path, Reason(reason, "读取文件内容"),
            () => File.ReadAllText(path), MatchMode.Exact);

    /// <summary>按行读取文本文件。取代 <c>File.ReadAllLines</c>。</summary>
    public SafeResult<string[]> ReadLines(string path, string? reason = null)
        => SafeExecutor.Run("PRIV-01", path, Reason(reason, "按行读取文件"),
            () => File.ReadAllLines(path), MatchMode.Exact);

    /// <summary>读取二进制文件。取代 <c>File.ReadAllBytes</c>。</summary>
    public SafeResult<byte[]> ReadBytes(string path, string? reason = null)
        => SafeExecutor.Run("PRIV-01", path, Reason(reason, "读取二进制文件"),
            () => File.ReadAllBytes(path), MatchMode.Exact);

    /// <summary>判断文件是否存在。取代 <c>File.Exists</c>。</summary>
    public SafeResult<bool> Exists(string path, string? reason = null)
        => SafeExecutor.Run("PRIV-01", path, Reason(reason, "检查文件是否存在"),
            () => File.Exists(path), MatchMode.Exact);

    /// <summary>判断目录是否存在。取代 <c>Directory.Exists</c>。</summary>
    public SafeResult<bool> DirectoryExists(string path, string? reason = null)
        => SafeExecutor.Run("PRIV-01", path, Reason(reason, "检查目录是否存在"),
            () => Directory.Exists(path), MatchMode.PathPrefix);

    /// <summary>
    /// 按落点给写入动作选行为编号（4.2：判定看这次调用落在哪个编号上，而不是看用了哪个类型）。
    /// <list type="bullet">
    /// <item>可执行文件 / 脚本扩展名 → <c>FS-06</c>（写入可执行文件或脚本）；</item>
    /// <item>用户目录内（文档、图片、桌面、下载等）→ <c>FS-02</c>；</item>
    /// <item>程序自身目录内 → <c>FS-01</c>（创建文件或目录）；</item>
    /// <item>其余（系统目录、其它用户目录、网络位置）→ <c>FS-03</c>。</item>
    /// </list>
    /// </summary>
    private static string ClassifyWrite(string path)
    {
        var extension = Path.GetExtension(path);
        if (!string.IsNullOrEmpty(extension) && ExecutableExtensions.Contains(extension))
        {
            return "FS-06";
        }
        if (IsUnderUserDirectory(path))
        {
            return "FS-02";
        }
        return IsUnderProgramDirectory(path) ? "FS-01" : "FS-03";
    }

    /// <summary>
    /// 枚举的编号：越出用户目录按 <c>FS-07</c>（访问用户目录之外），否则按 <c>FS-08</c>（大规模遍历）。
    /// 规模阈值（&gt; 1000 条目/次）由执行侧的限额负责判定，申报侧只区分位置，避免把计数动作本身变成敏感动作。
    /// </summary>
    private static string ClassifyEnumerate(string directory)
        => IsUnderUserDirectory(directory) ? "FS-08" : "FS-07";

    private static bool IsUnderUserDirectory(string path)
    {
        var full = ToFullPath(path);
        if (full is null)
        {
            return false;
        }
        foreach (var root in UserRoots())
        {
            if (IsUnder(full, root))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsUnderProgramDirectory(string path)
    {
        var full = ToFullPath(path);
        return full is not null && IsUnder(full, AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
    }

    private static IEnumerable<string> UserRoots()
    {
        // 平台特定实现：按 Windows 的已知文件夹取用户目录根；非 Windows 上缺失的目录会被跳过，
        // 此时落点判定退化为「非用户目录」，行为仍 fail-closed（交由裁决而非放行）。
        foreach (var special in new[]
                 {
                     Environment.SpecialFolder.UserProfile,
                     Environment.SpecialFolder.MyDocuments,
                     Environment.SpecialFolder.MyPictures,
                     Environment.SpecialFolder.MyMusic,
                     Environment.SpecialFolder.MyVideos,
                     Environment.SpecialFolder.Desktop,
                 })
        {
            var root = Environment.GetFolderPath(special);
            if (!string.IsNullOrEmpty(root))
            {
                yield return root.TrimEnd(Path.DirectorySeparatorChar);
            }
        }

        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!string.IsNullOrEmpty(downloads))
        {
            yield return downloads.TrimEnd(Path.DirectorySeparatorChar);
        }
    }

    private static bool IsUnder(string fullPath, string root)
        => fullPath.Length > root.Length
           && fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
           && (fullPath[root.Length] == Path.DirectorySeparatorChar || fullPath[root.Length] == Path.AltDirectorySeparatorChar);

    private static string? ToFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string Reason(string? reason, string fallback)
        => string.IsNullOrWhiteSpace(reason) ? fallback : reason;
}
