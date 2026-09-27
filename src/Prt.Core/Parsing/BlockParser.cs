using System.Text;
using Prt.Core.Diagnostics;
using Prt.Core.Structure;
using Prt.Core.Syntax;
using Prt.Core.Text;

namespace Prt.Core.Parsing;

/// <summary>
/// 块级解析器，实现《PRT 标准》第 8 章的围栏语法（栈式 LIFO 嵌套）、
/// 基础块（标题/段落/列表/引用/代码/分隔线/管道表）、
/// 语义块（8.3）、布局块（8.4）、结构块（8.5 / 第 9–10 章）与计算块（8.6）的降级捕获。
/// </summary>
internal sealed class BlockParser
{
    private readonly SourceText _source;
    private readonly PrtOptions _options;
    private readonly DiagnosticBag _diagnostics;
    private readonly int _lineOffset;
    private readonly int _columnOffset;

    /// <summary>标签分隔符（提成字段：每次解析都新建数组会被 CA1861 记为热路径分配）。</summary>
    private static readonly char[] TagSeparators = { ',', '，', ';', '；' };

    /// <summary>glossary 条目分隔符（同上）。</summary>
    private static readonly char[] GlossarySeparators = { ';', '；' };

    private int _index;

    public BlockParser(
        SourceText source,
        PrtOptions options,
        DiagnosticBag diagnostics,
        int lineOffset = 0,
        int columnOffset = 0)
    {
        _source = source;
        _options = options;
        _diagnostics = diagnostics;
        _lineOffset = lineOffset;
        _columnOffset = columnOffset;
    }

    private int LineCount => _source.LineCount;

    private int AbsLine(int localIndex) => localIndex + 1 + _lineOffset;

    private int AbsColumn(int localColumn) => localColumn + _columnOffset;

    private void Report(string code, string message, int localLineIndex, int column = 1)
        => ParsingHelpers.Report(_diagnostics, _options.Strict, code, message, AbsLine(localLineIndex), AbsColumn(column));

    /// <summary>解析整篇文档。</summary>
    public DocumentBlock ParseDocument()
    {
        var document = new DocumentBlock { Line = 1, Column = 1 };
        document.Children.AddRange(ParseBlocks(null, 0, -1));
        return document;
    }

    /// <summary>
    /// 解析一个块序列。<paramref name="endType"/> 为 null 表示顶层；
    /// 非 null 时遇到匹配的结束围栏返回。围栏按 LIFO 闭合最近者（标准 8.7）。
    /// </summary>
    private List<PrtBlock> ParseBlocks(string? endType, int depth, int openerLocalIndex)
    {
        var blocks = new List<PrtBlock>();
        var closed = false;

        if (depth > _options.MaxFenceDepth)
        {
            Report(PrtDiagnosticCodes.NestingViolation, "围栏嵌套深度超出上限，已停止解析该层级", openerLocalIndex < 0 ? _index : openerLocalIndex);
            return blocks;
        }

        while (_index < LineCount)
        {
            var line = _source.Lines[_index];
            var fence = ParsingHelpers.ParseFenceLine(line.Text);

            if (fence.IsFence && fence.IsEnd)
            {
                if (endType is null)
                {
                    Report(PrtDiagnosticCodes.UnclosedFence, "多余的结束围栏：未找到与之匹配的起始围栏", _index);
                    _index++;
                    continue;
                }
                if (fence.TypeName is not null
                    && !string.Equals(fence.TypeName, endType, StringComparison.OrdinalIgnoreCase))
                {
                    Report(
                        PrtDiagnosticCodes.FenceTypeMismatch,
                        $"结束围栏类型「{fence.TypeName}」与当前未闭合的围栏类型「{endType}」不一致",
                        _index);
                }
                _index++;
                closed = true;
                break;
            }

            ParseOneBlock(blocks, depth);
        }

        if (endType is not null && !closed)
        {
            Report(
                PrtDiagnosticCodes.UnclosedFence,
                $"未闭合的围栏：`::: {endType}` 缺少对应的结束围栏",
                openerLocalIndex < 0 ? 0 : openerLocalIndex);
        }

        return blocks;
    }

