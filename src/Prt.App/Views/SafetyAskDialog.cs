using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Perisc.Safety;

namespace Prt.App.Views;

/// <summary>
/// 授权弹窗（3.4 ask 语义的界面载体）：
/// 每个待决申报一张卡片（行为 / 对象 / 理由 + 三选一），
/// 批量申报一次交互完成（4.3.1）。默认「拒绝」；未决定也按拒绝处理。
/// </summary>
internal sealed class SafetyAskDialog : Window
{
    private sealed class Item
    {
        public AskRequest Request = null!;
        public ComboBox Choice = null!;
    }

    private readonly List<Item> _items = new();

    public SafetyAskDialog(IReadOnlyList<AskRequest> requests)
    {
        Title = "安全模块 · 请求授权";
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        MinWidth = 520;
        MaxWidth = 720;

        var root = new StackPanel { Margin = new Thickness(16) };

        root.Children.Add(new TextBlock
        {
            Text = "本程序请求执行以下敏感行为，请逐项决定：",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
            FontWeight = FontWeights.SemiBold,
        });

        foreach (var request in requests)
        {
            _items.Add(new Item { Request = request, Choice = BuildCard(root, request) });
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var denyAll = new Button { Content = "全部拒绝", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        denyAll.Click += (_, _) => { DialogResult = false; };
        var ok = new Button { Content = "确定", Padding = new Thickness(20, 4, 20, 4), IsDefault = true };
        ok.Click += (_, _) => { DialogResult = true; };
        buttons.Children.Add(denyAll);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);

        Content = new ScrollViewer
        {
            MaxHeight = 520,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root,
        };
    }

    /// <summary>
    /// 收集界面决定（PSS 6.1.3：许可的"单次"由 <c>MaxUses</c> 定义）：
    /// 「允许一次」= 单次许可（MaxUses 为 null 即首次 Allow 后用尽）；
    /// 「本次会话内允许」= 显式给出用量与时长（12.2 上限：24 小时 / 1000 次）。
    /// </summary>
    public IReadOnlyList<SafetyUiHost.Answer?> CollectAnswers() =>
        _items.Select(item => item.Choice.SelectedIndex switch
        {
            1 => new SafetyUiHost.Answer(allowed: true),
            2 => new SafetyUiHost.Answer(allowed: true, ttl: TimeSpan.FromHours(24), maxUses: 1000),
            _ => null,
        }).ToList();

    private static ComboBox BuildCard(StackPanel root, AskRequest request)
    {
        var card = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 10),
        };
        var stack = new StackPanel();

        var catalog = BehaviorCatalog.Find(request.Action);
        stack.Children.Add(new TextBlock
        {
            Text = catalog is null ? request.Action : $"{request.Action}　{catalog.Title}",
            FontWeight = FontWeights.SemiBold,
        });
        stack.Children.Add(new TextBlock { Text = "对象：" + request.Target, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        stack.Children.Add(new TextBlock { Text = "理由：" + request.Reason, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });

        var choice = new ComboBox { Margin = new Thickness(0, 8, 0, 0), MinWidth = 200 };
        choice.Items.Add("拒绝（默认）");
        choice.Items.Add("允许一次");
        choice.Items.Add("本次会话内允许");
        choice.SelectedIndex = 0;
        stack.Children.Add(choice);

        card.Child = stack;
        root.Children.Add(card);
        return choice;
    }
}
