using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Perisc.Safety;

namespace Prt.App.Services;

/// <summary>
/// 可持久化的界面设置。
/// <para>
/// 字段一律用「可空 + 默认值」的宽松定义：配置文件缺失、字段缺失或值非法时都能安全回退，
/// 不会因为手工编辑坏了一行 JSON 就打不开程序。
/// </para>
/// </summary>
public sealed class AppSettings
{
    /// <summary>界面配色是否为深色。</summary>
    public bool InterfaceDark { get; set; }

    /// <summary>预览主题：<c>follow</c> / <c>default</c> / <c>dark</c> / <c>print</c> / <c>accessible</c>。</summary>
    public string PreviewTheme { get; set; } = PreviewThemeFollow;

    /// <summary>缩放（0.5–2.0）。</summary>
    public double Zoom { get; set; } = 1d;

    /// <summary>编辑器长行自动折行。</summary>
    public bool WordWrap { get; set; } = true;

    /// <summary>强制严格模式。</summary>
    public bool StrictMode { get; set; }

    /// <summary>默认显示方式：<c>editor</c> / <c>split</c> / <c>preview</c>。</summary>
    public string DefaultViewMode { get; set; } = ViewModeSplit;

    /// <summary>界面语言：<c>system</c> / <c>zh</c> / <c>en</c>。</summary>
    public string Language { get; set; } = Localizer.SystemDefault;

    /// <summary>启动时是否显示启动界面。</summary>
    public bool ShowSplash { get; set; } = true;

    /// <summary>自定义启动图（用户数据目录内的相对文件名）；null / 空表示用内置图。</summary>
    public string? SplashImage { get; set; }

    /// <summary>是否显示目录面板。</summary>
    public bool ShowOutline { get; set; } = true;

    /// <summary>是否显示诊断面板。</summary>
    public bool ShowProblems { get; set; }

    /// <summary>教程结业测试的历史最好成绩（百分制）；-1 表示还没考过。</summary>
    public int TutorialBestScore { get; set; } = -1;

    /// <summary>已完成练习的课序号（0 起）。</summary>
    public List<int> TutorialPracticeDone { get; set; } = [];

    public const string PreviewThemeFollow = "follow";
    public const string ViewModeEditor = "editor";
    public const string ViewModeSplit = "split";
    public const string ViewModePreview = "preview";

    /// <summary>复制一份（用于「打开设置前快照 → 取消时回滚」）。</summary>
    public AppSettings Clone() => new()
    {
        InterfaceDark = InterfaceDark,
        PreviewTheme = PreviewTheme,
        Zoom = Zoom,
        WordWrap = WordWrap,
        StrictMode = StrictMode,
        DefaultViewMode = DefaultViewMode,
        Language = Language,
        ShowSplash = ShowSplash,
        SplashImage = SplashImage,
        ShowOutline = ShowOutline,
        ShowProblems = ShowProblems,
        TutorialBestScore = TutorialBestScore,
        TutorialPracticeDone = [.. TutorialPracticeDone],
    };

    /// <summary>把越界的取值拉回合法范围；任何非法字符串都回退默认值。</summary>
    public void Normalize()
    {
        PreviewTheme = PreviewTheme is "default" or "dark" or "print" or "accessible" or PreviewThemeFollow
            ? PreviewTheme
            : PreviewThemeFollow;

        DefaultViewMode = DefaultViewMode is ViewModeEditor or ViewModePreview or ViewModeSplit
            ? DefaultViewMode
            : ViewModeSplit;

        Language = Language is Localizer.Chinese or Localizer.English or Localizer.SystemDefault
            ? Language
            : Localizer.SystemDefault;

        Zoom = Math.Clamp(double.IsFinite(Zoom) ? Zoom : 1d, 0.5d, 2d);

        if (string.IsNullOrWhiteSpace(SplashImage))
        {
            SplashImage = null;
        }

        // 教程进度：成绩越界一律视为「还没考」，课序号只保留合法范围。
        if (TutorialBestScore is < -1 or > 100)
        {
            TutorialBestScore = -1;
        }

        TutorialPracticeDone = TutorialPracticeDone is null
            ? []
            : [.. TutorialPracticeDone.Where(i => i >= 0).Distinct().Order()];
    }
}

/// <summary>
/// 设置存储：把 <see cref="AppSettings"/> 读写到程序所在文件夹下的 <c>settings.json</c>。
/// <para>
/// 位置策略＝<b>纯便携</b>：读写候选<b>只有</b>程序所在文件夹（见 <see cref="PortableStorage"/>），
/// 不再回退到 <c>%APPDATA%</c>。同机多份副本因此互不可见；程序目录不可写时按失败处理、
/// 由界面明确告知，<b>不</b>改写别处（《设计取舍》第 13 条）。
/// </para>
/// <para>
/// 旧位置（<c>%APPDATA%\PRT 工具</c> 等）的存量设置由
/// <see cref="LegacyConfigMigration"/> 在首次启动时搬过来，本类不参与。
/// </para>
/// </summary>
public static class SettingsStore
{
    private const string StorageFileName = "settings.json";

