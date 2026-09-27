using System.Text;

namespace Perisc.Safety.SafeGuard;

/// <summary>
/// 运行体完整性基准（程序目录下的 <c>Safe.data</c>，PSS 3.4 二 / 6.2）。
/// <para>
/// <b>为什么基准随包分发，而不是运行时在 <c>%LOCALAPPDATA%</c> 里"首次登记"</b>：
/// 登记在本机的那一份与包**不同源**——它记的是"上次在这台机器上跑过的运行体"，而不是
/// "这一批发布声明的运行体"。于是重新编译一次就得先手工清掉本机残留，否则启动器拿旧基准
/// 去核新运行体、一律 <c>HASH-MISMATCH</c> 拒启。基准改由构建生成、随包分发后，
/// 基准与同批运行体天然同源：安装、升级都不需要任何环境干预，也不再存在
/// "本机残留文件能左右校验结果"这条旁路（<c>Safe.data</c> 优先于本机登记值）。
/// </para>
/// <para>
/// <b>独占持有</b>：本类打开基准文件后一直持有句柄到进程退出，共享模式取
/// <see cref="FileShare.Read"/>——即允许别的进程再以只读打开（双击两次不会因争用而失败），
/// 但<b>写入与删除都要额外的共享许可，故一律失败</b>。这使"运行期间替换基准"不成立：
/// 想换基准就得先让启动器退出，而它一退出就不再是裁决者。
/// </para>
/// <para>
/// <b>文件格式</b>：纯文本，每行 <c>&lt;运行体文件名&gt; = sha256:&lt;64 位十六进制&gt;</c>；
/// 空行与 <c>#</c> 开头的注释行忽略。文件名按<b>不含路径</b>匹配（运行体就是启动器同目录下的
/// 那个 DLL）。由 <c>build.ps1</c> 在发布时生成，不接受手工编辑。
/// </para>
/// </summary>
internal sealed class GuardIntegrityBaseline : IDisposable
{
    /// <summary>基准文件名。非公告性常量：报告与文档都直接引用它。</summary>
    public const string FileName = "Safe.data";

    /// <summary>
    /// 基准文件缺失或损坏时的处置提示（随错误消息一起给出）。
    /// 只说用户能做的事，不复述内部判据。
    /// </summary>
    public const string Hint =
        FileName + " 随发布包分发，由 build.ps1 生成。若它缺失或被改动，请改用完整的发布包重新部署" +
        "（只替换 Prt.App.dll 会因缺少配套基准而被拒绝启动）。";

    private readonly FileStream _handle;
    private readonly Dictionary<string, string> _entries;

    private GuardIntegrityBaseline(FileStream handle, Dictionary<string, string> entries)
    {
        _handle = handle;
        _entries = entries;
    }

    /// <summary>基准文件的绝对路径（与宿主启动器同目录）。</summary>
    public static string Location => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>
    /// 打开并读取基准文件；此后<b>独占持有句柄</b>直到 <see cref="Dispose"/>（即进程退出）。
    /// <para>
    /// 失败时返回 <c>null</c> 并由 <paramref name="error"/> 说明原因——调用方据此**拒绝启动**：
    /// 拿不到基准就证明不了运行体完整，按 3.4 二 不得启动。
    /// </para>
    /// </summary>
    public static GuardIntegrityBaseline? Open(out string error)
    {
        var path = Location;

        FileStream handle;
        try
        {
            handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            error = "读不到 " + path + "（" + ex.GetType().Name + "：" + ex.Message + "）";
            return null;
        }

        try
        {
            var text = ReadAll(handle, out var readError);
            if (text is null)
            {
                error = readError;
                handle.Dispose();
                return null;
            }

            var entries = Parse(text, out var parseError);
            if (entries.Count == 0)
            {
                error = parseError;
                handle.Dispose();
                return null;
            }

            error = string.Empty;
            return new GuardIntegrityBaseline(handle, entries);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            // 只可能是读取途中的 I/O 故障；句柄一律不留给失败路径。
            error = "读取 " + path + " 失败（" + ex.GetType().Name + "：" + ex.Message + "）";
            handle.Dispose();
            return null;
        }
    }

    /// <summary>
    /// 取某个运行体的基准哈希；该运行体不在基准文件里时返回 <c>null</c>
    /// （同样是"没有基准"，由调用方按拒绝启动处置）。
    /// </summary>
    public string? Find(string programPath)
        => _entries.TryGetValue(Path.GetFileName(programPath), out var hash) ? hash : null;

    /// <summary>释放基准文件句柄（进程退出时）。释放之后本实例不可再用。</summary>
    public void Dispose() => _handle.Dispose();

    /// <summary>从已持有的句柄读全文（不另开文件句柄：那会与自己的独占句柄冲突）。</summary>
    private static string? ReadAll(FileStream handle, out string error)
    {
        try
        {
            var length = checked((int)handle.Length);
            if (length == 0)
            {
                error = "文件是空的（没有任何基准条目）";
                return null;
            }
            var buffer = new byte[length];
            handle.ReadExactly(buffer, 0, length);
            error = string.Empty;
            return Encoding.UTF8.GetString(buffer);
        }
        catch (Exception ex) when (IsFileFailure(ex) || ex is OverflowException)
        {
            error = "文件读不出或大小异常：" + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// 解析基准文件。任何一行不合格式都算失败（返回空表）——**不做"跳过坏行继续"的宽容**：
    /// 一份被悄悄改过的基准文件，应当表现为"校验拒绝"，而不是"少核了一项但照样启动"。
    /// </summary>
    private static Dictionary<string, string> Parse(string text, out string error)
    {
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            // 行首的 BOM 不是空白字符，Trim 去不掉；手工用其它编辑器另存过的文件常带它。
            var line = raw.TrimStart('\uFEFF').Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var separator = line.IndexOf('=');
            var name = separator > 0 ? line[..separator].Trim() : string.Empty;
            var hash = separator > 0 ? line[(separator + 1)..].Trim() : string.Empty;
            if (name.Length == 0 || hash.Length == 0)
            {
                error = "第 " + lineNumber + " 行不是「运行体文件名 = sha256:…」形式：" + line;
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            entries[name] = hash;
        }

        error = entries.Count == 0 ? "文件里没有任何基准条目" : string.Empty;
        return entries;
    }

    /// <summary>本类视作"文件层失败"的异常（其余异常照常上抛，不掩盖编程错误）。</summary>
    private static bool IsFileFailure(Exception ex)
        => ex is IOException or UnauthorizedAccessException or NotSupportedException
            or System.Security.SecurityException or ArgumentException or PathTooLongException;
}