    private void ParseOneBlock(List<PrtBlock> blocks, int depth)
    {
        var line = _source.Lines[_index];
        var text = line.Text;

        if (line.IsBlank)
        {
            _index++;
            return;
        }

        var trimStart = text.TrimStart();

        // 行注释（标准 6.5）：行首（忽略前导空白）以 `//` 开头，整行不进入渲染。
        if (trimStart.StartsWith("//", StringComparison.Ordinal))
        {
            _index++;
            return;
        }

        // 代码围栏（标准 8.2）：三个反引号或三个波浪。
        if (IsCodeFence(text))
        {
            blocks.Add(ParseCodeFence());
            return;
        }

        // 围栏块（标准 8.1）。
        var fence = ParsingHelpers.ParseFenceLine(text);
        if (fence.IsFence && !fence.IsEnd)
        {
            blocks.Add(ParseFenceBlock(fence, depth));
            return;
        }

        // 基础块：标题。
        if (TryParseHeading(text, out var level, out var headingContent))
        {
            var heading = new HeadingBlock
            {
                Level = level,
                Line = AbsLine(_index),
                Column = AbsColumn(1),
                PlainText = InlineParser.ToPlainText(
                    InlineParser.Parse(headingContent, AbsLine(_index), AbsColumn(1), _options, _diagnostics)),
            };
            heading.Inlines.AddRange(
                InlineParser.Parse(headingContent, AbsLine(_index), AbsColumn(1), _options, _diagnostics));
            blocks.Add(heading);
            _index++;
            return;
        }

        // 基础块：分隔线。
        if (IsThematicBreak(text))
        {
            blocks.Add(new ThematicBreakBlock { Line = AbsLine(_index), Column = AbsColumn(1) });
            _index++;
            return;
        }

        // 基础块：引用。
        if (IsQuoteLine(text))
        {
            blocks.Add(ParseQuote(depth));
            return;
        }

        // 基础块：列表（含任务清单）。
        if (TryGetListInfo(text, out _, out _, out _, out _))
        {
            blocks.Add(ParseList(depth));
            return;
        }

        // 基础块：管道表（当前行含竖线，且下一行为分隔行）。
        if (TableParser.LooksLikeTableRow(text)
            && _index + 1 < LineCount
            && ParsingHelpers.IsTableSeparatorRow(_source.Lines[_index + 1].Text))
        {
            blocks.Add(ParsePipeTable());
            return;
        }

        // 其余：段落。
        blocks.Add(ParseParagraph());
    }

    // ────────────────────────────── 围栏块分发 ──────────────────────────────

    private PrtBlock ParseFenceBlock(ParsingHelpers.FenceLine fence, int depth)
    {
        var typeName = fence.TypeName ?? string.Empty;
        var openerIndex = _index;
        var line = AbsLine(openerIndex);
        var column = AbsColumn(1);
        var parts = ParsingHelpers.ParseHeader(fence.Header);

        // `::: set 名称 = 表达式`：同一物理行写完时行尾自动闭合、单行自终止（标准 11.1）。
        // 【COMP 不支持】仅捕获源码，不求值。
        if (typeName == "set" && fence.Header.Length > 0)
        {
            _index++;
            return new ComputationBlock
            {
                Kind = ComputationKind.Set,
                Header = fence.Header,
                Source = _source.Lines[openerIndex].Text,
                Line = line,
                Column = column,
            };
        }

        if (ParsingHelpers.TryParseSemanticKind(typeName, out var kind))
        {
            var block = new SemanticBlock
            {
                Kind = kind,
                // 标题既可用自由文本（`::: note 自定义标题`），也可用 title= 参数。
                Title = parts.FreeText.Length > 0 ? parts.FreeText : GetParam(parts, "title"),
                Line = line,
                Column = column,
            };
            _index++;
            block.Children.AddRange(ParseBlocks(typeName, depth + 1, openerIndex));
            return block;
        }

        switch (typeName)
        {
            case "meta":
                return ParseMetaBlock(openerIndex, line, column);

            case "refs":
                return ParseRefsBlock(openerIndex, line, column);

            case "theme":
                return ParseThemeBlock(parts, openerIndex, line, column);

            case "summary":
            {
                var block = new SummaryBlock { Line = line, Column = column };
                _index++;
                block.Children.AddRange(ParseBlocks(typeName, depth + 1, openerIndex));
                return block;
            }

            case "section":
                return ParseSectionBlock(parts, openerIndex, line, column, depth);

            case "column":
            {
                var block = new ColumnBlock { Line = line, Column = column };
                _index++;
                block.Children.AddRange(ParseBlocks(typeName, depth + 1, openerIndex));
                return block;
            }

            case "tab":
            {
                var block = new TabBlock
                {
                    Name = GetParam(parts, "name"),
                    Line = line,
                    Column = column,
                };
                _index++;
                block.Children.AddRange(ParseBlocks(typeName, depth + 1, openerIndex));
                return block;
            }

            case "columns":
                return ParseColumnsBlock(parts, openerIndex, line, column, depth);

            case "tabs":
                return ParseTabsBlock(parts, openerIndex, line, column, depth);

            case "collapse":
            {
                var block = new LayoutBlock
                {
                    Kind = LayoutKind.Collapse,
                    Title = GetParam(parts, "title"),
                    DefaultOpen = GetBoolParam(parts, "default-open"),
                    Line = line,
                    Column = column,
                };
                _index++;
                block.Children.AddRange(ParseBlocks(typeName, depth + 1, openerIndex));
                return block;
            }

            case "text":
                return ParseTextStyleBlock(parts, openerIndex, line, column, depth);

            case "align":
                return ParseAlignBlock(parts, openerIndex, line, column, depth);

            case "table":
                return ParseTableBlock(parts, openerIndex, line, column);

            case "figure":
                return ParseFigureBlock(parts, openerIndex, line, column, depth);

            case "equation":
                return ParseEquationBlock(parts, openerIndex, line, column);

            case "code":
            {
                var (body, _) = CaptureStructuredBody(openerIndex, "code", ignoreBareEnd: false);
                return new CodeBlock
                {
                    Language = GetParam(parts, "lang") ?? GetParam(parts, "language"),
                    Code = body,
                    Line = line,
                    Column = column,
                };
            }

            // 计算块（COMP）：仅捕获源码，不求值、不解析内部语义。
            case "if":
            case "loop":
            case "run":
            {
                var computationKind = typeName switch
                {
                    "if" => ComputationKind.If,
                    "loop" => ComputationKind.Loop,
                    _ => ComputationKind.Run,
                };
                var (raw, closed) = CaptureComputationBlock(typeName, openerIndex);
                if (!closed)
                {
                    Report(PrtDiagnosticCodes.UnclosedFence, $"未闭合的围栏：`::: {typeName}` 缺少对应的结束围栏", openerIndex);
                }
                return new ComputationBlock
                {
                    Kind = computationKind,
                    Header = fence.Header,
                    Source = raw,
                    Line = line,
                    Column = column,
                };
            }

            default:
            {
                // 未知块类型：标准 14.4——严格模式报错，宽松模式按纯文本降级。
                Report(PrtDiagnosticCodes.UnknownBlockType, $"未知块类型「{typeName}」，已按纯文本降级", openerIndex);
                var (raw, _) = CaptureComputationBlock(typeName, openerIndex);
                return new UnknownBlock
                {
                    TypeName = typeName,
                    Body = raw,
                    Line = line,
                    Column = column,
                };
            }
        }
    }

