using System.Windows;
using System.Windows.Media.Imaging;

namespace Perisc.Safety.SafeGuard;

/// <summary>
/// 「安全系统启动中」启动界面（双击启动时显示，生命周期由 <see cref="GuardSplash"/> 掌握）。
/// <para>
/// 本窗口只承担观感：不显示假进度、不拦截操作、不参与任何裁决。
/// 它在或不在，运行体哈希校验、作业对象、管道监听与终止规则都照原样执行——
/// 这一点在《行为清单》里有明文，避免被读成"关掉启动界面就等于关掉守护"。
/// </para>
/// </summary>
internal sealed partial class GuardSplashWindow : Window
{
    /// <summary>品牌标的嵌入资源名（与 csproj 的 LogicalName 一致）。</summary>
    private const string BrandMarkResource = "Perisc.Safety.SafeGuard.Assets.app.ico";

    /// <param name="stage">初始阶段文字；随后由 <see cref="SetStage"/> 推进。</param>
    public GuardSplashWindow(string stage)
    {
        InitializeComponent();
        StageText.Text = stage;
        BrandMark.Source = LoadBrandMark();
    }

    /// <summary>推进阶段文字（只在启动界面的调度线程上调用）。</summary>
    public void SetStage(string stage) => StageText.Text = stage;

    /// <summary>
    /// 取品牌标位图：与 exe 图标、主程序窗口图标同一个源文件（Assets/app.ico）。
    /// <para>
    /// 从 ico 的多档帧中取<b>像素最大</b>的一帧：窗口按 72 逻辑像素显示，
    /// 在 200% 缩放下需要 144 物理像素，只有 256 那一帧够用；
    /// 不指定尺寸地解码 ico 会拿到最小的帧，放大后糊成一团。
    /// </para>
    /// </summary>
    private static BitmapFrame? LoadBrandMark()
    {
        try
        {
            using var stream = typeof(GuardSplashWindow).Assembly.GetManifestResourceStream(BrandMarkResource);
            if (stream is null)
            {
                return null;
            }
            var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames.OrderByDescending(candidate => candidate.PixelWidth).FirstOrDefault();
        }
        catch
        {
            // 吞掉的是"品牌标取不到"（嵌入资源缺失 / ico 解码失败）；降级到"启动界面少一张图"。
            // 何时应传播：不需要——缺一张图既不是启动失败，也没有比"少一张图"更轻的处置。
            return null;
        }
    }
}
