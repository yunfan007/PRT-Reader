namespace Prt.Installer.Install;

using System.IO;

/// <summary>
/// 安装器内嵌素材的读取。
///
/// build.ps1 把「阅读器产物 / 便携运行时 / 卸载器」放进 src\Prt.Installer\content\，
/// csproj 用 LogicalName 前缀 `prt.installer.content.` 把它们编进单文件 exe，
/// 于是发布口径始终只有一个 exe——分发的永远是「安装器自己」，不需要旁边跟着素材目录。
/// </summary>
public static class EmbeddedFiles
{
    private const string Prefix = "prt.installer.content.";

    // 下面的前缀一律用**反斜杠**收尾，不是点号：csproj 里 LogicalName 写作
    // `prt.installer.content.%(RecursiveDir)%(Filename)%(Extension)`，而 %(RecursiveDir) 给的是
    // 目录部分，在 Windows 上就是 `reader\` 这样。写成点号（`reader.`）时一个都匹配不上——
    // 编译期毫无反应，安装器却"释放了 0 个文件"并在下一步抛「内嵌素材缺失」。
    // runtime.zip 平铺在 content\ 根上，RecursiveDir 为空，故仍是点号结尾。

    /// <summary>阅读器产物（含 samples 与 帮助 等子目录）。</summary>
    public const string ReaderPrefix = Prefix + "reader\\";

    /// <summary>
    /// 便携 .NET 运行时：**内嵌压缩包** `runtime.zip`，不是散装文件树。
    /// 散装会让安装包凭空多出 160 MB；打成压缩包（约 50 MB）后安装时再解压，
    /// 既守住了"单文件"，也不必让用户下载一份两百多兆的安装包。
    /// </summary>
    public const string RuntimeArchive = Prefix + "runtime.zip";

    /// <summary>压缩包释放到安装目录后的文件名（释放完即删，不该留在安装目录里）。</summary>
    public const string RuntimeArchiveName = "runtime.zip";

    /// <summary>卸载器（框架依赖单文件）。</summary>
    public const string UninstallerPrefix = Prefix + "uninstaller\\";

    // 这里**没有**激活码生成器：安装包面向社会分发，而程序在"无码"时本就按 Free 级运行。
    // 包里再塞一个生成器，它唯一的新增能力就是签发付费码——等于把付费墙当附件一起发货；
    // 让它只签免费码又纯属仪式（用户本来就是 Free）。故签发端只留在 keys\ 与
    // publish\framework-dependent\密钥\，由俱乐部离线签发后把激活码发给用户粘贴。
    // build.ps1 另有一条硬门禁：安装包内出现 license.private.xml 直接构建失败。

    /// <summary>列出某个前缀下的全部素材相对路径。</summary>
    public static IReadOnlyList<string> List(string prefix)
    {
        var names = new List<string>();
        foreach (var name in typeof(EmbeddedFiles).Assembly.GetManifestResourceNames())
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                names.Add(RelativePathOf(name, prefix));
            }
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>把某个前缀下的素材全部释放到目标目录（目标目录须已存在）。</summary>
    public static void Extract(string prefix, string targetDirectory, Action<string>? onFile = null)
    {
        foreach (var entry in List(prefix))
        {
            ExtractOne(prefix, entry, targetDirectory);
            onFile?.Invoke(entry);
        }
    }

    /// <summary>把单个素材释放到目标目录下的指定文件名。</summary>
    public static void ExtractOne(string prefix, string entry, string targetDirectory)
    {
        // 素材缺失是打包流程的错，不是用户的错——直接抛，别让安装"静默少装一个文件"。
        using var source = OpenStream(prefix, entry)
                           ?? throw new InvalidOperationException("内嵌素材缺失：" + prefix + entry);
        var destination = Path.Combine(targetDirectory, entry);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        source.CopyTo(target, 1024 * 1024);
    }

    /// <summary>打开某个素材流（不存在时返回 null，便于调用方跳过可选素材）。</summary>
    private static Stream? OpenStream(string prefix, string entry)
    {
        var name = prefix + entry;
        var assembly = typeof(EmbeddedFiles).Assembly;
        return assembly.GetManifestResourceStream(name) ?? assembly.GetManifestResourceStream(name.ToLowerInvariant());
    }

    /// <summary>资源逻辑名 → 相对路径（逻辑名里的目录分隔符就是原生的 \ ）。</summary>
    private static string RelativePathOf(string resourceName, string prefix)
    {
        var rest = resourceName.Substring(prefix.Length);
        return rest.Replace('/', Path.DirectorySeparatorChar);
    }
}
