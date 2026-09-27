using System.IO;

namespace Prt.App.Services;

/// <summary>
/// 旧位置配置的一次性迁移：把历史上写在 <c>%APPDATA%</c> 下的本程序配置搬进程序目录。
/// <para>
/// <b>为什么要有它</b>：便携化改造（《设计取舍》第 13 条）把读写候选收到程序目录后，
/// 老用户原本存在 <c>%APPDATA%</c> 的设置与会话就看不到了。这里在**首次启动时读一次旧位置**
/// 把内容搬过来，之后程序不再读写 <c>%APPDATA%</c>——「一次性」就是这条路径的全部授权范围。
/// </para>
/// <para>
/// <b>安全顺序（不可调换）</b>：先写入程序目录 → <b>读回校验内容一致</b> → 才删除旧文件。
/// 任一环节失败都保留旧文件并记下原因：迁移绝不能退化成「删了又没搬过去」。
/// </para>
/// <para>
/// <b>迁移范围</b>只有本程序会读的三样：<c>settings.json</c>、<c>session.json</c>、
/// 自定义启动图目录 <c>resources\</c>。前两样是文本，启动图是**二进制**——两者走不同的搬运通道
/// （见 <see cref="Transfer"/>）。授权凭据（<c>license.key</c>、<c>activations.json</c>）
/// 自始就是纯便携、历史版本也从未从 <c>%APPDATA%</c> 读取，故**不在**迁移范围内、也**不**删除
/// ——它们是用户手上可能唯一的授权实物，不该由一次配置迁移顺手抹掉。
/// </para>
/// <para>
/// 旧目录本身（以及其中的授权凭据遗留物）**不删除**：本类只删它自己搬走的文件，
/// 不替用户清理目录。
/// </para>
/// </summary>
internal static class LegacyConfigMigration
{
    /// <summary>历史版本的配置目录名（改名沿革：PRT 阅读器 ← PRT 工具 ← PSR 工具 ← MDP 工具）。</summary>
    private static readonly string[] LegacyFolderNames = ["PRT 阅读器", "PRT 工具", "PSR 工具", "MDP 工具"];

    /// <summary>需要迁移的文件（程序目录下的文件名即旧位置的文件名）。</summary>
    private static readonly string[] MigratedFileNames = ["settings.json", "session.json"];

    /// <summary>自定义启动图所在子目录名（与 <see cref="SettingsStore"/> 一致）。</summary>
    private const string ResourceFolderName = "resources";

    /// <summary>迁移结果摘要。</summary>
    /// <param name="Migrated">成功迁移（含旧文件未能删除的情形）的条目数。</param>
    /// <param name="Skipped">程序目录已有同名文件、因而不迁移的条目数。</param>
    /// <param name="Failed">迁移失败（已保留旧文件）的条目数。</param>
    /// <param name="Notes">逐条说明，供日志与自检回溯。</param>
    internal sealed record Summary(int Migrated, int Skipped, int Failed, IReadOnlyList<string> Notes);

    /// <summary>按真实环境执行一次迁移（程序目录 ← <c>%APPDATA%</c> 的历史目录）。</summary>
    public static Summary RunOnce() => Migrate(PortableStorage.Directory, LegacyDirectoryPaths());

