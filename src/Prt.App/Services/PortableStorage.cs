using System.IO;

namespace Prt.App.Services;

/// <summary>
/// 便携落点：本程序**自己的**运行期数据（界面设置、会话记录、自定义启动图、异常日志、授权文件）
/// 一律落在<b>程序所在文件夹</b>，不再写用户配置目录（<c>%APPDATA%</c>）。
/// 同机多份副本因此互不可见，绿色部署整目录拷走即可。
/// <para>
/// <b>本类为什么不经模块四（SRT）申报</b>（PSS 4.4 / 4.6 的边界，别改回去）：
/// 4.6 的 63 项是为「程序触碰<b>用户或系统</b>的资源」而设——
/// 读侧只有 <c>PRIV-01</c>（读取文档库 / 图片库 / 音乐库）与 <c>PRIV-06</c>（读取其它程序的数据目录）；
/// 写侧 <c>FS-02</c> 限定为「用户目录」、<c>FS-03</c> 明确限定为「程序自身目录<b>以外</b>的地方」。
/// 本类只碰<b>程序自己在程序目录里写出的运行期数据</b>：既不是用户的文档库，也不是别的程序的数据，
/// 更不在程序目录之外——它不落在任何既有编号上。按 3.6「编号不得自造」与 3.8「等效不增强」，
/// 不该为它发明申报编号，因而它不在申报义务范围内（4.4 的申报义务只及于 63 项内的敏感动作）。
/// </para>
/// <para>
/// <b>为什么必须把这一类划出去</b>（实测教训，2026-09-26）：SRT 的裁决要经模块一，而模块一是在
/// <c>App.OnStartup</c> 里、界面建立之前才由 <c>SafetyBridge.Initialize</c> 注入的。启动期读设置
/// 这件事若走 SRT，只有两种下场：<b>①</b> 模块尚未注入时 <c>SafeRuntime</c> 会自建一份没有界面
/// 宿主的客户端，申报无人答复 → 一律 <c>Deny</c>：设置、会话、授权全都读不出来，旧配置迁移
/// 每一步都被拒（2026-09-26 的审计记录里，这几条申报清一色是 <c>deny / 用户拒绝或未决定</c>）；
/// <b>②</b> 把注入提前到一切读写之前，则每次启动都要用户先点一次授权窗，才肯读它自己的设置。
/// 两条都不可接受，故这类动作必须在 SRT 之外——见《设计取舍》第 13 条与《行为清单》第七节。
/// </para>
/// <para>
/// <b>不受本类影响的收口点</b>：<b>用户文档</b>的读写（打开 / 保存 / 导出 / 导入）仍一律经 SRT
/// （<c>FileService</c> → <c>SafeRuntime.File</c>，以 <c>PRIV-01</c> / <c>FS-02</c> / <c>FS-03</c> /
/// <c>FS-06</c> 申报）；剪贴板（<c>DEV-04</c> / <c>DEV-05</c>）与打印（<c>DEV-07</c>）同理。
/// </para>
/// <para>
/// <b>另一条边界（别把这条读掉了）</b>：本类只管本程序自己的运行期数据。安全模块的两处落点
/// <b>不</b>随本类迁移，它们各有规范依据：
/// </para>
/// <list type="bullet">
///   <item>模块一的审计记录 —— PSS 6.1.6（规范性）钉死在「当前用户的本地应用数据目录」，
///     且**不得**写入可被其它账户直接改写的位置；</item>
///   <item>模块三的宿主启动器存储与误报白名单 —— PSS 6.2.1（规范性）钉死在
///     <c>%LOCALAPPDATA%\Perisc\SafeGuard\</c>，并且明文要求目录权限「仅当前用户可写、可读；
///     **不得**放宽为全机可写」。移进程序目录，在程序目录可被他人写入的部署下正好违反该条。</item>
/// </list>
/// <para>取舍与理由见《设计取舍》第 13 条；用户可见的落点说明见《部署文档》与《行为清单》。</para>
/// </summary>
internal static class PortableStorage
{
    /// <summary>程序所在文件夹（可执行文件同级）。</summary>
    public static string Directory => AppContext.BaseDirectory;

