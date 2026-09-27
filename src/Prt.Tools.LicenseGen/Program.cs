using System.Runtime.InteropServices;

namespace Prt.Tools.LicenseGen;

/// <summary>
/// 离线激活码生成器入口：无参数启动图形界面；带参数走命令行模式（原用法不变）。
/// </summary>
internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool AttachConsole(int processId);

    private const int AttachParentProcess = -1;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
            return 0;
        }

        // 命令行模式：WinExe 默认没有控制台。stdout 未被重定向时挂接父终端以显示输出；
        // 已重定向时保持管道输出，否则 Console.Out 会绑到屏幕缓冲导致收不到结果。
        if (!Console.IsOutputRedirected)
        {
            AttachConsole(AttachParentProcess);
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "keygen" => CommandKeygen(args.Skip(1).ToArray()),
                "gen" => CommandGen(args.Skip(1).ToArray()),
                "verify" => CommandVerify(args.Skip(1).ToArray()),
                _ => PrintUsage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[错误] " + ex.Message);
            return 1;
        }
    }

    // ─────────────────────────────── keygen ───────────────────────────────

    /// <summary>
    /// 解析一个整型命令行参数（D-18）：解析失败交给调用方给出用法提示，不抛异常。
    /// 用不变文化显式解析——命令行参数不该受当前区域设置影响。
    /// </summary>
    private static bool TryParseArg(string text, out int value)
        => int.TryParse(text, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out value);

    private static int CommandKeygen(string[] args)
    {
        var directory = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        var force = args.Contains("--force", StringComparer.OrdinalIgnoreCase);
        var result = LicenseCore.Keygen(directory, force);

        Console.WriteLine("已生成密钥对：");
        Console.WriteLine("  私钥 " + result.PrivatePath + "（务必保密；丢失则无法再签发可被当前编辑器接受的激活码）");
        Console.WriteLine("  公钥 " + result.PublicPath + "（需内嵌到编辑器后重新编译，编辑器才会接受新签发的激活码）");
        return 0;
    }

    // ─────────────────────────────── gen ───────────────────────────────

    private static int CommandGen(string[] args)
    {
        string? name = null, email = null, level = null, keyPath = null, outFile = null;
        string? issuer = null, reason = null;
        int? days = null;
        var seats = 1;
        var perpetual = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "-n" or "--name" when i + 1 < args.Length:
                    name = args[++i];
                    break;
                case "-e" or "--email" when i + 1 < args.Length:
                    email = args[++i];
                    break;
                case "-l" or "--level" when i + 1 < args.Length:
                    level = args[++i];
                    break;
                case "-d" or "--days" when i + 1 < args.Length:
                    // D-18：命令行参数是可被误输入的外部输入，解析失败要给出用法提示并退出，
                    // 而不是抛 FormatException 让用户看到一段栈。
                    if (!TryParseArg(args[++i], out var parsedDays))
                    {
                        Console.Error.WriteLine("[错误] --days 需要一个整数，实际收到：" + args[i]);
                        return 1;
                    }
                    days = parsedDays;
                    break;
                case "--seats" when i + 1 < args.Length:
                    if (!TryParseArg(args[++i], out var parsedSeats))
                    {
                        Console.Error.WriteLine("[错误] --seats 需要一个整数，实际收到：" + args[i]);
                        return 1;
                    }
                    seats = parsedSeats;
                    break;
                case "-i" or "--issuer" when i + 1 < args.Length:
                    issuer = args[++i];
                    break;
                case "-r" or "--reason" when i + 1 < args.Length:
                    reason = args[++i];
                    break;
                case "--perpetual":
                    perpetual = true;
                    break;
                case "-k" or "--key" when i + 1 < args.Length:
                    keyPath = args[++i];
                    break;
                case "-o" or "--out" when i + 1 < args.Length:
                    outFile = args[++i];
                    break;
                default:
                    Console.Error.WriteLine("[错误] 无法识别的参数：" + args[i]);
                    return 1;
            }
        }

        var result = LicenseCore.Generate(
            name ?? string.Empty,
            email ?? string.Empty,
            level ?? string.Empty,
            days,
            perpetual,
            keyPath,
            issuer ?? string.Empty,
            seats,
            reason);
        var text = "激活码（复制整行发给用户）：" + Environment.NewLine
                   + result.Code + Environment.NewLine + Environment.NewLine
                   + "授权明细：" + result.Summary;

        if (outFile is not null)
        {
            File.WriteAllText(outFile, text, new System.Text.UTF8Encoding(false));
        }

        Console.WriteLine(text);
        return 0;
    }

    // ─────────────────────────────── verify ───────────────────────────────

    private static int CommandVerify(string[] args)
    {
        string? code = null, publicPath = null, outFile = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "-p" or "--public" && i + 1 < args.Length)
            {
                publicPath = args[++i];
            }
            else if (args[i] is "-o" or "--out" && i + 1 < args.Length)
            {
                outFile = args[++i];
            }
            else if (code is null)
            {
                code = args[i];
            }
        }

        var result = LicenseCore.Verify(code ?? string.Empty, publicPath);
        var text = (result.Ok ? "[通过] " : "[失败] ") + result.Detail;
        if (outFile is not null)
        {
            File.WriteAllText(outFile, text, new System.Text.UTF8Encoding(false));
        }

        Console.WriteLine(text);
        return result.Ok ? 0 : 1;
    }

    private static int PrintUsage()
    {
        Console.WriteLine("PRT 编辑器激活码生成器（离线 RSA-2048 签名）");
        Console.WriteLine();
        Console.WriteLine("用法：");
        Console.WriteLine("  LicenseGen                     启动图形界面（推荐）");
        Console.WriteLine("  LicenseGen keygen [目录] [--force]                        生成 RSA 密钥对");
        Console.WriteLine("  LicenseGen gen -n 用户名 -e 邮箱 -l 等级 [-d 天数] [--perpetual] [-k 私钥.xml]");
        Console.WriteLine("                              [-i 签发人] [--seats 台数] [-r 签发理由]");
        Console.WriteLine("                                                            签发激活码");
        Console.WriteLine("  LicenseGen verify <激活码> [-p 公钥.xml]                   校验激活码");
        Console.WriteLine();
        Console.WriteLine("等级：Free | Standard | Professional；-d 缺省（或不带 -d 加 --perpetual）为永久授权。");
        Console.WriteLine("签发人必填（用于追溯）；--seats 为可激活设备数，缺省 1；-r 签发理由可省略。");
        Console.WriteLine("注意：编辑器只认内嵌公钥对应的私钥签发的激活码；keygen 生成新密钥后需把新公钥");
        Console.WriteLine("      内嵌进编辑器并重新编译。");
        return 1;
    }
}
