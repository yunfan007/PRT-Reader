namespace Prt.Prta.Compiler;

/// <summary>操作数种类（第 6.5 节）。</summary>
internal enum OperandKind
{
    Register,    // r0–r15
    ParamSlot,   // p0–p7
    Named,       // 名变量（VAR 声明或形参名绑定）
    Immediate,   // 立即数
    PortRef,     // @name
    VarRef,      // $name
}

/// <summary>分类后的操作数。</summary>
internal readonly struct Operand
{
    public Operand(OperandKind kind, string text, string? detail = null)
    {
        Kind = kind;
        Text = text;
        Detail = detail;
    }

    /// <summary>种类。</summary>
    public OperandKind Kind { get; }

    /// <summary>原始书写（去除空白）。</summary>
    public string Text { get; }

    /// <summary>附加信息：寄存器/形参槽的下标、名变量名、端口/变量名、立即数归一化值。</summary>
    public string? Detail { get; }
}

/// <summary>一条已解析的指令。</summary>
/// <param name="Opcode">操作码（普通指令名，或白名单函数的大写操作码）。</param>
/// <param name="Line">块体内 1 起的行号。</param>
/// <param name="Operands">操作数。</param>
/// <param name="VarName">仅 VAR：声明的名变量名。</param>
/// <param name="VarType">仅 VAR：类型标注（可空）。</param>
/// <param name="VarInit">仅 VAR：初始化器立即数（可空）。</param>
/// <param name="FuncName">白名单函数指令：对应的白名单函数原名（小写）；普通指令为 null。</param>
internal sealed record ParsedInstruction(
    string Opcode,
    int Line,
    IReadOnlyList<Operand> Operands,
    string? VarName,        // 仅 VAR：声明的名变量名
    string? VarType,        // 仅 VAR：类型标注（可空）
    Operand? VarInit,       // 仅 VAR：初始化器立即数（可空）
    string? FuncName = null);// 白名单函数指令：函数原名（小写）

/// <summary>VAR 声明记录（按声明顺序）。</summary>
internal sealed record VarDecl(string Name, string? Type, Operand? Init, int Line);

/// <summary>签名解析结果（失败时抛 <see cref="PrtaCompileException"/>）。</summary>
internal static class PrtaParser
{
    public static readonly string[] ValidTypes =
        { "Null", "Boolean", "Integer", "Decimal", "String", "List", "Map", "Date" };

    public static readonly string[] ValidRels = { "==", "!=", "<", "<=", ">", ">=" };

    public static readonly string[] Opcodes =
    {
        "LOAD", "MOV", "ADD", "SUB", "MUL", "DIV", "MOD", "NEG", "AND", "OR", "NOT", "VAR",
        "IF", "ELSE", "ENDIF", "WHILE", "ENDWHILE", "BREAK", "CONTINUE",
        "CALL", "RETURN",
        "LIST_NEW", "LIST_LEN", "LIST_GET", "LIST_PUSH",
        "MAP_NEW", "MAP_GET", "MAP_SET", "MAP_KEYS", "MAP_VALUES",
        "PUSH", "POP", "EMIT", "LOADVAR", "LOADPORT", "HALT", "NOP",
    };

    private static readonly HashSet<string> OpcodeSet = new(Opcodes, StringComparer.Ordinal);

    public static bool IsOpcode(string token) => OpcodeSet.Contains(token);

    /// <summary>v5.0 起废止的指令：保留识别以便给出迁移指引。</summary>
    public const string RetiredSyscall = "SYSCALL";

    /// <summary>不安全白名单函数名（仅 <c>unsafe=true</c> 块可用）。</summary>
    public static readonly string[] UnsafeFunctionNames =
    {
        "fileRead", "fileWrite", "fileAppend", "fileExists", "fileList", "fileDelete",
        "httpGet", "httpPost", "clockNow", "randomInteger", "envGet",
    };

