using System.Text;
using System.Text.RegularExpressions;

namespace Prt.Core.Import;

/// <summary>
/// HTML → PRT 的轻量导入器：把浏览器另存 / Word 导出的 HTML 转成 PRT 文本。
/// <para>
/// 目标是把「常见文档型 HTML」转成结构对等的 PRT：标题、段落、列表、引用、
/// 代码块、表格、分隔线，以及行内的粗体 / 斜体 / 删除线 / 行内代码 / 链接 / 图片。
/// 无法识别的标签按容器处理（保留其内的文字），<c>script</c> / <c>style</c> / 注释一律丢弃。
/// </para>
/// <para>
/// 本类只做文本转换，不接触任何 UI；输出始终是「未做任何求值」的 PRT 源文本。
/// </para>
/// </summary>
public static class HtmlImporter
{
    private static readonly Regex DropBlocks = new(
        "<(script|style|noscript|head|iframe|svg|canvas|form|button|input)\\b[^>]*>.*?</\\1\\s*>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DropSelfClosing = new(
        "<(meta|link|base|input)\\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Comment = new("<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TitleTag = new(
        "<title\\b[^>]*>(.*?)</title\\s*>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Token = new(
        "<[^>]+>|[^<]+",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Dictionary<string, string> Entities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["&amp;"] = "&",
        ["&lt;"] = "<",
        ["&gt;"] = ">",
        ["&quot;"] = "\"",
        ["&apos;"] = "'",
        ["&#39;"] = "'",
        ["&nbsp;"] = " ",
        ["&mdash;"] = "—",
        ["&ndash;"] = "–",
        ["&hellip;"] = "…",
        ["&times;"] = "×",
        ["&laquo;"] = "«",
        ["&raquo;"] = "»",
    };

    /// <summary>把 HTML 文本转换为 PRT 文本。</summary>
    public static string Convert(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var title = TitleTag.Match(html) is { Success: true } titleMatch
            ? Decode(StripTags(titleMatch.Groups[1].Value)).Trim()
            : string.Empty;

        var body = html;
        var bodyMatch = Regex.Match(html, "<body\\b[^>]*>(.*)</body\\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (bodyMatch.Success)
        {
            body = bodyMatch.Groups[1].Value;
        }

        body = Comment.Replace(body, string.Empty);
        body = DropBlocks.Replace(body, string.Empty);
        body = DropSelfClosing.Replace(body, " ");

        var writer = new PrtWriter();
        var tokens = Token.Matches(body);
        foreach (Match token in tokens)
        {
            var text = token.Value;
            if (text.StartsWith('<'))
            {
                writer.HandleTag(text);
            }
            else
            {
                writer.HandleText(text);
            }
        }

        var content = writer.Finish();

        var output = new StringBuilder();
        if (title.Length > 0)
        {
            output.Append("# ").Append(title).Append('\n').Append('\n');
        }

        output.Append(content);
        return output.ToString().TrimEnd() + "\n";
    }

    private static string StripTags(string text) => Regex.Replace(text, "<[^>]+>", string.Empty);

    private static string Decode(string text)
    {
        if (text.IndexOf('&', StringComparison.Ordinal) < 0)
        {
            return text;
        }

        var result = text;
        foreach (var pair in Entities)
        {
            result = result.Replace(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase);
        }

        // 数字实体：&#123; / &#x1F4A1;
        result = Regex.Replace(result, "&#(\\d+);", m =>
            int.TryParse(m.Groups[1].Value, out var code) ? char.ConvertFromUtf32(code) : m.Value);
        result = Regex.Replace(result, "&#x([0-9a-fA-F]+);", m =>
            int.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var code)
                ? char.ConvertFromUtf32(code)
                : m.Value);
        return result;
    }

    /// <summary>行 / 块写入器：把 HTML 标签流累积成 PRT 文本。</summary>
    private sealed class PrtWriter
    {
        private readonly StringBuilder _output = new();
        private readonly StringBuilder _line = new();
        private readonly Stack<string> _blocks = new();     // p / h1 / blockquote / pre / li …
        private readonly Stack<(string Kind, int Index)> _lists = new();
        private readonly List<List<string>> _tableRows = new();
        private List<string>? _tableCells;
        private string? _tableKind;                          // th / td（首行判定表头）
        private bool _inPre;
        private int _blankPending;

        public void HandleTag(string rawTag)
        {
            var isClosing = rawTag.StartsWith("</", StringComparison.Ordinal);
            var tag = TagName(rawTag);
            switch (tag)
            {
                case "html" or "body" or "div" or "section" or "article" or "main" or "span" or "font"
                    or "thead" or "tbody" or "tfoot" or "colgroup" or "col" or "center" or "small" or "u":
                    // 纯容器：不产生结构变化（表格的行边界由 tr 提交）。
                    break;

                case "tr":
                    if (isClosing && _tableCells is { Count: > 0 })
                    {
                        _tableRows.Add(_tableCells);
                        _tableCells = null;
                    }
                    break;

                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    if (isClosing)
                    {
                        var level = tag[1] - '0';
                        Commit(new string('#', level) + " " + FlushLine());
                    }
                    break;

                case "p":
                    if (isClosing)
                    {
                        Commit(FlushLine());
                    }
                    break;

                case "blockquote":
                    if (isClosing)
                    {
                        Commit(FlushLine(), prefix: (line) => "> " + line);
                    }
                    break;

                case "pre":
                    _inPre = !isClosing;
                    if (isClosing)
                    {
                        Commit("```\n" + _line.ToString().TrimEnd('\n', '\r') + "\n```");
                        _line.Clear();
                    }
                    break;

                case "ul" or "ol":
                    if (isClosing)
                    {
                        if (_lists.Count > 0)
                        {
                            _lists.Pop();
                        }
                    }
                    else
                    {
                        _lists.Push((tag, 0));
                    }
                    break;

                case "li":
                    if (isClosing)
                    {
                        var text = FlushLine();
                        if (text.Length > 0)
                        {
                            var depth = Math.Max(0, _lists.Count - 1);
                            var marker = "- ";
                            if (_lists.Count > 0)
                            {
                                var (kind, index) = _lists.Pop();
                                index++;
                                marker = kind == "ol" ? index + ". " : "- ";
                                _lists.Push((kind, index));
                            }

                            Commit(new string(' ', depth * 2) + marker + text);
                        }
                    }
                    break;

                case "table":
                    if (isClosing)
                    {
                        FlushTable();
                    }
                    else
                    {
                        _tableRows.Clear();
                        _tableCells = null;
                        _tableKind = null;
                    }
                    break;

                case "th" or "td":
                    if (isClosing)
                    {
                        _tableCells ??= [];
                        _tableCells.Add(FlushLine());
                        if (tag == "th")
                        {
                            _tableKind ??= "th";
                        }
                    }
                    break;

                case "hr":
                    Commit("---");
                    break;

                case "br":
                    if (!isClosing)
                    {
                        _line.Append('\n');
                    }
                    break;

                case "strong" or "b":
                    AppendInline("**");
                    break;

                case "em" or "i":
                    AppendInline("*");
                    break;

                case "del" or "s" or "strike":
                    AppendInline("~~");
                    break;

                case "mark":
                    AppendInline("==");
                    break;

                case "code" or "tt" or "kbd" or "samp":
                    AppendInline("`");
                    break;

                case "sup":
                    AppendInline("^");
                    break;

                case "sub":
                    AppendInline("~");
                    break;

                case "a":
                    if (isClosing)
                    {
                        if (_linkHref.Length > 0)
                        {
                            _line.Append("](").Append(_linkHref).Append(')');
                            _linkHref = string.Empty;
                        }
                    }
                    else
                    {
                        _linkHref = Attr(rawTag, "href");
                        if (_linkHref.Length > 0)
                        {
                            _line.Append('[');
                        }
                    }
                    break;

                case "img":
                    if (!isClosing)
                    {
                        var src = Attr(rawTag, "src");
                        var alt = Attr(rawTag, "alt");
                        if (src.Length > 0)
                        {
                            _line.Append("![").Append(alt).Append("](").Append(src).Append(')');
                        }
                    }
                    break;
            }
        }

        private string _linkHref = string.Empty;

        private void AppendInline(string marker)
        {
            if (_inPre)
            {
                return; // 代码块内不解析行内标记
            }

            _line.Append(marker);
        }

        public void HandleText(string text)
        {
            var decoded = Decode(text);
            if (_inPre)
            {
                _line.Append(decoded);
                return;
            }

            var normalized = Regex.Replace(decoded, "\\s+", " ");
            if (normalized.Length == 0)
            {
                return;
            }

            if (_line.Length == 0 && normalized == " " && _blankPending >= 0)
            {
                _blankPending = 1;
                return;
            }

            _line.Append(normalized);
        }

        private string FlushLine()
        {
            var text = _line.ToString().Trim();
            _line.Clear();
            return text;
        }

        private void Commit(string block, Func<string, string>? prefix = null)
        {
            if (string.IsNullOrWhiteSpace(block))
            {
                return;
            }

            if (_blankPending > 0 && _output.Length > 0)
            {
                _output.Append('\n');
            }

            _blankPending = 0;

            if (prefix is not null)
            {
                var lines = block.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                foreach (var line in lines)
                {
                    _output.Append(prefix(line)).Append('\n');
                }
            }
            else
            {
                _output.Append(block).Append('\n').Append('\n');
            }
        }

        private void FlushTable()
        {
            if (_tableRows.Count == 0)
            {
                return;
            }

            var columns = _tableRows.Max(r => r.Count);
            var builder = new StringBuilder();
            for (var i = 0; i < _tableRows.Count; i++)
            {
                var cells = _tableRows[i].Select(EscapeCell).ToList();
                while (cells.Count < columns)
                {
                    cells.Add(string.Empty);
                }

                builder.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
                if (i == 0)
                {
                    builder.Append('|').Append(string.Concat(Enumerable.Repeat(" :-- |", columns))).Append('\n');
                }
            }

            Commit(builder.ToString().TrimEnd('\n'));
            _tableRows.Clear();
        }

        private static string EscapeCell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();

        public string Finish()
        {
            var rest = FlushLine();
            if (rest.Length > 0)
            {
                Commit(rest);
            }

            FlushTable();
            return Regex.Replace(_output.ToString(), "\n{3,}", "\n\n").Trim();
        }

        private static string TagName(string rawTag)
        {
            var name = rawTag.TrimStart('<', '/').TrimEnd('>');
            var space = name.IndexOfAny([' ', '\t', '\n', '\r', '/']);
            if (space >= 0)
            {
                name = name[..space];
            }

            return name.ToLowerInvariant();
        }

        private static string Attr(string rawTag, string name)
        {
            var match = Regex.Match(rawTag, name + "\\s*=\\s*(\"([^\"]*)\"|'([^']*)'|([^\\s>]+))", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return string.Empty;
            }

            return Decode(match.Groups[2].Success ? match.Groups[2].Value
                : match.Groups[3].Success ? match.Groups[3].Value
                : match.Groups[4].Value);
        }
    }
}
