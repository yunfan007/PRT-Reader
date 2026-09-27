using System.Windows;
using System.Windows.Controls;
using Prt.App.Services;

namespace Prt.App.Views;

// 编辑器右键菜单与行级操作（DocumentView 的 partial；D-06）
// 拆分依据：《工程改进方案》§4.8 / CRS 9.6.4 D-06；拆分**只做位置迁移**，未改行为。
public partial class DocumentView : UserControl
{
    // ─────────────────────────────── 编辑器右键菜单 ───────────────────────────────

    /// <summary>
    /// 构建源码编辑器的右键菜单。
    /// <para>
    /// 两种主要用法：<br/>
    /// · 选中一段文本后右键 → 「插入框」把选区包进语义框 / 结构块（行块则改前缀）；<br/>
    /// · 在空白行右键 → 「插入框」就地插入块骨架，光标落在内容行上。
    /// </para>
    /// </summary>
    private void BuildEditorContextMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(CreateCommandItem("撤销", "Ctrl+Z", () => Undo()));
        menu.Items.Add(CreateCommandItem("重做", "Ctrl+Y", () => Redo()));
        menu.Items.Add(new Separator());

        _cutItem = CreateCommandItem("剪切", "Ctrl+X", () => Cut());
        _copyItem = CreateCommandItem("复制", "Ctrl+C", () => Copy());
        _pasteItem = CreateCommandItem("粘贴", "Ctrl+V", () => Paste());
        menu.Items.Add(_cutItem);
        menu.Items.Add(_copyItem);
        menu.Items.Add(_pasteItem);
        menu.Items.Add(new Separator());

        menu.Items.Add(CreateCommandItem("选中光标所在整行", "Ctrl+L", SelectCurrentLine));
        menu.Items.Add(new Separator());

        // 单行可视编辑：不用手打标记，直接把光标所在行换成另一种角色。
        var lineMenu = new MenuItem { Header = "这一行（可视编辑）" };
        lineMenu.Items.Add(CreateLineKindItem("标题 1", LineKind.Heading1));
        lineMenu.Items.Add(CreateLineKindItem("标题 2", LineKind.Heading2));
        lineMenu.Items.Add(CreateLineKindItem("标题 3", LineKind.Heading3));
        lineMenu.Items.Add(CreateLineKindItem("标题 4", LineKind.Heading4));
        lineMenu.Items.Add(CreateLineKindItem("标题 5", LineKind.Heading5));
        lineMenu.Items.Add(CreateLineKindItem("标题 6", LineKind.Heading6));
        lineMenu.Items.Add(new Separator());
        lineMenu.Items.Add(CreateLineKindItem("无序列表项", LineKind.Bullet));
        lineMenu.Items.Add(CreateLineKindItem("有序列表项", LineKind.Ordered));
        lineMenu.Items.Add(CreateLineKindItem("任务清单项", LineKind.Task));
        lineMenu.Items.Add(CreateLineKindItem("引用行", LineKind.Quote));
        lineMenu.Items.Add(CreateLineKindItem("包成代码块", LineKind.Code));
        lineMenu.Items.Add(new Separator());
        lineMenu.Items.Add(CreateLineKindItem("去掉行首标记（普通段落）", LineKind.Plain));
        lineMenu.Items.Add(new Separator());
        lineMenu.Items.Add(CreateCommandItem("整行上移", "Alt+↑", () => ApplyLineMove(-1)));
        lineMenu.Items.Add(CreateCommandItem("整行下移", "Alt+↓", () => ApplyLineMove(+1)));
        menu.Items.Add(lineMenu);

        menu.Items.Add(new Separator());

        _insertMenu = new MenuItem { Header = "插入框" };
        _insertMenu.Items.Add(BuildSnippetGroup(PrtSnippets.GroupSemantic, "语义框（标准 8.3）"));
        _insertMenu.Items.Add(BuildSnippetGroup(PrtSnippets.GroupStructure, "结构块（标准 8.5）"));
        _insertMenu.Items.Add(BuildSnippetGroup(PrtSnippets.GroupBasic, "基础块（标准 8.2）"));
        menu.Items.Add(_insertMenu);

        _wrapHintItem = new MenuItem
        {
            Header = "提示：选中文本后插入 = 包裹该段；空白行插入 = 生成空骨架",
            IsEnabled = false,
        };
        menu.Items.Add(new Separator());
        menu.Items.Add(_wrapHintItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateCommandItem("全选", "Ctrl+A", SelectAllText));