    /// <summary><c>%APPDATA%</c> 下可能存放过旧配置的目录（按改名沿革由新到旧）。</summary>
    public static IReadOnlyList<string> LegacyDirectoryPaths()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData))
        {
            return [];
        }

        return [.. LegacyFolderNames.Select(name => Path.Combine(appData, name))];
    }

    /// <summary>
    /// 执行迁移。目标目录与旧位置都作为参数传入，便于自检用临时目录复现整条链路
    /// （不碰真实用户数据）。
    /// </summary>
    public static Summary Migrate(string targetDirectory, IReadOnlyList<string> legacyDirectories)
    {
        var notes = new List<string>();
        var migrated = 0;
        var skipped = 0;
        var failed = 0;

        void Tally(Outcome outcome)
        {
            switch (outcome)
            {
                case Outcome.Migrated: migrated++; break;
                case Outcome.Skipped: skipped++; break;
                case Outcome.Failed: failed++; break;
                case Outcome.Absent: break;
            }
        }

        foreach (var fileName in MigratedFileNames)
        {
            Tally(MigrateEntry(fileName, Path.Combine(targetDirectory, fileName), legacyDirectories, notes));
        }

        var resourceFiles = LegacyResourceFiles(legacyDirectories, notes);
        if (resourceFiles.Count > 0)
        {
            if (TryPrepareResourceFolder(targetDirectory, out var resourceFolder, notes))
            {
                foreach (var (source, name) in resourceFiles)
                {
                    Tally(MigrateEntry(
                        name,
                        Path.Combine(resourceFolder, name),
                        [Path.GetDirectoryName(source)!],
                        notes,
                        asBinary: true));
                }
            }
            else
            {
                failed += resourceFiles.Count;
            }
        }

        return new Summary(migrated, skipped, failed, notes);
    }

    /// <summary>单条目在旧位置与目标位置之间的流转结果。</summary>
    private enum Outcome
    {
        /// <summary>旧位置没有这个文件，无需迁移。</summary>
        Absent,

        /// <summary>程序目录已有，跳过。</summary>
        Skipped,

        /// <summary>已迁移（旧文件可能因删除失败而残留，原因记在 Notes）。</summary>
        Migrated,

        /// <summary>迁移失败，旧文件已保留。</summary>
        Failed,
    }

    private static Outcome MigrateEntry(
        string name,
        string targetPath,
        IReadOnlyList<string> legacyDirectories,
        List<string> notes,
        bool asBinary = false)
    {
        // 幂等：程序目录已有同名文件就不动它，也不删除旧文件——到底以哪份为准交由用户决定，
        // 迁移只负责把「看不到的」搬成「看得到的」。
        if (PortableStorage.Exists(targetPath, out var probeError))
        {
            notes.Add("程序目录已有 " + name + "，跳过迁移。");
            return Outcome.Skipped;
        }

        if (probeError.Length > 0)
        {
            notes.Add(probeError + "（" + name + " 未迁移，旧文件保持不动）");
            return Outcome.Failed;
        }

        foreach (var directory in legacyDirectories)
        {
            var sourcePath = Path.Combine(directory, name);
            if (!PortableStorage.Exists(sourcePath, out _))
            {
                continue;
            }

            if (!Transfer(name, sourcePath, targetPath, asBinary, notes))
            {
                return Outcome.Failed;
            }

            if (!PortableStorage.TryDelete(sourcePath, "迁移后删除旧配置", out var deleteError))
            {
                // 迁移本身已成功，只是旧文件没删掉：如实记下，不当作失败。
                notes.Add("已迁移 " + name + "：" + sourcePath + " → " + targetPath + "；" + deleteError);
                return Outcome.Migrated;
            }

            notes.Add("已迁移 " + name + "：" + sourcePath + " → " + targetPath);
            return Outcome.Migrated;
        }

        return Outcome.Absent;
    }

    /// <summary>
    /// 把一份内容从旧位置搬到程序目录，并在搬完后**校验一致**——校验通过，调用方才允许删旧文件。
    /// <para>
    /// 文本与二进制走**两条**通道，不可互换：<c>settings.json</c> / <c>session.json</c> 是文本，
    /// 按文本搬运并比对字符串；启动图是二进制，必须按字节搬运并比对字节。
    /// </para>
    /// <para>
    /// <b>2026-09-26 实测缺陷（这条分支就是为此而拆）</b>：启动图曾与文本同走一条通道，
    /// 于是 JPEG 的 <c>FF D8</c> 被解码成替换字符、再编码为 <c>EF BF BD</c>——图片静默损坏、
    /// 体积还从 53789 B 涨到 95542 B；而「写后校验」两边同样按文本解码，反倒一致通过、拦不住。
    /// 二进制内容一律走 <see cref="PortableStorage.TryCopyVerified"/>。
    /// </para>
    /// </summary>
    private static bool Transfer(
        string name,
        string sourcePath,
        string targetPath,
        bool asBinary,
        List<string> notes)
    {
        if (asBinary)
        {
            if (!PortableStorage.TryCopyVerified(sourcePath, targetPath, out var copyError))
            {
                notes.Add(copyError + "（旧文件 " + sourcePath + " 保持不动）");
                return false;
            }

            return true;
        }

        if (!PortableStorage.TryReadText(sourcePath, out var content) || content is null)
        {
            notes.Add("读不到旧配置 " + sourcePath + "，未迁移（旧文件保持不动）。");
            return false;
        }

        if (!PortableStorage.TryWriteTextTo(targetPath, content, "迁移历史配置到程序目录", out var writeError))
        {
            notes.Add(writeError + "（旧文件 " + sourcePath + " 保持不动）");
            return false;
        }

        // 写后校验：内容一致才允许删旧文件。这一步是「先搬后删」的凭据。
        if (!PortableStorage.TryReadText(targetPath, out var verification)
            || !string.Equals(verification, content, StringComparison.Ordinal))
        {
            notes.Add("迁移 " + name + " 后校验不一致，已保留旧文件 " + sourcePath + "。");
            return false;
        }

        return true;
    }

    /// <summary>列出旧位置 <c>resources\</c> 里的全部启动图（按改名沿革由新到旧，同名以新者为准）。</summary>
    private static List<(string Source, string Name)> LegacyResourceFiles(
        IReadOnlyList<string> legacyDirectories,
        List<string> notes)
    {
        var found = new List<(string Source, string Name)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in legacyDirectories)
        {
            var folder = Path.Combine(directory, ResourceFolderName);
            if (!PortableStorage.TryEnumerateFiles(folder, out var listed, out var listError))
            {
                notes.Add(listError);
                continue;
            }

            foreach (var file in listed)
            {
                var name = Path.GetFileName(file);
                if (seen.Add(name))
                {
                    found.Add((file, name));
                }
                else
                {
                    notes.Add("旧位置存在同名启动图 " + file + "，以更新的位置为准，未迁移。");
                }
            }
        }

        return found;
    }

    private static bool TryPrepareResourceFolder(string targetDirectory, out string folder, List<string> notes)
    {
        folder = Path.Combine(targetDirectory, ResourceFolderName);
        if (PortableStorage.TryEnsureFolderAt(folder, out var error))
        {
            return true;
        }

        notes.Add(error);
        return false;
    }
}