    /// <summary>
    /// 默认白名单函数名清单（上位标准附录 B 的常用集合，含不安全白名单）。
    /// <see cref="PrtaCompileOptions.Whitelist"/> 给出时以之为准。
    /// </summary>
    public static readonly string[] DefaultWhitelist =
    {
        "abs", "ceil", "floor", "round", "min", "max", "pow",
        "len", "upper", "lower", "trim", "substr", "replace", "split", "join",
        "contains", "startsWith", "endsWith", "format", "percent", "str", "num", "type",
        "listGet", "listPush", "mapGet", "mapSet", "mapKeys", "mapValues",
        "date", "dateFormat", "dateAdd", "dateDiff", "datePart", "dateIsLeap",
        "fileRead", "fileWrite", "fileAppend", "fileExists", "fileList", "fileDelete",
        "httpGet", "httpPost", "clockNow", "randomInteger", "envGet",
    };

    /// <summary>函数名 → 大写操作码（逐字符大写；与既有指令名冲突时加 <c>FN_</c> 前缀）。</summary>
    public static string FunctionOpcode(string functionName)
    {
        var upper = functionName.ToUpperInvariant();
        return OpcodeSet.Contains(upper) ? "FN_" + upper : upper;
    }

    /// <summary>构建「操作码 → 函数原名」映射表。</summary>
    public static IReadOnlyDictionary<string, string> BuildFunctionMap(IReadOnlySet<string>? whitelist)
    {
        var names = whitelist is null ? DefaultWhitelist : whitelist.ToArray();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            map[FunctionOpcode(name)] = name;
        }