    private PrtBlock ParseSectionBlock(
        ParsingHelpers.HeaderParts parts,
        int openerIndex,
        int line,
        int column,
        int depth)
    {
        ValidateParameters(parts, openerIndex, "section", "id", "title", "numeration", "toc");

        var block = new SectionBlock
        {
            Id = GetParam(parts, "id"),
            Title = GetParam(parts, "title") ?? (parts.FreeText.Length > 0 ? parts.FreeText : null),
            IncludeInToc = parts.Parameters.TryGetValue("toc", out var tocText)
                           && ParsingHelpers.TryParseBoolean(tocText, out var tocValue)
                ? tocValue
                : true,
            Line = line,
            Column = column,
        };

        if (parts.Parameters.TryGetValue("numeration", out var numerationText))
        {
            if (ParsingHelpers.TryParseNumberingStyle(numerationText, out var style))
            {
                block.Numeration = style;
            }
            else
            {
                Report(PrtDiagnosticCodes.InvalidEnumeration, $"非法编号样式「{numerationText}」", openerIndex);
            }
        }

        _index++;
        block.Children.AddRange(ParseBlocks("section", depth + 1, openerIndex));

        // 标题回退（标准 9.2）：省略 title= 且块内以标题行开头时，该标题行作为章节标题。
        if (block.Title is null && block.Children.Count > 0 && block.Children[0] is HeadingBlock firstHeading)
        {
            block.Title = firstHeading.PlainText;
            block.Children.RemoveAt(0);
        }

        return block;
    }

    private PrtBlock ParseColumnsBlock(
        ParsingHelpers.HeaderParts parts,
        int openerIndex,
        int line,
        int column,
        int depth)
    {
        ValidateParameters(parts, openerIndex, "columns", "cols");
        var cols = 2;
        if (parts.Parameters.TryGetValue("cols", out var colsText) && int.TryParse(colsText, out var parsed) && parsed > 0)
        {
            cols = parsed;
        }

        _index++;
        var children = ParseBlocks("columns", depth + 1, openerIndex);

        var layout = new LayoutBlock
        {
            Kind = LayoutKind.Columns,
            Columns = cols,
            Line = line,
            Column = column,
        };

        // 未包裹在 `::: column` 中的内容归入一个隐式栏。
        ColumnBlock? implicitColumn = null;
        foreach (var child in children)
        {
            if (child is ColumnBlock explicitColumn)
            {
                layout.ColumnItems.Add(explicitColumn);
                implicitColumn = null;
                continue;
            }
            if (implicitColumn is null)
            {
                implicitColumn = new ColumnBlock { Line = line, Column = column };
                layout.ColumnItems.Add(implicitColumn);
            }
            implicitColumn.Children.Add(child);
        }

        return layout;
    }

    private PrtBlock ParseTabsBlock(
        ParsingHelpers.HeaderParts parts,
        int openerIndex,
        int line,
        int column,
        int depth)
    {
        _index++;
        var children = ParseBlocks("tabs", depth + 1, openerIndex);

        var layout = new LayoutBlock
        {
            Kind = LayoutKind.Tabs,
            Line = line,
            Column = column,
        };

        TabBlock? implicitTab = null;
        var tabIndex = 0;
        foreach (var child in children)
        {
            if (child is TabBlock explicitTab)
            {
                layout.TabItems.Add(explicitTab);
                implicitTab = null;
                continue;
            }
            if (implicitTab is null)
            {
                tabIndex++;
                implicitTab = new TabBlock { Name = $"标签 {tabIndex}", Line = line, Column = column };
                layout.TabItems.Add(implicitTab);
            }
            implicitTab.Children.Add(child);
        }

        return layout;
    }