    /// <summary>程序目录不可写时的统一提示（调用方按需展示给用户）。</summary>
    public const string NotWritableHint =
        "程序所在文件夹不可写。请把程序放到可写目录（或改用绿色免安装目录）后重试。";

    /// <summary>程序目录下的文件路径。</summary>
    public static string FilePath(string fileName) => Path.Combine(Directory, fileName);

    /// <summary>程序目录下的子目录路径。</summary>
    public static string FolderPath(string folderName) => Path.Combine(Directory, folderName);

    /// <summary>
    /// 把文本写进程序目录下的文件。失败时给出**可读原因**（不静默、也不改写别处）。
    /// <paramref name="reason"/> 保留调用点的自述意图（本类不再申报，故仅用于说明）。
    /// </summary>
    public static bool TryWriteText(string fileName, string content, string reason, out string error)
        => TryWriteTextTo(FilePath(fileName), content, reason, out error);

    /// <summary>把文本写到程序目录内的指定路径（含子目录里的文件）。失败时给出可读原因。</summary>
    public static bool TryWriteTextTo(string path, string content, string reason, out string error)
    {
        _ = reason;
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, content);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            error = NotWritableHint + "（" + ex.GetType().Name + "：" + ex.Message + "）";
            return false;
        }
    }

    /// <summary>把文本追加到程序目录下的文件（异常诊断日志）。失败时给出可读原因。</summary>
    public static bool TryAppendText(string fileName, string content, string reason, out string error)
    {
        _ = reason;
        try
        {
            File.AppendAllText(FilePath(fileName), content);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            error = NotWritableHint + "（" + ex.GetType().Name + "：" + ex.Message + "）";
            return false;
        }
    }

    /// <summary>把文件复制进程序目录（自定义启动图）。失败时给出可读原因。</summary>
    public static bool TryCopy(string sourcePath, string targetPath, out string error)
    {
        try
        {
            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            File.Copy(sourcePath, targetPath, overwrite: true);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            error = NotWritableHint + "（" + ex.GetType().Name + "：" + ex.Message + "）";
            return false;
        }
    }

    /// <summary>
    /// 复制文件并在复制后**按字节**校验一致。用于二进制内容（自定义启动图）。
    /// <para>
    /// <b>为什么必须有这条通道</b>（2026-09-26 实测缺陷）：<see cref="TryReadText"/> /
    /// <see cref="TryWriteTextTo"/> 走的是文本解码与编码，JPEG 这类二进制内容里无效的字节会被
    /// 换成替换字符（<c>FF D8</c> → <c>EF BF BD</c>），文件被写坏、体积还会变；
    /// 更糟的是「写后校验」两边同样按文本解码，于是**一致通过**、拦不住——用户的自定义启动图
    /// 就这么静默没了。二进制内容一律走本方法（或 <see cref="TryCopy"/> 后再自行校验）。
    /// </para>
    /// </summary>
    public static bool TryCopyVerified(string sourcePath, string targetPath, out string error)
    {
        if (!TryCopy(sourcePath, targetPath, out error))
        {
            return false;
        }

        if (FilesIdentical(sourcePath, targetPath, out var mismatch))
        {
            error = string.Empty;
            return true;
        }

        error = "复制 " + Path.GetFileName(sourcePath) + " 后校验不一致：" + mismatch;
        return false;
    }

    /// <summary>逐块比较两个文件是否字节一致；任一环节读不到都在 <paramref name="error"/> 中说明。</summary>
    private static bool FilesIdentical(string left, string right, out string error)
    {
        try
        {
            var leftLength = new FileInfo(left).Length;
            var rightLength = new FileInfo(right).Length;
            if (leftLength != rightLength)
            {
                error = "长度不同（" + leftLength + " ≠ " + rightLength + "）。";
                return false;
            }

            using var leftStream = File.OpenRead(left);
            using var rightStream = File.OpenRead(right);
            var leftBuffer = new byte[81920];
            var rightBuffer = new byte[81920];

            while (true)
            {
                var leftRead = leftStream.Read(leftBuffer, 0, leftBuffer.Length);
                var rightRead = rightStream.Read(rightBuffer, 0, rightBuffer.Length);
                if (leftRead != rightRead)
                {
                    error = "读取进度不同（" + leftRead + " ≠ " + rightRead + "）。";
                    return false;
                }

                if (leftRead == 0)
                {
                    error = string.Empty;
                    return true;
                }

                if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
                {
                    error = "内容不同（第 " + (leftStream.Position - leftRead) + " 字节起）。";
                    return false;
                }
            }
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            error = "比较文件时出错（" + ex.GetType().Name + "：" + ex.Message + "）。";
            return false;
        }
    }

    /// <summary>确保程序目录下的子目录存在；失败时给出可读原因。</summary>
    public static bool TryEnsureFolder(string folderName, out string folder, out string error)
    {
        folder = FolderPath(folderName);
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            error = NotWritableHint + "（" + ex.GetType().Name + "：" + ex.Message + "）";
            return false;
        }
    }

    /// <summary>
    /// 确保**指定路径**的目录存在。用于目标不在程序目录内的场合（如迁移时按参数给定的目录、
    /// 自检用的临时目录），故提示语保持中性，不套用「程序文件夹不可写」那句。
    /// </summary>
    public static bool TryEnsureFolderAt(string path, out string error)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            error = "无法创建目录 " + path + "（" + ex.GetType().Name + "：" + ex.Message + "）";
            return false;
        }
    }

    /// <summary>
    /// 枚举目录里的文件（非递归，仅本层）。目录**不存在**算作「没有文件」并返回成功
    /// ——迁移会逐个试几个历史目录，缺目录是常态而非故障；枚举本身失败才回报原因。
    /// </summary>
    public static bool TryEnumerateFiles(string directory, out string[] files, out string error)
    {
        try
        {
            files = System.IO.Directory.Exists(directory)
                ? System.IO.Directory.GetFiles(directory, "*")
                : [];
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            files = [];
            error = "列不出目录 " + directory + "（" + ex.GetType().Name + "：" + ex.Message + "）";
            return false;
        }
    }

    /// <summary>读取文件文本。<paramref name="text"/> 在失败时为 null，返回值表明是否读到。</summary>
    public static bool TryReadText(string path, out string? text)
    {
        try
        {
            if (!File.Exists(path))
            {
                text = null;
                return false;
            }

            text = File.ReadAllText(path);
            return true;
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            // 读不到按「没有」处理：调用方随即回退默认值，不会因为一次读取失败而改动任何文件。
            _ = ex;
            text = null;
            return false;
        }
    }

    /// <summary>
    /// 判定文件是否存在。判定本身问不出来时返回 false 并在 <paramref name="error"/> 中说明——
    /// 偏保守：把「问不出来」当作「没有」，调用方随即回退默认值，不会因此改动任何文件。
    /// </summary>
    public static bool Exists(string path, out string error)
    {
        try
        {
            error = string.Empty;
            return File.Exists(path);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            error = "无法确认文件是否存在：" + path + "（" + ex.GetType().Name + "）";
            return false;
        }
    }

    /// <summary>删除文件。<paramref name="error"/> 说明未删除的原因（文件本就不存在算作已删除）。</summary>
    public static bool TryDelete(string path, string reason, out string error)
    {
        _ = reason;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            error = "未能删除 " + Path.GetFileName(path) + "（" + ex.GetType().Name + "：" + ex.Message + "）";
            return false;
        }
    }

    /// <summary>
    /// 本类会遇到的失败类型集合：磁盘与权限类异常。筛掉进程级灾难（OOM 等），
    /// 让它们照常向上传播，不被当成「文件操作失败」吞掉。
    /// </summary>
    private static bool IsFileFailure(Exception ex)
        => ex is IOException or UnauthorizedAccessException or NotSupportedException
            or System.Security.SecurityException or ArgumentException or PathTooLongException;
}