        return map;
    }

    /// <summary>不安全白名单函数判定。</summary>
    public static bool IsUnsafeFunction(string functionName)
        => UnsafeFunctionNames.Contains(functionName, StringComparer.Ordinal);

    // ─────────────────────────────── 签名（第 11.2 节） ───────────────────────────────

    /// <summary>解析签名文本；返回 null 表示 sig 属性整体省略。</summary>
    public static PrtaSignature? ParseSignature(string? sigText, List<PrtaDiagnostic> diagnostics, string blockId)
    {
        if (sigText is null)
        {
            return null;
        }

        var text = sigText.Trim();
        if (text.Length == 0)
        {
            diagnostics.Add(new PrtaDiagnostic("E_SIGNATURE", blockId, 0, "sig 属性为空；要么整体省略，要么给出合法签名。"));
            return null;
        }

        // [identifier] "(" [paramList] ")" ["->" typeRef]
        string? identifier = null;
        var open = text.IndexOf('(', StringComparison.Ordinal);
        if (open < 0)
        {
            diagnostics.Add(new PrtaDiagnostic("E_SIGNATURE", blockId, 0, "签名缺少 \"(\"：" + sigText));
            return null;
        }

        var head = text[..open].Trim();
        if (head.Length > 0)
        {
            if (!IsIdentifier(head))
            {
                diagnostics.Add(new PrtaDiagnostic("E_SIGNATURE", blockId, 0, "签名标识不是合法标识符：" + head));
                return null;
            }

            identifier = head;
        }

        var close = text.IndexOf(')', open + 1);
        if (close < 0)
        {
            diagnostics.Add(new PrtaDiagnostic("E_SIGNATURE", blockId, 0, "签名缺少 \")\"：" + sigText));
            return null;
        }

        var tail = text[(close + 1)..].Trim();
        string? returnType = null;
        if (tail.Length > 0)
        {
            if (!tail.StartsWith("->", StringComparison.Ordinal))
            {
                diagnostics.Add(new PrtaDiagnostic("E_SIGNATURE", blockId, 0, "签名 \")\" 之后只允许 \"-> 类型\"：" + tail));
                return null;
            }

            returnType = tail[2..].Trim();
            if (!ValidTypes.Contains(returnType, StringComparer.Ordinal))
            {
                diagnostics.Add(new PrtaDiagnostic("E_SIGNATURE", blockId, 0, "返回类型非法（须为 8 种类型名之一，大小写敏感）：" + returnType));
                return null;
            }
        }

        var paramText = text[(open + 1)..close].Trim();
        var parameters = new List<PrtaParam>();
        if (paramText.Length > 0)
        {
            foreach (var raw in paramText.Split(','))
            {
                var part = raw.Trim();
                var colon = part.IndexOf(':', StringComparison.Ordinal);
                string paramName;
                string? paramType = null;
                if (colon >= 0)
                {
                    paramName = part[..colon].Trim();
                    paramType = part[(colon + 1)..].Trim();
                    if (!ValidTypes.Contains(paramType, StringComparer.Ordinal))
                    {
                        diagnostics.Add(new PrtaDiagnostic("E_SIGNATURE", blockId, 0, "形参类型非法：" + paramType));
                        continue;
                    }
                }
                else
                {
                    paramName = part;
                }

                if (!IsIdentifier(paramName))
                {
                    diagnostics.Add(new PrtaDiagnostic("E_SIGNATURE", blockId, 0, "形参名不是合法标识符：" + part));
                    continue;
                }

                if (paramName.StartsWith('r') && paramName.Length > 1 && paramName[1..].All(char.IsAsciiDigit) && paramName != "r")
                {
                    diagnostics.Add(new PrtaDiagnostic("E_SIGNATURE", blockId, 0, "形参名不得与寄存器名同名：" + paramName));
                    continue;
                }

                if (paramName.StartsWith('p') && paramName.Length > 1 && paramName[1..].All(char.IsAsciiDigit) && paramName != "p")
                {
                    diagnostics.Add(new PrtaDiagnostic("E_SIGNATURE", blockId, 0, "形参名不得与形参槽名同名：" + paramName));
                    continue;
                }

                parameters.Add(new PrtaParam(paramName, paramType));
            }

            if (parameters.Count > 8)
            {
                diagnostics.Add(new PrtaDiagnostic("E_SIGNATURE", blockId, 0, "形参个数超过上限 8。"));
            }
        }

        return new PrtaSignature(identifier, parameters, returnType);
    }

    // ─────────────────────────────── 块体词法 ───────────────────────────────

    /// <summary>把块体文本解析为指令序列（含 VAR 声明）。词法错误写入 diagnostics。</summary>
    public static List<ParsedInstruction> ParseBody(
        string body,
        string blockId,
        IReadOnlyDictionary<string, string> functions,
        List<PrtaDiagnostic> diagnostics)
    {
        var result = new List<ParsedInstruction>();
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = StripComment(lines[i]).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var lineNumber = i + 1;
            var space = IndexOfWhitespace(line);
            var opcode = space < 0 ? line : line[..space];
            var rest = space < 0 ? string.Empty : line[space..].Trim();

            // v5.0：白名单函数以「函数名大写即操作码」的指令形式调用（12.2）。
            var isFunction = false;
            string? functionName = null;
            if (OpcodeSet.Contains(opcode))
            {
                // 普通指令：优先于白名单操作码（既有指令名优先，12.2）。
            }
            else if (functions.TryGetValue(opcode, out var resolved))
            {
                isFunction = true;
                functionName = resolved;
            }
            else
            {
                var asFunctionOpcode = FunctionOpcode(opcode);
                diagnostics.Add(opcode == RetiredSyscall
                    ? new PrtaDiagnostic("E_LEX", blockId, lineNumber,
                        "SYSCALL 已在 PRTA v5.0 废止：白名单函数改为「函数名大写即操作码」，请改写为 FUNC 目的, 实参… 形式（例如 SYSCALL upper, r0, s → UPPER r0, s）。")
                    : functions.ContainsKey(asFunctionOpcode)
                        ? new PrtaDiagnostic("E_LEX", blockId, lineNumber,
                            "白名单函数名须大写后作操作码（v5.0）：" + opcode + " → " + asFunctionOpcode)
                        : new PrtaDiagnostic("E_LEX", blockId, lineNumber, "未知操作码：" + opcode));
                continue;
            }

            if (isFunction)
            {
                var operandsForFunction = SplitOperands(rest);
                var parsedFunction = new List<Operand>(operandsForFunction.Count);
                foreach (var raw in operandsForFunction)
                {
                    if (ClassifyOperand(raw) is { } operand)
                    {
                        parsedFunction.Add(operand);
                    }
                    else
                    {
                        diagnostics.Add(new PrtaDiagnostic("E_LEX", blockId, lineNumber, "无法识别的操作数：" + raw));
                    }
                }

                result.Add(new ParsedInstruction(opcode, lineNumber, parsedFunction, null, null, null, functionName));
                continue;
            }

            if (opcode == "VAR")
            {
                result.Add(ParseVar(rest, lineNumber, blockId, diagnostics));
                continue;
            }

            var operands = SplitOperands(rest);
            var parsed = new List<Operand>(operands.Count);
            foreach (var raw in operands)
            {
                if (ClassifyOperand(raw) is { } operand)
                {
                    parsed.Add(operand);
                }
                else
                {
                    diagnostics.Add(new PrtaDiagnostic("E_LEX", blockId, lineNumber, "无法识别的操作数：" + raw));
                }
            }

            result.Add(new ParsedInstruction(opcode, lineNumber, parsed, null, null, null));
        }

        return result;
    }

    /// <summary>去除注释：`;` 起始至行尾（字符串字面量内不视为注释，第 6.2 节）。</summary>
    public static string StripComment(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"' && (i == 0 || line[i - 1] != '\\'))
            {
                inString = !inString;
            }
            else if (c == ';' && !inString)
            {
                return line[..i];
            }
        }

        return line;
    }

    /// <summary>按顶层逗号拆分操作数（字符串内的逗号不拆分）。</summary>
    public static List<string> SplitOperands(string text)
    {
        var parts = new List<string>();
        if (text.Length == 0)
        {
            return parts;
        }

        var inString = false;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"' && (i == 0 || text[i - 1] != '\\'))
            {
                inString = !inString;
            }
            else if (c == ',' && !inString)
            {
                parts.Add(text[start..i].Trim());
                start = i + 1;
            }
        }

        parts.Add(text[start..].Trim());
        return parts;
    }

    /// <summary>操作数分类（第 6.5 节）；无法识别返回 null。</summary>
    public static Operand? ClassifyOperand(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        // 关系操作数（== / != / < / <= / > / >=）按立即数口径传递。
        if (ValidRels.Contains(text, StringComparer.Ordinal))
        {
            return new Operand(OperandKind.Immediate, text, text);
        }

        if (text[0] == '@')
        {
            var name = text[1..];
            return IsIdentifier(name) ? new Operand(OperandKind.PortRef, text, name) : null;
        }

        if (text[0] == '$')
        {
            var name = text[1..];
            return IsIdentifier(name) ? new Operand(OperandKind.VarRef, text, name) : null;
        }

        if (text[0] == '"' && text.Length >= 2 && text[^1] == '"')
        {
            return new Operand(OperandKind.Immediate, text, NormalizeString(text));
        }

        if (text is "true" or "false" or "null")
        {
            return new Operand(OperandKind.Immediate, text, text);
        }

        if (text[0] is '-' or (>= '0' and <= '9'))
        {
            return ClassifyNumber(text) is { } normalized
                ? new Operand(OperandKind.Immediate, text, normalized)
                : null;
        }

        if (IsRegister(text, out var rIndex))
        {
            return new Operand(OperandKind.Register, text, rIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (IsParamSlot(text, out var pIndex))
        {
            return new Operand(OperandKind.ParamSlot, text, pIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (IsIdentifier(text))
        {
            return new Operand(OperandKind.Named, text, text);
        }

        return null;
    }

    /// <summary>数值立即数：整数原样；定点数（1–2 位小数）归一化为两位小数字符串；非法返回 null。</summary>
    public static string? ClassifyNumber(string text)
    {
        var t = text;
        var negative = t.StartsWith('-');
        if (negative)
        {
            t = t[1..];
        }

        if (t.Length == 0)
        {
            return null;
        }

        var dot = t.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return t.All(char.IsAsciiDigit) ? text : null;
        }

        var intPart = t[..dot];
        var fracPart = t[(dot + 1)..];
        if (intPart.Length == 0 || !intPart.All(char.IsAsciiDigit))
        {
            return null;
        }

        if (fracPart.Length is < 1 or > 2 || !fracPart.All(char.IsAsciiDigit))
        {
            return null;
        }

        fracPart = fracPart.PadRight(2, '0');
        return (negative ? "-" : string.Empty) + intPart + "." + fracPart;
    }

    public static bool IsRegister(string text, out int index)
    {
        index = -1;
        if (text.Length < 2 || text[0] != 'r' || !text[1..].All(char.IsAsciiDigit))
        {
            return false;
        }

        // D-18：位数溢出（如 `r99999999999999`）会让 int.Parse 抛异常，而这里其实只需要
        // 回答"是不是 r0..r15"，**不是**就是答案本身，没有任何理由升级成异常。
        if (!int.TryParse(text.AsSpan(1), System.Globalization.NumberStyles.None,
                          System.Globalization.CultureInfo.InvariantCulture, out index))
        {
            index = -1;
            return false;
        }
        return index is >= 0 and <= 15;
    }

    public static bool IsParamSlot(string text, out int index)
    {
        index = -1;
        if (text.Length < 2 || text[0] != 'p' || !text[1..].All(char.IsAsciiDigit))
        {
            return false;
        }

        // 同上：不可解析即"不是参数槽写法"，不需要异常。
        if (!int.TryParse(text.AsSpan(1), System.Globalization.NumberStyles.None,
                          System.Globalization.CultureInfo.InvariantCulture, out index))
        {
            index = -1;
            return false;
        }
        return index is >= 0 and <= 7;
    }

    /// <summary>标识符：字母或下划线开头，可含字母、数字、下划线（第 6.3 节；CJK 视为字母）。</summary>
    public static bool IsIdentifier(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        if (!char.IsLetter(text[0]) && text[0] != '_')
        {
            return false;
        }

        for (var i = 1; i < text.Length; i++)
        {
            if (!char.IsLetterOrDigit(text[i]) && text[i] != '_')
            {
                return false;
            }
        }

        return true;
    }

    // ─────────────────────────────── 内部 ───────────────────────────────

    private static ParsedInstruction ParseVar(string rest, int line, string blockId, List<PrtaDiagnostic> diagnostics)
    {
        // VAR name[: Type][= imm]
        string name;
        string? type = null;
        Operand? init = null;

        var eq = IndexOfTopLevel(rest, '=');
        var valuePart = string.Empty;
        var declPart = rest;
        if (eq >= 0)
        {
            declPart = rest[..eq].TrimEnd();
            valuePart = rest[(eq + 1)..].Trim();
        }

        var colon = declPart.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            name = declPart[..colon].Trim();
            type = declPart[(colon + 1)..].Trim();
            if (!ValidTypes.Contains(type, StringComparer.Ordinal))
            {
                diagnostics.Add(new PrtaDiagnostic("E_LEX", blockId, line, "VAR 类型名非法（8 种类型之一，大小写敏感）：" + type));
                type = null;
            }
        }
        else
        {
            name = declPart.Trim();
        }

        if (!IsIdentifier(name))
        {
            diagnostics.Add(new PrtaDiagnostic("E_LEX", blockId, line, "VAR 名变量名不合法：" + name));
            name = string.Empty;
        }

        if (valuePart.Length > 0)
        {
            if (ClassifyOperand(valuePart) is { } operand && operand.Kind == OperandKind.Immediate)
            {
                init = operand;
            }
            else
            {
                diagnostics.Add(new PrtaDiagnostic("E_LEX", blockId, line, "VAR 初始化器只能是立即数：" + valuePart));
            }
        }

        return new ParsedInstruction("VAR", line, Array.Empty<Operand>(), name, type, init);
    }

    private static int IndexOfWhitespace(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static int IndexOfTopLevel(string text, char target)
    {
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"' && (i == 0 || text[i - 1] != '\\'))
            {
                inString = !inString;
            }
            else if (c == target && !inString)
            {
                return i;
            }
        }

        return -1;
    }

    private static string NormalizeString(string literal)
    {
        // 去除首尾引号后做 ECMAScript 双引号字面量转义。
        var inner = literal[1..^1];
        var sb = new System.Text.StringBuilder(inner.Length + 2);
        sb.Append('"');
        foreach (var c in inner)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }
}
