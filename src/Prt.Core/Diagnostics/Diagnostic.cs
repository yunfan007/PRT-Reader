namespace Prt.Core.Diagnostics;

/// <summary>诊断严重级别。对应《PRT 标准》14.4 节的「致命错误」与「警告」。</summary>
public enum DiagnosticSeverity
{
    /// <summary>提示信息，不影响输出。</summary>
    Info,

    /// <summary>宽松模式下的警告：该问题已按降级处理（标准 14.4）。</summary>
    Warning,

    /// <summary>致命错误：严格模式下应终止并报告位置（标准 14.4）。</summary>
    Error,
}

/// <summary>
/// 一条诊断信息。位置为源文本中的「行号:列号」，均从 1 起（标准 14.4）。
/// </summary>
public sealed class Diagnostic
{
    public Diagnostic(DiagnosticSeverity severity, string code, string message, int line, int column)
    {
        Severity = severity;
        Code = code;
        Message = message;
        Line = line;
        Column = column;
    }

    public DiagnosticSeverity Severity { get; }

    /// <summary>诊断代码，如 PRT0001；便于工具化处理与去重。</summary>
    public string Code { get; }

    /// <summary>人类可读的描述文本。</summary>
    public string Message { get; }

    /// <summary>行号（从 1 起）。</summary>
    public int Line { get; }

    /// <summary>列号（从 1 起）。</summary>
    public int Column { get; }

    /// <summary>标准 14.4 规定的警告/错误输出格式：`行号:列号 内容`。</summary>
    public string Format() => $"{Line}:{Column} {Message}";

    public override string ToString() => $"{Severity} {Code} {Format()}";
}

/// <summary>
/// 诊断集合。负责收集解析过程中的全部诊断，并在需要时按标准 14.4 节排序与去重：
/// 警告按源位置（行号，再列号）升序排列；同一源位置同一类问题合并为一次。
/// </summary>
public sealed class DiagnosticBag : IEnumerable<Diagnostic>
{
    private readonly List<Diagnostic> _items = new();

    public IReadOnlyList<Diagnostic> Items => _items;

    public bool HasErrors
    {
        get
        {
            foreach (var item in _items)
            {
                if (item.Severity == DiagnosticSeverity.Error)
                {
                    return true;
                }
            }
            return false;
        }
    }

    public int ErrorCount
    {
        get
        {
            var count = 0;
            foreach (var item in _items)
            {
                if (item.Severity == DiagnosticSeverity.Error)
                {
                    count++;
                }
            }
            return count;
        }
    }

    public int WarningCount
    {
        get
        {
            var count = 0;
            foreach (var item in _items)
            {
                if (item.Severity == DiagnosticSeverity.Warning)
                {
                    count++;
                }
            }
            return count;
        }
    }

    public void Add(DiagnosticSeverity severity, string code, string message, int line, int column)
        => _items.Add(new Diagnostic(severity, code, message, line, column));

    public void Info(string code, string message, int line, int column)
        => Add(DiagnosticSeverity.Info, code, message, line, column);

    public void Warn(string code, string message, int line, int column)
        => Add(DiagnosticSeverity.Warning, code, message, line, column);

    public void Error(string code, string message, int line, int column)
        => Add(DiagnosticSeverity.Error, code, message, line, column);

    /// <summary>
    /// 按标准 14.4 返回排序并去重后的诊断列表：先按行号、再按列号升序；
    /// 同一「行:列 + 代码 + 内容」只保留一次。
    /// </summary>
    public IReadOnlyList<Diagnostic> SortedAndDeduplicated()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<Diagnostic>();

        foreach (var item in _items.OrderBy(d => d.Line).ThenBy(d => d.Column))
        {
            var key = $"{item.Line}:{item.Column}|{item.Code}|{item.Message}";
            if (seen.Add(key))
            {
                result.Add(item);
            }
        }
        return result;
    }

    public IEnumerator<Diagnostic> GetEnumerator() => _items.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>本实现使用的诊断代码常量。</summary>
public static class PrtDiagnosticCodes
{
    public const string UnclosedFence = "PRT0001";
    public const string UnknownBlockType = "PRT0002";
    public const string UnknownParameter = "PRT0003";
    public const string InvalidColor = "PRT0004";
    public const string UnregisteredReference = "PRT0005";
    public const string TableMergeViolation = "PRT0006";
    public const string DuplicateAlias = "PRT0007";
    public const string MissingReferenceId = "PRT0008";
    public const string ComputationNotSupported = "PRT0009";
    public const string InvalidMetaEntry = "PRT0010";
    public const string FenceTypeMismatch = "PRT0011";
    public const string NestingViolation = "PRT0012";
    public const string InvalidEnumeration = "PRT0013";
    public const string TableStructure = "PRT0014";
    public const string UnknownDirective = "PRT0015";
    public const string Degradation = "PRT0016";
    public const string DuplicateId = "PRT0017";
    public const string InvalidAttribute = "PRT0018";
}