    private PrtBlock ParseTextStyleBlock(
        ParsingHelpers.HeaderParts parts,
        int openerIndex,
        int line,
        int column,
        int depth)
    {
        ValidateParameters(parts, openerIndex, "text", "color", "bg", "size", "align", "bold", "italic", "strike");

        var block = new TextStyleBlock { Line = line, Column = column };

        if (parts.Parameters.TryGetValue("color", out var colorText))
        {
            if (ParsingHelpers.TryParseNamedColor(colorText, out var color))
            {
                block.Color = color;
            }
            else
            {
                Report(PrtDiagnosticCodes.InvalidColor, $"非法颜色「{colorText}」，仅接受命名色（标准 12.1）", openerIndex);
            }
        }

        if (parts.Parameters.TryGetValue("size", out var sizeText))
        {
            if (ParsingHelpers.TryParseFontSize(sizeText, out var size))
            {
                block.Size = size;
            }
        }

        if (parts.Parameters.TryGetValue("align", out var alignText))
        {
            if (ParsingHelpers.TryParseAlignment(alignText, out var align))
            {
                block.Align = align;
            }
        }

        block.Bold = parts.Flags.Contains("bold") || GetBoolParam(parts, "bold");
        block.Italic = parts.Flags.Contains("italic") || GetBoolParam(parts, "italic");
        block.Strike = parts.Flags.Contains("strike") || GetBoolParam(parts, "strike");

        _index++;
        block.Children.AddRange(ParseBlocks("text", depth + 1, openerIndex));
        return block;
    }

    private PrtBlock ParseAlignBlock(
        ParsingHelpers.HeaderParts parts,
        int openerIndex,
        int line,
        int column,
        int depth)
    {
        var alignText = parts.FreeText.Length > 0
            ? parts.FreeText
            : (parts.Flags.Count > 0 ? parts.Flags[0] : string.Empty);

        var block = new AlignBlock { Line = line, Column = column };
        if (ParsingHelpers.TryParseAlignment(alignText, out var align))
        {
            block.Align = align;
        }
        else
        {
            Report(PrtDiagnosticCodes.InvalidAttribute, $"非法对齐取值「{alignText}」", openerIndex);
        }

        _index++;
        block.Children.AddRange(ParseBlocks("align", depth + 1, openerIndex));
        return block;
    }

    private PrtBlock ParseTableBlock(
        ParsingHelpers.HeaderParts parts,
        int openerIndex,
        int line,
        int column)
    {
        ValidateParameters(parts, openerIndex, "table", "id", "caption", "align");

        var block = new TableBlock
        {
            Id = GetParam(parts, "id"),
            Caption = GetParam(parts, "caption"),
            Line = line,
            Column = column,
        };

        if (parts.Parameters.TryGetValue("align", out var alignText)
            && ParsingHelpers.TryParseAlignment(alignText, out var align))
        {
            block.TableAlign = align switch
            {
                TextAlignment.Center => TableAlignment.Center,
                TextAlignment.Right => TableAlignment.Right,
                _ => TableAlignment.Left,
            };
        }

        var rows = new List<(string Text, int Line)>();
        _index++;
        while (_index < LineCount)
        {
            var fence = ParsingHelpers.ParseFenceLine(_source.Lines[_index].Text);
            if (fence.IsFence && fence.IsEnd)
            {
                _index++;
                break;
            }
            var text = _source.Lines[_index].Text;
            if (text.Trim().Length > 0)
            {
                rows.Add((text, AbsLine(_index)));
            }
            _index++;
        }

        var model = TableParser.Parse(rows, _options, _diagnostics);
        if (model is not null)
        {
            block.Table = model;
        }
        return block;
    }

    private PrtBlock ParseFigureBlock(
        ParsingHelpers.HeaderParts parts,
        int openerIndex,
        int line,
        int column,
        int depth)
    {
        var block = new FigureBlock
        {
            Id = GetParam(parts, "id"),
            Caption = GetParam(parts, "caption"),
            Line = line,
            Column = column,
        };

        _index++;
        block.Children.AddRange(ParseBlocks("figure", depth + 1, openerIndex));

        // 题注/替代文本：从块体内首个图片语法中提取（标准 9.4）。
        foreach (var child in block.Children)
        {
            var image = FindImage(child);
            if (image is not null)
            {
                block.Alt = image.Alt;
                block.Source = image.Url;
                break;
            }
        }

        return block;
    }

    private PrtBlock ParseEquationBlock(
        ParsingHelpers.HeaderParts parts,
        int openerIndex,
        int line,
        int column)
    {
        var (body, _) = CaptureStructuredBody(openerIndex, "equation", ignoreBareEnd: false);
        return new EquationBlock
        {
            Id = GetParam(parts, "id"),
            Caption = GetParam(parts, "caption"),
            Source = body,
            Line = line,
            Column = column,
        };
    }

    private static LinkInline? FindImage(PrtBlock block)
    {
        switch (block)
        {
            case ParagraphBlock p:
                foreach (var inline in p.Inlines)
                {
                    if (inline is LinkInline { IsImage: true } img)
                    {
                        return img;
                    }
                }
                break;
            case QuoteBlock q:
                foreach (var child in q.Children)
                {
                    var found = FindImage(child);
                    if (found is not null)
                    {
                        return found;
                    }
                }
                break;
        }
        return null;
    }

