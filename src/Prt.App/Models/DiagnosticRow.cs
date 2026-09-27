using System.Windows.Media;
using Prt.App.Rendering;
using Prt.Core.Diagnostics;

namespace Prt.App.Models;

/// <summary>诊断面板中的一行（面向界面展示的只读视图模型）。</summary>
internal sealed class DiagnosticRow
{
    public DiagnosticRow(Diagnostic diagnostic)
    {
        Diagnostic = diagnostic;
        Location = $"{diagnostic.Line}:{diagnostic.Column}";
        Severity = diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => "错误",
            DiagnosticSeverity.Warning => "警告",
            _ => "提示",
        };
        Code = diagnostic.Code;
        Message = diagnostic.Message;
        Line = diagnostic.Line;
        Column = diagnostic.Column;
        Meta = $"{Severity} · {Code} · {Location}";
        SeverityBrush = ColorUtil.Brush(SeverityColor(diagnostic.Severity), Colors.Gray);
    }

    public Diagnostic Diagnostic { get; }

    /// <summary>`行:列` 文本。</summary>
    public string Location { get; }

    public string Severity { get; }

    public string Code { get; }

    public string Message { get; }

    /// <summary>「级别 · 代码 · 行列」单行摘要。</summary>
    public string Meta { get; }

    /// <summary>左侧色条颜色（跟随界面明暗）。</summary>
    public Brush SeverityBrush { get; }

    public int Line { get; }

    public int Column { get; }

    private static string SeverityColor(DiagnosticSeverity severity)
    {
        var dark = App.Palette.IsDark;
        return severity switch
        {
            DiagnosticSeverity.Error => dark ? "#ff99a4" : "#c42b1c",
            DiagnosticSeverity.Warning => dark ? "#fce100" : "#9d5d00",
            _ => dark ? "#60cdff" : "#0078d4",
        };
    }
}
