using System.IO;
using System.Text;
using Perisc.Safety;

namespace Prt.App.Services;

/// <summary>
/// 文档读写服务：统一的编码约定与磁盘访问入口。
/// <para>
/// 编码约定：PRT 源文件一律以 UTF-8 处理；读取时接受 UTF-8 BOM 并自动剥离，
/// 写入时不写 BOM（与主流 Markdown 工具保持一致，避免污染 diff）。
/// </para>
/// </summary>
internal static class FileService
{
    /// <summary>用于新建文档的默认文件名。</summary>
    public const string DefaultExtension = ".prt";

    /// <summary>文件对话框过滤器。</summary>
    public const string Filter =
        "PRT 文档 (*.prt)|*.prt|旧版 PSR 文档 (*.psr)|*.psr|Markdown 文档 (*.md)|*.md|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";

    /// <summary>
    /// 读取文本文件（按 UTF-8 解码，去除 BOM）。
    /// <para>
    /// 改走模块四 SRT（<see cref="SafeRuntime.File"/>）：调用 SRT 的函数即完成
    /// 「申报 → 裁决 → 执行 → 审计」四步（3.8），不存在"忘了申报"这一档；
    /// SRT 返回未执行（deny / 超时 / 模块不可用）时，本层**不执行**并向上抛出，
    /// 与 3.8 ③ 的「同失败语义」一致——不部分执行、不静默成功。
    /// </para>
    /// </summary>
    public static string ReadAllText(string path)
    {
        var result = SafeRuntime.File.ReadBytes(path, "读取 PRT 文档");
        if (!result.Executed)
        {
            throw new UnauthorizedAccessException(
                "安全模块未许可本次读取（" + result.Code + "）：" + result.Detail);
        }

        var bytes = result.Value ?? Array.Empty<byte>();
        using var stream = new MemoryStream(bytes);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// 写入文本文件（UTF-8 无 BOM）。同样经 SRT：先建目录，再写文件，两步各自申报。
    /// <para>
    /// 不再先探一次 <c>Directory.Exists</c>：那是一次未申报的文件系统访问（3.8），
    /// 而 <c>Directory.CreateDirectory</c> 对已存在的目录本来就是空操作——
    /// 直接申报「创建目录」（对象按前缀匹配，同目录的后续写入复用同一份许可）即可。
    /// </para>
    /// </summary>
    public static void WriteAllText(string path, string text)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            var created = SafeRuntime.File.CreateDirectory(directory, "创建文档所在目录");
            if (!created.Executed)
            {
                throw new UnauthorizedAccessException(
                    "安全模块未许可创建目录（" + created.Code + "）：" + created.Detail);
            }
        }

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
        var result = SafeRuntime.File.WriteBytes(path, bytes, "保存 PRT 文档");
        if (!result.Executed)
        {
            throw new UnauthorizedAccessException(
                "安全模块未许可本次写入（" + result.Code + "）：" + result.Detail);
        }
    }

    /// <summary>取适合作为标签页标题的文件名。</summary>
    public static string DisplayName(string? path)
        => string.IsNullOrWhiteSpace(path) ? "未命名" : Path.GetFileName(path);

    /// <summary>取文件所在目录（用于解析图片等相对资源）。</summary>
    public static string? DirectoryOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        return Path.GetDirectoryName(Path.GetFullPath(path));
    }
}