    // ─────────────────────────── 结构块：meta / refs / theme ───────────────────────────

    private PrtBlock ParseMetaBlock(int openerIndex, int line, int column)
    {
        var (body, _) = CaptureStructuredBody(openerIndex, "meta", ignoreBareEnd: true);
        var metadata = new PrtMetadata();
        var bodyLines = body.Split('\n');

        foreach (var rawLine in bodyLines)
        {
            var text = rawLine.Trim();
            if (text.Length == 0)
            {
                continue;
            }
            var colon = text.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                ParsingHelpers.Report(
                    _diagnostics,
                    _options.Strict,
                    PrtDiagnosticCodes.InvalidMetaEntry,
                    $"元数据行缺少「键: 值」形式：「{text}」",
                    line,
                    column);
                continue;
            }

            var key = text[..colon].Trim();
            var value = Unquote(text[(colon + 1)..].Trim());

            switch (key.ToLowerInvariant())
            {
                case "title": metadata.Title = value; break;
                case "subtitle": metadata.Subtitle = value; break;
                case "author": metadata.Authors.Add(value); break;
                case "version": metadata.Version = value; break;
                case "date": metadata.Date = value; break;
                case "timezone": metadata.Timezone = value; break;
                case "lang": metadata.Language = value; break;
                case "theme": metadata.Theme = value; break;
                case "tags":
                    foreach (var tag in value.Split(TagSeparators, StringSplitOptions.RemoveEmptyEntries))
                    {
                        metadata.Tags.Add(tag.Trim());
                    }
                    break;
                case "toc":
                    if (ParsingHelpers.TryParseBoolean(value, out var toc)) metadata.Toc = toc;
                    break;
                case "numeration":
                    if (ParsingHelpers.TryParseNumberingStyle(value, out var style)) metadata.Numeration = style;
                    break;
                case "page": metadata.Page = value; break;
                case "margin": metadata.Margin = value; break;
                case "strict":
                    if (ParsingHelpers.TryParseBoolean(value, out var strict)) metadata.Strict = strict;
                    break;
                case "glossary":
                    ParseGlossaryValue(value, metadata);
                    break;
                default:
                    if (key.StartsWith("custom.", StringComparison.OrdinalIgnoreCase))
                    {
                        metadata.Custom[key["custom.".Length..]] = value;
                    }
                    else
                    {
                        ParsingHelpers.Report(
                            _diagnostics,
                            _options.Strict,
                            PrtDiagnosticCodes.InvalidMetaEntry,
                            $"未知元数据键「{key}」，已忽略",
                            line,
                            column);
                    }
                    break;
            }
        }