    /// <summary>用户数据目录下的子目录名（存放自定义启动图等资源）。</summary>
    private const string ResourceFolderName = "resources";

    /// <summary>设置的落点（程序所在文件夹内）。自检据此断言「纯便携」。</summary>
    public static string StoragePath => PortableStorage.FilePath(StorageFileName);

    /// <summary>当前设置（启动时 <see cref="Load"/> 之后即为生效值）。</summary>
    public static AppSettings Current { get; private set; } = new();

    /// <summary>用一份新设置替换当前值（调用方随后按需 <see cref="Save"/>）。</summary>
    public static void Update(AppSettings settings)
    {
        settings.Normalize();
        Current = settings;
    }

    /// <summary>从程序目录读取设置；文件缺失或损坏时保持默认值。</summary>
    public static void Load()
    {
        // 经模块四 SRT（3.8）：探测与读取各自完成「申报 → 裁决 → 执行 → 审计」。
        if (PortableStorage.TryReadText(StoragePath, out var json) && !string.IsNullOrEmpty(json))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    loaded.Normalize();
                    Current = loaded;
                    return;
                }
            }
            catch (Exception)
            {
                // 损坏的设置文件视同不存在，继续用默认设置运行——不因为手工编辑坏了一行 JSON 就打不开程序。
                // 刻意不区分「损坏」与「不存在」：两者的处置相同，都是退回默认值；
                // 且此处没有可用的报错通道（Load 在 OnStartup 最前面，消息框尚未到可用时机）。
            }
        }

        Current.Normalize();
    }

    /// <summary>
    /// 写回程序目录。失败时给出**可读原因**（程序目录不可写、未获许可…）；
    /// 调用方负责把原因展示给用户——本程序不写用户配置目录，故没有「换个地方写」这个退路。
    /// </summary>
    public static bool TrySave(out string error)
    {
        Current.Normalize();
        var json = JsonSerializer.Serialize(Current, JsonOptions);
        return PortableStorage.TryWriteText(StorageFileName, json, "保存界面设置", out error);
    }

    /// <summary>把外部图片复制进程序目录的 <c>resources\</c> 作为启动图，并记入设置。</summary>
    public static bool TrySetSplashImage(string sourcePath, out string error)
    {
        error = string.Empty;
        try
        {
            var found = SafeRuntime.File.Exists(sourcePath, "检查启动图源文件");
            if (!found.Executed || found.Value != true)
            {
                error = "找不到该图片文件。";
                return false;
            }

            var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp"))
            {
                error = "仅支持 PNG / JPG / BMP 格式。";
                return false;
            }

            if (!PortableStorage.TryEnsureFolder(ResourceFolderName, out var folder, out error))
            {
                return false;
            }

            // 统一改名，避免覆盖同名旧文件；旧文件随后清掉，避免目录里越堆越多。
            var fileName = "splash" + extension;
            var target = Path.Combine(folder, fileName);
            var copied = SafeRuntime.File.Copy(sourcePath, target, overwrite: true, reason: "设置为启动图");
            if (!copied.Executed)
            {
                error = "安全模块未许可写入启动图（" + copied.Code + "）：" + copied.Detail;
                return false;
            }

            if (Current.SplashImage is { Length: > 0 } previous && !string.Equals(previous, fileName, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(Path.Combine(folder, previous));
            }

            Current.SplashImage = fileName;
            return TrySave(out error);
        }
        catch (Exception ex)
        {
            // 捕获后转为可读原因交给调用方展示（不是吞掉：调用方会弹提示，详见调用点）。
            // 这里兜的是路径形态异常一类（来源文件路径由用户经对话框给出，不可假定其合法）。
            error = ex.Message;
            return false;
        }
    }

    /// <summary>清掉自定义启动图，回到内置图。</summary>
    public static bool ResetSplashImage(out string error)
    {
        if (Current.SplashImage is { Length: > 0 } name)
        {
            TryDelete(Path.Combine(ResourceDirectory, name));
        }

        Current.SplashImage = null;
        return TrySave(out error);
    }

    /// <summary>自定义启动图的绝对路径；未设置或文件已丢失时返回 null（调用方回退内置图）。</summary>
    public static string? ResolveSplashImage()
    {
        if (Current.SplashImage is not { Length: > 0 } name)
        {
            return null;
        }

        var candidate = Path.Combine(ResourceDirectory, name);
        return PortableStorage.Exists(candidate, out _) ? candidate : null;
    }

    /// <summary>自定义启动图所在目录（程序目录下的 <c>resources\</c>）。</summary>
    private static string ResourceDirectory => PortableStorage.FolderPath(ResourceFolderName);

    private static void TryDelete(string path) =>
        // 删不掉也无妨：下次写入会覆盖同名文件。故刻意不检查结果、不打断调用方。
        PortableStorage.TryDelete(path, "删除旧资源文件", out _);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
