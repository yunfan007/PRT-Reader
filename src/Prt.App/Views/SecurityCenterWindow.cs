using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Perisc.Safety;
using Prt.App.Services;

namespace Prt.App.Views;

/// <summary>
/// 安全中心（4.3 ShowUi 的界面载体）：许可（查看 / 撤销）、审计（查看 / 导出 / 清空）、
/// 行为清单（3.6 全 63 项的本地展示）。窗口为非模态，许可变动时自动刷新。
/// </summary>
internal sealed class SecurityCenterWindow : Window
{
    private readonly SafetyClient _client;
    private readonly ObservableCollection<PermitRow> _permits = new();
    private readonly ObservableCollection<AuditRow> _audit = new();
    private ListView _permitList = null!;
    private ListView _auditList = null!;

    public SecurityCenterWindow(SafetyClient client, SafetyUiPage initialPage)
    {
        _client = client;
        Title = "安全中心 · PSS 安全模块";
        Width = 880;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var tabs = new TabControl();

        var auditTab = BuildAuditTab();
        var permitTab = BuildPermitTab();
        var catalogTab = BuildCatalogTab();

        tabs.Items.Add(new TabItem { Header = "审计记录", Content = auditTab });
        tabs.Items.Add(new TabItem { Header = "有效许可", Content = permitTab });
        tabs.Items.Add(new TabItem { Header = "行为清单", Content = catalogTab });
        tabs.SelectedIndex = initialPage switch
        {
            SafetyUiPage.Permits => 1,
            SafetyUiPage.ActionList => 2,
            _ => 0,
        };

        Content = tabs;
        RefreshPermits();
        RefreshAudit();

        client.PermitChanged += OnPermitChanged;
        Closed += (_, _) => client.PermitChanged -= OnPermitChanged;
    }

    private void OnPermitChanged(object? sender, PermitChangedEventArgs e)
    {
        Dispatcher.Invoke(RefreshPermits);
    }

    // ───────────────────────────── 许可页 ─────────────────────────────

    private sealed class PermitRow
    {
        public string RequestId { get; set; } = string.Empty;
        public string 行为 { get; set; } = string.Empty;
        public string 对象 { get; set; } = string.Empty;
        public string 匹配模式 { get; set; } = string.Empty;
        public string 剩余次数 { get; set; } = string.Empty;
        public string 到期时刻 { get; set; } = string.Empty;
    }

    private UIElement BuildPermitTab()
    {
        var root = new DockPanel();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
        var revoke = new Button { Content = "撤销选中", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
        revoke.Click += OnRevokeSelected;
        var revokeAll = new Button { Content = "撤销全部", Padding = new Thickness(10, 3, 10, 3) };
        revokeAll.Click += OnRevokeAll;
        var refresh = new Button { Content = "刷新", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0) };
        refresh.Click += (_, _) => RefreshPermits();
        buttons.Children.Add(revoke);
        buttons.Children.Add(revokeAll);
        buttons.Children.Add(refresh);
        DockPanel.SetDock(buttons, Dock.Top);

        _permitList = new ListView { ItemsSource = _permits, Margin = new Thickness(8) };
        _permitList.View = BuildColumns(
            ("RequestId", "RequestId", 260),
            ("行为", "行为", 90),
            ("对象", "对象", 260),
            ("匹配模式", "匹配模式", 90),
            ("剩余次数", "剩余次数", 70),
            ("到期时刻", "到期时刻", 150));

        root.Children.Add(buttons);
        root.Children.Add(_permitList);
        return root;
    }

    private void RefreshPermits()
    {
        _permits.Clear();
        foreach (var (requestId, scope) in _client.ListPermits())
        {
            _permits.Add(new PermitRow
            {
                RequestId = requestId,
                行为 = scope.Action,
                对象 = scope.Target,
                匹配模式 = scope.MatchMode.ToString(),
                // 界面展示给用户看的数字与时刻，显式跟随用户区域设置（与 Prt.Core 的
                // 编号层相反：后者写进导出文件，必须用不变文化；这里是给人看，保持本地化）。
                剩余次数 = scope.RemainingUses >= int.MaxValue / 2 ? "不限" : scope.RemainingUses.ToString(CultureInfo.CurrentCulture),
                到期时刻 = scope.ExpiresAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
            });
        }
    }

    private void OnRevokeSelected(object sender, RoutedEventArgs e)
    {
        if (_permitList.SelectedItem is PermitRow row)
        {
            _client.Revoke(row.RequestId);
            RefreshPermits();
        }
    }

    private void OnRevokeAll(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "确定撤销全部有效许可？此后所有敏感行为都需重新申报。",
                "PRT 阅读器 · 安全模块", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        _client.RevokeAllPermits();
        RefreshPermits();
    }

    // ───────────────────────────── 审计页 ─────────────────────────────

