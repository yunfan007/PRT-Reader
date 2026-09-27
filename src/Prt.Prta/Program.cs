using Prt.Prta.Compiler;

namespace Prt.Prta.Gui;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private readonly TextBox _idBox = new() { Text = "main" };
    private readonly TextBox _sigBox = new() { Text = "" };
    private readonly TextBox _inputBox = new();
    private readonly TextBox _outputBox = new();

    public MainForm()
    {
        Text = "PRTA → JS 编译器";
        Font = new Font("Microsoft YaHei UI", 9F);
        ClientSize = new Size(860, 620);
        MinimumSize = new Size(700, 500);
        StartPosition = FormStartPosition.CenterScreen;

        var idLabel = new Label { Text = "块 id：", Location = new Point(12, 15), AutoSize = true };
        _idBox.SetBounds(70, 12, 140, 23);

        var sigLabel = new Label { Text = "sig（可空）：", Location = new Point(230, 15), AutoSize = true };
        _sigBox.SetBounds(320, 12, 240, 23);
        _sigBox.TabIndex = 1;

        var compileButton = new Button
        {
            Text = "编译 → JS",
            Location = new Point(740, 10),
            Size = new Size(105, 27),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        compileButton.Click += OnCompileClick;

        var inputLabel = new Label { Text = "PRTA 指令（块体）：", Location = new Point(12, 46), AutoSize = true };
        _inputBox.SetBounds(12, 68, 834, 250);
        _inputBox.Multiline = true;
        _inputBox.ScrollBars = ScrollBars.Both;
        _inputBox.WordWrap = false;
        _inputBox.AcceptsReturn = true;
        _inputBox.Font = new Font("Consolas", 10F);
        _inputBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _inputBox.Text = """
; ── 计算 (a + b) × (c + d) ──
; 展示：寄存器做运算、名变量存值、堆栈暂存中间结果
VAR a: Integer = 3
VAR b: Integer = 7
VAR c: Integer = 5
VAR d: Integer = 2
; 第一步：算 a + b
LOAD  r0, a
LOAD  r1, b
ADD   r2, r0, r1          ; r2 = 10
PUSH  r2                  ; 把左半结果压入堆栈
; 第二步：算 c + d，复用 r0、r1
LOAD  r0, c               ; r0 原来的值被覆盖，没关系
LOAD  r1, d
ADD   r3, r0, r1          ; r3 = 7
; 第三步：取回左半结果，相乘
POP   r4                  ; r4 = 10
MUL   r5, r4, r3          ; r5 = 70
EMIT  @结果, r5
""";

        var outputLabel = new Label { Text = "目标 JavaScript：", Location = new Point(12, 326), AutoSize = true };
        _outputBox.SetBounds(12, 348, 834, 258);
        _outputBox.Multiline = true;
        _outputBox.ReadOnly = true;
        _outputBox.ScrollBars = ScrollBars.Both;
        _outputBox.WordWrap = false;
        _outputBox.Font = new Font("Consolas", 10F);
        _outputBox.BackColor = SystemColors.Window;
        _outputBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        Controls.AddRange([idLabel, _idBox, sigLabel, _sigBox, compileButton, inputLabel, _inputBox, outputLabel, _outputBox]);
    }

    private void OnCompileClick(object? sender, EventArgs e)
    {
        var id = string.IsNullOrWhiteSpace(_idBox.Text) ? "main" : _idBox.Text.Trim();
        var sig = string.IsNullOrWhiteSpace(_sigBox.Text) ? null : _sigBox.Text.Trim();
        var block = new PrtaBlock(id, sig, null, new Dictionary<string, string>(), 0, _inputBox.Text);

        // 单块输入：无文档上下文，端口存在性等文档级检查关闭（EMIT @结果 之类直接可用）。
        var options = new PrtaCompileOptions { ValidatePorts = false };
        var ok = PrtaCompiler.TryCompileDocument([block], options, out var js, out var diagnostics);
        _outputBox.Text = ok
            ? js
            : "编译失败（" + diagnostics.Count + " 个问题）：\r\n" + string.Join("\r\n", diagnostics.Select(d => d.ToString()));
    }
}