        return new MetaBlock { Metadata = metadata, Line = line, Column = column };
    }

    /// <summary>
    /// 解析 glossary 值。已确认格式：`<术语>: <定义>`（或 `<术语>=<定义>`），
    /// 多条以 `;` / `；` / 换行分隔。
    /// </summary>
    private static void ParseGlossaryValue(string value, PrtMetadata metadata)
    {
        var segments = value.Split(GlossarySeparators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var s = segment.Trim();
            if (s.Length == 0)
            {
                continue;
            }
            var separator = s.IndexOf('=', StringComparison.Ordinal);
            var colon = s.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0 && (separator < 0 || colon < separator))
            {
                separator = colon;
            }
            if (separator <= 0)
            {
                continue;
            }
            var term = s[..separator].Trim();
            var def = s[(separator + 1)..].Trim();
            if (term.Length > 0)
            {
                metadata.Glossary.Add(new GlossaryEntry(term, Unquote(def)));
            }
        }
    }

    private PrtBlock ParseRefsBlock(int openerIndex, int line, int column)
    {
        var (body, _) = CaptureStructuredBody(openerIndex, "refs", ignoreBareEnd: true);
        var block = new RefsBlock { Line = line, Column = column };
        var bodyLines = body.Split('\n');

        foreach (var rawLine in bodyLines)
        {
            var text = rawLine.Trim();
            if (text.Length == 0 || text.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var eq = text.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                ParsingHelpers.Report(
                    _diagnostics,
                    _options.Strict,
                    PrtDiagnosticCodes.InvalidMetaEntry,
                    $"refs 行缺少「别名 = 目标ID」形式：「{text}」",
                    line,
                    column);
                continue;
            }

            var alias = text[..eq].Trim();
            var rest = text[(eq + 1)..];

            // 目标 ID 中的字面 `|` 需写作 `\|`（标准 9.4）。
            string targetId;
            string? display = null;
            var barIndex = FindUnescapedBar(rest);
            if (barIndex >= 0)
            {
                targetId = ParsingHelpers.Unescape(rest[..barIndex].Trim());
                display = rest[(barIndex + 1)..].Trim();
                if (display.Length == 0)
                {
                    display = null;
                }
            }
            else
            {
                targetId = ParsingHelpers.Unescape(rest.Trim());
            }

            block.Entries.Add(new RefEntry
            {
                Alias = alias,
                TargetId = targetId,
                DefaultDisplay = display,
                Line = line,
                Column = column,
            });
        }

        return block;
    }

    private static int FindUnescapedBar(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }
            if (text[i] == '|')
            {
                return i;
            }
        }
        return -1;
    }

    private PrtBlock ParseThemeBlock(
        ParsingHelpers.HeaderParts parts,
        int openerIndex,
        int line,
        int column)
    {
        var block = new ThemeBlock
        {
            Name = GetParam(parts, "name"),
            Base = GetParam(parts, "base"),
            Line = line,
            Column = column,
        };

        var (body, _) = CaptureStructuredBody(openerIndex, "theme", ignoreBareEnd: false);
        foreach (var rawLine in body.Split('\n'))
        {
            var text = rawLine.Trim();
            if (text.Length == 0 || text.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }
            var colon = text.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                ParsingHelpers.Report(
                    _diagnostics,
                    _options.Strict,
                    PrtDiagnosticCodes.InvalidAttribute,
                    $"主题变量缺少「名称: 值」形式：「{text}」",
                    line,
                    column);
                continue;
            }
            var name = text[..colon].Trim();
            var value = text[(colon + 1)..].Trim();
            block.Variables.Add(new KeyValuePair<string, string>(name, value));
        }

        return block;
    }

    // ────────────────────────────── 基础块 ──────────────────────────────

    private CodeBlock ParseCodeFence()
    {
        var firstText = _source.Lines[_index].Text;
        var trimmed = firstText.TrimStart();
        var marker = trimmed[0];
        var language = trimmed.TrimStart(marker).Trim();
        var startLine = AbsLine(_index);

        _index++;
        var sb = new StringBuilder();
        while (_index < LineCount)
        {
            var t = _source.Lines[_index].Text.TrimStart();
            if (t.Length >= 3 && t[0] == marker && t[1] == marker && t[2] == marker)
            {
                _index++;
                break;
            }
            sb.Append(_source.Lines[_index].Text).Append('\n');
            _index++;
        }

        return new CodeBlock
        {
            Language = language.Length == 0 ? null : language,
            Code = sb.ToString().TrimEnd('\n'),
            Line = startLine,
            Column = AbsColumn(1),
        };
    }

    private QuoteBlock ParseQuote(int depth)
    {
        var collected = new List<(string Text, int AbsLine)>();
        var startLine = AbsLine(_index);

        while (_index < LineCount)
        {
            var text = _source.Lines[_index].Text;
            if (!IsQuoteLine(text))
            {
                break;
            }
            var trimmed = text.TrimStart();
            var content = trimmed[1..];
            if (content.StartsWith(' '))
            {
                content = content[1..];
            }
            collected.Add((content, AbsLine(_index)));
            _index++;
        }

        var quote = new QuoteBlock { Line = startLine, Column = AbsColumn(1) };
        var sub = CreateSubParser(collected, 2);
        quote.Children.AddRange(sub.ParseBlocks(null, depth + 1, -1));
        return quote;
    }

    private PrtBlock ParsePipeTable()
    {
        var rows = new List<(string Text, int Line)>();
        var startLine = AbsLine(_index);
        while (_index < LineCount && TableParser.LooksLikeTableRow(_source.Lines[_index].Text))
        {
            rows.Add((_source.Lines[_index].Text, AbsLine(_index)));
            _index++;
        }

        var model = TableParser.Parse(rows, _options, _diagnostics) ?? new TableModel();
        return new PipeTableBlock { Table = model, Line = startLine, Column = AbsColumn(1) };
    }

    private ParagraphBlock ParseParagraph()
    {
        var startLine = AbsLine(_index);
        var inlines = new List<PrtInline>();
        var first = true;

        while (_index < LineCount)
        {
            var line = _source.Lines[_index];
            if (line.IsBlank || (!first && IsBlockStartInternal(_index)))
            {
                break;
            }
            if (!first)
            {
                inlines.Add(new LineBreakInline { Line = AbsLine(_index), Column = AbsColumn(1) });
            }
            inlines.AddRange(InlineParser.Parse(line.Text, AbsLine(_index), AbsColumn(1), _options, _diagnostics));
            first = false;
            _index++;
        }

        var paragraph = new ParagraphBlock { Line = startLine, Column = AbsColumn(1) };
        paragraph.Inlines.AddRange(inlines);
        return paragraph;
    }

    private PrtBlock ParseList(int depth)
    {
        var startLine = AbsLine(_index);
        var baseIndent = GetIndent(_source.Lines[_index].Text);
        TryGetListInfo(_source.Lines[_index].Text, out var ordered, out _, out _, out var startNumber);

        var list = new ListBlock
        {
            Ordered = ordered,
            StartNumber = startNumber,
            Line = startLine,
            Column = AbsColumn(baseIndent + 1),
        };

        while (_index < LineCount)
        {
            var text = _source.Lines[_index].Text;
            if (text.Trim().Length == 0)
            {
                break;
            }

            var indent = GetIndent(text);
            if (indent != baseIndent)
            {
                break;
            }

            if (!TryGetListInfo(text, out var itemOrdered, out var markerLength, out var isTask, out _)
                || itemOrdered != ordered)
            {
                break;
            }

            var itemLine = AbsLine(_index);
            var contentColumn = indent + markerLength + 1;
            var content = text[(indent + markerLength)..];
            if (content.StartsWith(' '))
            {
                content = content[1..];
            }

            var item = new ListItemBlock
            {
                IsTask = isTask,
                Line = itemLine,
                Column = AbsColumn(indent + 1),
            };

            if (isTask)
            {
                content = ParseTaskMarker(content, out var isChecked);
                item.IsChecked = isChecked;
            }

            item.Inlines.AddRange(InlineParser.Parse(content, itemLine, AbsColumn(contentColumn), _options, _diagnostics));
            _index++;

            // 收集该项的续行（缩进更深者），作为子块解析。
            var continuation = new List<(string Text, int AbsLine)>();
            while (_index < LineCount)
            {
                var next = _source.Lines[_index].Text;
                if (next.Trim().Length == 0)
                {
                    break;
                }
                var nextIndent = GetIndent(next);
                if (nextIndent <= baseIndent)
                {
                    break;
                }
                continuation.Add((next, AbsLine(_index)));
                _index++;
            }

            if (continuation.Count > 0)
            {
                var sub = CreateSubParser(continuation, 0);
                item.Children.AddRange(sub.ParseBlocks(null, depth + 1, -1));
            }

            list.Items.Add(item);
        }

        return list;
    }

    private static string ParseTaskMarker(string content, out bool isChecked)
    {
        isChecked = false;
        if (content.Length >= 3 && content[0] == '[')
        {
            var marker = content[1];
            if ((marker == ' ' || marker is 'x' or 'X') && content[2] == ']')
            {
                isChecked = marker is 'x' or 'X';
                var rest = content[3..];
                return rest.StartsWith(' ') ? rest[1..] : rest;
            }
        }
        return content;
    }

    // ────────────────────────────── 通用工具 ──────────────────────────────

    private BlockParser CreateSubParser(List<(string Text, int AbsLine)> lines, int columnOffset)
    {
        var sb = new StringBuilder();
        foreach (var (text, _) in lines)
        {
            sb.Append(text).Append('\n');
        }
        var source = SourceText.From(sb.ToString());
        var lineOffset = lines.Count > 0 ? lines[0].AbsLine - 1 : 0;
        return new BlockParser(source, _options, _diagnostics, lineOffset, _columnOffset + columnOffset);
    }

    /// <summary>
    /// 捕获结构块（meta / refs / theme / equation / code）的块体文本。
    /// <paramref name="ignoreBareEnd"/> 为 true 时，裸 `:::` 行按可选分隔处理并忽略（标准 9.1）。
    /// </summary>
    private (string Body, bool Closed) CaptureStructuredBody(int openerIndex, string typeName, bool ignoreBareEnd)
    {
        var sb = new StringBuilder();
        _index = openerIndex + 1;
        var closed = false;

        while (_index < LineCount)
        {
            var text = _source.Lines[_index].Text;
            var fence = ParsingHelpers.ParseFenceLine(text);
            if (fence.IsFence && fence.IsEnd)
            {
                if (fence.IsBare && ignoreBareEnd)
                {
                    _index++;
                    continue;
                }
                _index++;
                closed = true;
                break;
            }
            sb.Append(text).Append('\n');
            _index++;
        }

        return (sb.ToString().TrimEnd('\n'), closed);
    }

    /// <summary>
    /// 捕获计算块 / 未知块的原始文本。`if` 的 `::: elif` / `::: else` 分支标记不增加嵌套层级。
    /// </summary>
    private (string Raw, bool Closed) CaptureComputationBlock(string rootType, int openerIndex)
    {
        var sb = new StringBuilder();
        sb.Append(_source.Lines[openerIndex].Text).Append('\n');
        _index = openerIndex + 1;
        var depth = 1;

        while (_index < LineCount)
        {
            var text = _source.Lines[_index].Text;
            var fence = ParsingHelpers.ParseFenceLine(text);
            if (fence.IsFence)
            {
                if (fence.IsEnd)
                {
                    depth--;
                    sb.Append(text).Append('\n');
                    _index++;
                    if (depth == 0)
                    {
                        return (sb.ToString().TrimEnd('\n'), true);
                    }
                    continue;
                }

                var isBranchMarker = string.Equals(rootType, "if", StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(fence.TypeName, "elif", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(fence.TypeName, "else", StringComparison.OrdinalIgnoreCase));
                if (!isBranchMarker)
                {
                    depth++;
                }
                sb.Append(text).Append('\n');
                _index++;
                continue;
            }

            sb.Append(text).Append('\n');
            _index++;
        }

        return (sb.ToString().TrimEnd('\n'), false);
    }

    private void ValidateParameters(
        ParsingHelpers.HeaderParts parts,
        int openerIndex,
        string blockName,
        params string[] allowed)
    {
        var set = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
        foreach (var key in parts.Parameters.Keys)
        {
            if (!set.Contains(key))
            {
                Report(PrtDiagnosticCodes.UnknownParameter, $"块「{blockName}」的未知参数「{key}」，已忽略", openerIndex);
            }
        }
        foreach (var flag in parts.Flags)
        {
            if (!set.Contains(flag))
            {
                Report(PrtDiagnosticCodes.UnknownParameter, $"块「{blockName}」的未知标志「{flag}」，已忽略", openerIndex);
            }
        }
    }

    private static string? GetParam(ParsingHelpers.HeaderParts parts, string key)
        => parts.Parameters.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    private static bool GetBoolParam(ParsingHelpers.HeaderParts parts, string key)
        => parts.Parameters.TryGetValue(key, out var value)
           && ParsingHelpers.TryParseBoolean(value, out var result)
           && result;

    private static string Unquote(string text)
    {
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            return text[1..^1];
        }
        return text;
    }

    private static int GetIndent(string text)
    {
        var indent = 0;
        while (indent < text.Length && text[indent] == ' ')
        {
            indent++;
        }
        return indent;
    }

    private static bool IsCodeFence(string text)
    {
        var t = text.TrimStart();
        return t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith("~~~", StringComparison.Ordinal);
    }

    private static bool IsQuoteLine(string text)
    {
        var t = text.TrimStart();
        return t.Length > 0 && t[0] == '>';
    }

    private static bool IsThematicBreak(string text)
    {
        var t = text.Trim();
        if (t.Length < 3)
        {
            return false;
        }
        var dashCount = 0;
        foreach (var c in t)
        {
            if (c == '-')
            {
                dashCount++;
            }
            else if (c != ' ')
            {
                return false;
            }
        }
        return dashCount >= 3;
    }

    private static bool TryParseHeading(string text, out int level, out string content)
    {
        level = 0;
        content = string.Empty;

        var t = text.TrimStart();
        var count = 0;
        while (count < t.Length && t[count] == '#')
        {
            count++;
        }
        if (count == 0 || count > 6)
        {
            return false;
        }
        if (count < t.Length && t[count] != ' ')
        {
            // `##标题` 缺空格：不构成标题（标准 8.2 / 6.4）。
            return false;
        }

        level = count;
        content = t[count..].Trim();
        return true;
    }

    /// <summary>识别列表项：无序（`-`/`*`/`+` + 空白）或有序（`数字.` + 空白）。</summary>
    private static bool TryGetListInfo(
        string text,
        out bool ordered,
        out int markerLength,
        out bool isTask,
        out int startNumber)
    {
        ordered = false;
        markerLength = 0;
        isTask = false;
        startNumber = 1;

        var indent = GetIndent(text);
        if (indent >= text.Length)
        {
            return false;
        }

        var rest = text[indent..];

        // 分隔线优先（`---` 是分隔线，不是列表）。
        if (IsThematicBreak(text))
        {
            return false;
        }

        if (rest[0] is '-' or '*' or '+')
        {
            if (rest.Length < 2 || rest[1] != ' ')
            {
                return false;
            }
            ordered = false;
            markerLength = 1;
            var content = rest.Length > 2 ? rest[2..] : string.Empty;
            isTask = content.Length >= 3 && content[0] == '[' && content[2] == ']'
                     && (content[1] == ' ' || content[1] is 'x' or 'X');
            return true;
        }

        var digits = 0;
        while (digits < rest.Length && char.IsDigit(rest[digits]))
        {
            digits++;
        }
        if (digits > 0 && digits + 1 <= rest.Length - 1 && rest[digits] == '.' && rest[digits + 1] == ' ')
        {
            ordered = true;
            markerLength = digits + 1;
            // 不检查 TryParse 的返回值也算正确：失败时 out 为 0，与显式写 0 / 负数同样按 1 处理（D-18：不抛异常）。
            if (!int.TryParse(rest[..digits], out startNumber) || startNumber <= 0)
            {
                startNumber = 1;
            }
            var content = rest[(digits + 2)..];
            isTask = content.Length >= 3 && content[0] == '[' && content[2] == ']'
                     && (content[1] == ' ' || content[1] is 'x' or 'X');
            return true;
        }

        return false;
    }

    private bool IsBlockStartInternal(int index)
    {
        var line = _source.Lines[index];
        if (line.IsBlank)
        {
            return true;
        }
        var text = line.Text;
        var trimStart = text.TrimStart();
        if (trimStart.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }
        if (IsCodeFence(text))
        {
            return true;
        }
        var fence = ParsingHelpers.ParseFenceLine(text);
        if (fence.IsFence)
        {
            return true;
        }
        if (TryParseHeading(text, out _, out _))
        {
            return true;
        }
        if (IsThematicBreak(text))
        {
            return true;
        }
        if (IsQuoteLine(text))
        {
            return true;
        }
        if (TryGetListInfo(text, out _, out _, out _, out _))
        {
            return true;
        }
        if (TableParser.LooksLikeTableRow(text)
            && index + 1 < LineCount
            && ParsingHelpers.IsTableSeparatorRow(_source.Lines[index + 1].Text))
        {
            return true;
        }
        return false;
    }
}
