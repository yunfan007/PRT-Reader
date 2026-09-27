using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Prt.App.Services;

namespace Prt.App.Views;

/// <summary>
/// 启动界面：显示自定义图片（或内置画法）与程序版本号，主窗口就绪后自动淡出。
/// <para>
/// 最短显示 <see cref="MinimumVisibleMs"/> 毫秒——启动很快时也让人看清是什么程序，
/// 避免"闪一下"；随后 180 ms 淡出再关闭。
/// </para>
/// </summary>
public partial class SplashWindow : Window
{
    /// <summary>最短显示时长（毫秒），避免启动过快导致一闪而过。</summary>
    private const int MinimumVisibleMs = 800;

    private readonly Stopwatch _shown = Stopwatch.StartNew();

    private bool _dismissing;

    public SplashWindow()
    {
        InitializeComponent();

        VersionText.Text = Localizer.T("splash.version", AppVersion);
        ApplyCustomImage();
    }

    /// <summary>程序版本号（取程序集版本的前三段）。</summary>
    public static string AppVersion
        => typeof(SplashWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>
    /// 主窗口就绪后调用：等足最短时长再淡出并关闭。
    /// <para>
    /// 返回 <see cref="Task"/> 而不是 <c>async void</c>：本方法**不是**事件处理器，
    /// 按 9.6.4 / D-22 的锚点，非事件处理器的 <c>async void</c> 是扣分项——
    /// <c>async void</c> 无法被等待、异常也无处承接，是"调用方以为已经做完了"的经典来源。
    /// </para>
    /// </summary>
    public async Task DismissAfterMinimum()
    {
        if (_dismissing)
        {
            return;
        }

        _dismissing = true;

        try
        {
            var remaining = MinimumVisibleMs - (int)_shown.ElapsedMilliseconds;
            if (remaining > 0)
            {
                await Task.Delay(remaining);
            }

            var fade = new DoubleAnimation(1d, 0d, TimeSpan.FromMilliseconds(180));
            fade.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, fade);
        }
        catch
        {
            // 淡出失败也要保证窗口能关掉，绝不因为启动画面卡住启动流程。
            Close();
        }
    }

    /// <summary>
    /// 按设置取自定义启动图；未设置或读取失败时退回内置画法。
    /// <para>
    /// 这里不再做 <c>File.Exists</c> 探测：<see cref="SettingsStore.ResolveSplashImage"/> 内部
    /// 已经经 SRT 以 PRIV-01 申报过该路径的存在性（对象相同），再探一次等于同一行为申报两遍，
    /// 单次许可下会多弹一次窗；文件不存在时下面的 try/catch 同样会安静退回内置画法。
    /// </para>
    /// </summary>
    private void ApplyCustomImage()
    {
        var path = SettingsStore.ResolveSplashImage();
        if (path is null)
        {
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();

            SplashImage.Source = bitmap;
            SplashImage.Visibility = Visibility.Visible;
            BuiltinArt.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // 图片损坏：静默退回内置画法，不影响启动。
            SplashImage.Source = null;
            SplashImage.Visibility = Visibility.Collapsed;
            BuiltinArt.Visibility = Visibility.Visible;
        }
    }
}