    private sealed class AuditRow
    {
        public string 时刻 { get; set; } = string.Empty;
        public string 层 { get; set; } = string.Empty;
        public string 行为 { get; set; } = string.Empty;
        public string 对象 { get; set; } = string.Empty;
        public string 裁决 { get; set; } = string.Empty;
        public string 码 { get; set; } = string.Empty;
        public string 详情 { get; set; } = string.Empty;
    }

    private UIElement BuildAuditTab()
    {
        var root = new DockPanel();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
        var refresh = new Button { Content = "刷新（最近 200 条）", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
        refresh.Click += (_, _) => RefreshAudit();
        var export = new Button { Content = "导出 JSON Lines…", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
        export.Click += OnExportAudit;
        var purge = new Button { Content = "清空全部审计…", Padding = new Thickness(10, 3, 10, 3) };
        purge.Click += OnPurgeAudit;
        buttons.Children.Add(refresh);
        buttons.Children.Add(export);
        buttons.Children.Add(purge);
        DockPanel.SetDock(buttons, Dock.Top);

        _auditList = new ListView { ItemsSource = _audit, Margin = new Thickness(8) };
        _auditList.View = BuildColumns(
            ("时刻", "时刻", 140),
            ("层", "层", 50),
            ("行为", "行为", 80),
            ("对象", "对象", 200),
            ("裁决", "裁决", 70),
            ("码", "码", 60),
            ("详情", "详情", 320));

        root.Children.Add(buttons);
        root.Children.Add(_auditList);
        return root;
    }

    private void RefreshAudit()
    {
        _audit.Clear();
        var records = _client.Query(new AuditQuery());
        foreach (var record in records.Skip(Math.Max(0, records.Count - 200)))
        {
            _audit.Add(new AuditRow
            {
                时刻 = record.Time.LocalDateTime.ToString("MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
                层 = record.Layer.ToString(),
                行为 = record.Action,
                对象 = record.Target,
                裁决 = record.Decision.ToString(),
                码 = record.Code.ToString(),
                详情 = record.Detail,
            });
        }
    }

    private void OnExportAudit(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出审计记录",
            Filter = "JSON Lines (*.jsonl)|*.jsonl",
            // 文件名会被路径处理与脚本解析，必须跨区域稳定（同日志与自检报告）。
            FileName = "prt-audit-" + AppClock.Now().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".jsonl",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        var result = _client.Export(new AuditExportOptions(dialog.FileName));
        MessageBox.Show(this,
            result.Ok ? "导出完成：" + result.Detail : "导出失败：" + result.Detail,
            "PRT 阅读器 · 安全模块", MessageBoxButton.OK,
            result.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void OnPurgeAudit(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this,
            "⚠️ 此操作将永久删除本程序的全部审计记录（不可恢复）。\n\n审计是安全模块的可追溯凭证，确定继续？",
            "PRT 阅读器 · 安全模块", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }
        var outcome = _client.Purge(new AuditQuery());
        RefreshAudit();
        MessageBox.Show(this, outcome.Detail, "PRT 阅读器 · 安全模块", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ───────────────────────────── 行为清单页 ─────────────────────────────

    private sealed class CatalogRow
    {
        public string 编号 { get; set; } = string.Empty;
        public string 类别 { get; set; } = string.Empty;
        public string 条目名称 { get; set; } = string.Empty;
        public string 默认裁决 { get; set; } = string.Empty;
    }

    private static UIElement BuildCatalogTab()
    {
        var root = new DockPanel();
        var header = new TextBlock
        {
            Text = "PSS 标准 3.6 敏感行为清单（9 类 63 项）：未申报的行为一律按默认拒绝处理。",
            Margin = new Thickness(8),
            TextWrapping = TextWrapping.Wrap,
        };
        DockPanel.SetDock(header, Dock.Top);

        var list = new ListView { Margin = new Thickness(8) };
        var rows = new ObservableCollection<CatalogRow>();
        foreach (var item in BehaviorCatalog.All)
        {
            rows.Add(new CatalogRow
            {
                编号 = item.Id,
                类别 = item.Category,
                条目名称 = item.Title,
                默认裁决 = item.DefaultDecision == DecisionStatus.Deny ? "拒绝（须申报）" : "询问",
            });
        }
        list.ItemsSource = rows;
        list.View = BuildColumns(
            ("编号", "编号", 90),
            ("类别", "类别", 70),
            ("条目名称", "条目名称", 420),
            ("默认裁决", "默认裁决", 110));

        root.Children.Add(header);
        root.Children.Add(list);
        return root;
    }

    // ───────────────────────────── 公共 ─────────────────────────────

    private static GridView BuildColumns(params (string Path, string Header, double Width)[] columns)
    {
        var view = new GridView();
        foreach (var (path, header, width) in columns)
        {
            view.Columns.Add(new GridViewColumn
            {
                Header = header,
                Width = width,
                DisplayMemberBinding = new System.Windows.Data.Binding(path),
            });
        }
        return view;
    }
}