        menu.Opened += OnEditorContextMenuOpened;
        _contextMenu = menu;
        Editor.ContextMenu = menu;
    }

    private void OnEditorContextMenuOpened(object sender, RoutedEventArgs e)
    {
        var hasSelection = Editor.SelectionLength > 0;

        if (_insertMenu is not null)
        {
            _insertMenu.Header = hasSelection ? "插入框（包裹选中内容）" : "插入框（插入空骨架）";
        }
        if (_cutItem is not null)
        {
            _cutItem.IsEnabled = hasSelection;
        }
        if (_copyItem is not null)
        {
            _copyItem.IsEnabled = hasSelection;
        }
        if (_pasteItem is not null)
        {
            // 这里**不再**探测剪贴板。
            // 旧写法是 Clipboard.ContainsText()——那也是对系统剪贴板的读取（DEV-04），
            // 按 3.8 属绕开 SRT；而每开一次右键菜单申报一次，用户只给了单次许可时
            // 会变成"右键一次弹一次窗"。改为让菜单项恒可用：粘贴时由 SRT 申报，
            // 剪贴板没有文本就安静地什么都不做（见 DocumentView.Paste），
            // 既不误报"删掉选中内容"，也没有未申报的剪贴板访问。
            _pasteItem.IsEnabled = true;
        }
        if (_wrapHintItem is not null)
        {
            _wrapHintItem.Header = hasSelection
                ? "将把选中的整行内容包进所选框类型"
                : "将在光标处另起一段并插入所选框类型的空骨架";
        }
    }

    private static MenuItem CreateCommandItem(string header, string gesture, Action action)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture };
        item.Click += (_, _) => action();
        return item;
    }

    private MenuItem BuildSnippetGroup(string group, string header)
    {
        var root = new MenuItem { Header = header };
        foreach (var snippet in PrtSnippets.InGroup(group))
        {
            var item = new MenuItem
            {
                Header = snippet.Label,
                InputGestureText = snippet.Gesture,
            };
            var id = snippet.Id;
            item.Click += (_, _) => ApplySnippet(id);
            root.Items.Add(item);
        }

        return root;
    }

    /// <summary>「这一行」菜单项：把光标所在行换成指定形态。</summary>
    private MenuItem CreateLineKindItem(string label, LineKind kind)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => ApplyLineKind(kind);
        return item;
    }

    /// <summary>把光标所在行换成目标形态（单行可视编辑的入口）。</summary>
    internal void ApplyLineKind(LineKind kind)
    {
        FocusEditor();

        var result = LineEdit.Apply(Editor.Text, Editor.CaretIndex, kind);
        if (result is null)
        {
            StatusMessage?.Invoke(this, "这一行已经是该形态");
            return;
        }

        ApplyEdit(result.Value);
        StatusMessage?.Invoke(this, "已改写当前行：" + Label(kind));
    }

    /// <summary>整行上移 / 下移。</summary>
    internal void ApplyLineMove(int delta)
    {
        FocusEditor();

        var result = LineEdit.MoveLine(Editor.Text, Editor.CaretIndex, delta);
        if (result is null)
        {
            StatusMessage?.Invoke(this, delta < 0 ? "已经是第一行" : "已经是最后一行");
            return;
        }

        ApplyEdit(result.Value);
        StatusMessage?.Invoke(this, delta < 0 ? "已上移一行" : "已下移一行");
    }

    /// <summary>把一次「替换区间 + 之后的选区」写入编辑器（保留撤销栈）。</summary>
    private void ApplyEdit(EditResult result)
    {
        Editor.Select(result.Start, result.Length);
        Editor.SelectedText = result.Text;
        Editor.Select(result.SelectionStart, result.SelectionLength);
        Editor.Focus();

        var (line, _) = TextNavigation.ToLineColumn(Editor.Text, result.SelectionStart);
        Editor.ScrollToLine(Math.Max(0, line - 1));
        RestartDebounce();
    }

    private static string Label(LineKind kind) => kind switch
    {
        LineKind.Heading1 => "标题 1",
        LineKind.Heading2 => "标题 2",
        LineKind.Heading3 => "标题 3",
        LineKind.Heading4 => "标题 4",
        LineKind.Heading5 => "标题 5",
        LineKind.Heading6 => "标题 6",
        LineKind.Bullet => "无序列表项",
        LineKind.Ordered => "有序列表项",
        LineKind.Task => "任务清单项",
        LineKind.Quote => "引用行",
        LineKind.Code => "代码块",
        _ => "普通段落",
    };

    /// <summary>选中光标 / 选区所在的整行（含行尾换行，便于整行剪切与加块）。</summary>
    public void SelectCurrentLine()
    {
        FocusEditor();

        var text = Editor.Text;
        var (start, length) = Editor.SelectionLength > 0
            ? PrtSnippets.ExpandToWholeLines(text, Editor.SelectionStart, Editor.SelectionLength)
            : PrtSnippets.WholeLineRange(text, Editor.CaretIndex);

        Editor.Select(start, length);
        Editor.Focus();

        var (line, _) = TextNavigation.ToLineColumn(text, start);
        Editor.ScrollToLine(Math.Max(0, line - 1));
        StatusMessage?.Invoke(this, $"已选中第 {line} 行");
    }

    /// <summary>
    /// 插入片段：有选区则包裹选中内容，无选区则在光标处插入骨架。
    /// </summary>
    public void ApplySnippet(string snippetId)
    {
        var snippet = PrtSnippets.ById(snippetId);
        if (snippet is null)
        {
            return;
        }

        FocusEditor();

        var result = Editor.SelectionLength > 0
            ? PrtSnippets.WrapSelection(Editor.Text, Editor.SelectionStart, Editor.SelectionLength, snippet)
            : PrtSnippets.InsertSnippet(Editor.Text, Editor.CaretIndex, snippet);

        // 通过「选中 → 替换选中」写入，保留文本框自身的撤销 / 重做栈。
        Editor.Select(result.Start, result.Length);
        Editor.SelectedText = result.Text;
        Editor.Select(result.SelectionStart, result.SelectionLength);
        Editor.Focus();

        var (line, _) = TextNavigation.ToLineColumn(Editor.Text, result.SelectionStart);
        Editor.ScrollToLine(Math.Max(0, line - 1));

        StatusMessage?.Invoke(this, $"已插入：{snippet.Label}");
        RestartDebounce();
    }
}
