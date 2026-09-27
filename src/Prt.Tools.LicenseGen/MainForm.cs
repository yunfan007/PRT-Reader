using System.Text;

namespace Prt.Tools.LicenseGen;

/// <summary>
/// 激活码生成器主窗口：签发 / 校验 / 生成密钥对三个功能区。
/// </summary>
internal sealed class MainForm : Form
{
    private readonly TextBox _nameBox = new();
    private readonly TextBox _emailBox = new();
    private readonly ComboBox _levelBox = new();
    private readonly NumericUpDown _daysBox = new();
    private readonly CheckBox _perpetualBox = new();
    private readonly NumericUpDown _seatsBox = new();
    private readonly TextBox _issuerBox = new();
    private readonly TextBox _reasonBox = new();
    private readonly TextBox _privateKeyBox = new();
    private readonly TextBox _codeBox = new();
    private readonly TextBox _publicKeyBox = new();
    private readonly TextBox _outputBox = new();

    public MainForm()
    {
        Text = "PRT 激活码生成器";
        Font = new Font("Microsoft YaHei UI", 9F);
        ClientSize = new Size(640, 648);
        MinimumSize = new Size(656, 690);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;

        // ── 签发区 ──
        var issueGroup = new GroupBox
        {
            Text = "签发激活码",
            Location = new Point(12, 12),
            Size = new Size(616, 240),
        };

        AddLabel(issueGroup, "用户名", 12, 33);
        _nameBox.SetBounds(92, 30, 226, 23);
        issueGroup.Controls.Add(_nameBox);

        AddLabel(issueGroup, "邮箱", 334, 33);
        _emailBox.SetBounds(398, 30, 202, 23);
        issueGroup.Controls.Add(_emailBox);

        AddLabel(issueGroup, "等级", 12, 63);
        _levelBox.SetBounds(92, 60, 120, 23);
        _levelBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _levelBox.Items.AddRange(LicenseCore.ValidLevels);
        _levelBox.SelectedIndex = 2;
        issueGroup.Controls.Add(_levelBox);

        AddLabel(issueGroup, "有效期（天）", 228, 63);
        _daysBox.SetBounds(322, 60, 76, 23);
        _daysBox.Minimum = 1;
        _daysBox.Maximum = 36500;
        _daysBox.Value = 365;
        issueGroup.Controls.Add(_daysBox);

        _perpetualBox.SetBounds(416, 61, 130, 23);
        _perpetualBox.Text = "永久有效";
        _perpetualBox.CheckedChanged += (_, _) => _daysBox.Enabled = !_perpetualBox.Checked;
        issueGroup.Controls.Add(_perpetualBox);

        // 新增：授权量（可激活设备数）与签发人 —— 便于追溯与限制使用范围。
        AddLabel(issueGroup, "授权量（台）", 12, 93);
        _seatsBox.SetBounds(92, 90, 76, 23);
        _seatsBox.Minimum = 1;
        _seatsBox.Maximum = 10000;
        _seatsBox.Value = 1;
        issueGroup.Controls.Add(_seatsBox);

        AddLabel(issueGroup, "签发人", 180, 93);
        _issuerBox.SetBounds(258, 90, 342, 23);
        _issuerBox.PlaceholderText = "谁签的这张码（必填，便于追溯）";
        issueGroup.Controls.Add(_issuerBox);

        AddLabel(issueGroup, "签发理由", 12, 123);
        _reasonBox.SetBounds(92, 120, 508, 23);
        _reasonBox.PlaceholderText = "可留空，例如「老用户补偿」「内部测试」";
        issueGroup.Controls.Add(_reasonBox);

        AddLabel(issueGroup, "私钥路径", 12, 154);
        _privateKeyBox.SetBounds(92, 151, 436, 23);
        issueGroup.Controls.Add(_privateKeyBox);

        var browseKeyButton = new Button
        {
            Text = "浏览…",
            Location = new Point(534, 150),
            Size = new Size(66, 25),
        };
        browseKeyButton.Click += OnBrowsePrivateKey;
        issueGroup.Controls.Add(browseKeyButton);

        issueGroup.Controls.Add(new Label
        {
            Text = "私钥留空则按「当前目录 → 程序目录 → 源码树 keys 目录」自动查找。",
            Location = new Point(92, 179),
            Size = new Size(500, 16),
            ForeColor = SystemColors.GrayText,
        });

        var issueButton = new Button
        {
            Text = "签发激活码",
            Location = new Point(92, 201),
            Size = new Size(120, 28),
        };
        issueButton.Click += OnIssueClick;
        issueGroup.Controls.Add(issueButton);

        // ── 校验区 ──
        var verifyGroup = new GroupBox
        {
            Text = "校验激活码",
            Location = new Point(12, 258),
            Size = new Size(616, 130),
        };

        AddLabel(verifyGroup, "激活码", 12, 33);
        _codeBox.SetBounds(92, 30, 436, 23);
        verifyGroup.Controls.Add(_codeBox);

        var verifyRunButton = new Button
        {
            Text = "校验",
            Location = new Point(534, 29),
            Size = new Size(66, 25),
        };
        verifyRunButton.Click += OnVerifyClick;
        verifyGroup.Controls.Add(verifyRunButton);

        AddLabel(verifyGroup, "公钥（可选）", 12, 63);
        _publicKeyBox.SetBounds(92, 60, 436, 23);
        verifyGroup.Controls.Add(_publicKeyBox);

        var browsePublicKeyButton = new Button
        {
            Text = "浏览…",
            Location = new Point(534, 59),
            Size = new Size(66, 25),
        };
        browsePublicKeyButton.Click += OnBrowsePublicKey;
        verifyGroup.Controls.Add(browsePublicKeyButton);

        verifyGroup.Controls.Add(new Label
        {
            Text = "公钥留空则用与编辑器内嵌一致的默认公钥。",
            Location = new Point(92, 89),
            Size = new Size(400, 16),
            ForeColor = SystemColors.GrayText,
        });

        // ── 密钥区 ──
        var keyGroup = new GroupBox
        {
            Text = "密钥对",
            Location = new Point(12, 396),
            Size = new Size(616, 72),
        };

        var keygenButton = new Button
        {
            Text = "生成新密钥对…",
            Location = new Point(16, 28),
            Size = new Size(130, 28),
        };
        keygenButton.Click += OnKeygenClick;
        keyGroup.Controls.Add(keygenButton);

        keyGroup.Controls.Add(new Label
        {
            Text = "私钥务必保密（丢失则无法再签发）；新公钥需内嵌进编辑器并重新编译，\r\n编辑器才会接受新签发的激活码。",
            Location = new Point(160, 22),
            Size = new Size(446, 40),
            ForeColor = SystemColors.GrayText,
        });

        // ── 输出区 ──
        _outputBox.SetBounds(12, 476, 616, 122);
        _outputBox.Multiline = true;
        _outputBox.ReadOnly = true;
        _outputBox.ScrollBars = ScrollBars.Vertical;
        _outputBox.WordWrap = false;
        _outputBox.BackColor = SystemColors.Window;
        _outputBox.Font = new Font("Consolas", 9F);

        var copyButton = new Button
        {
            Text = "复制激活码",
            Location = new Point(12, 606),
            Size = new Size(110, 26),
        };
        copyButton.Click += OnCopyClick;

        Controls.Add(issueGroup);
        Controls.Add(verifyGroup);
        Controls.Add(keyGroup);
        Controls.Add(_outputBox);
        Controls.Add(copyButton);
    }

    private static void AddLabel(Control parent, string text, int x, int y)
    {
        parent.Controls.Add(new Label
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(76, 18),
            TextAlign = ContentAlignment.MiddleRight,
        });
    }

    // ─────────────────────────────── 事件 ───────────────────────────────

    private void OnBrowsePrivateKey(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择私钥文件",
            Filter = "私钥 XML (*.xml)|*.xml|全部文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _privateKeyBox.Text = dialog.FileName;
        }
    }

    private void OnBrowsePublicKey(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择公钥文件（留空用默认公钥）",
            Filter = "公钥 XML (*.xml)|*.xml|全部文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _publicKeyBox.Text = dialog.FileName;
        }
    }

    private void OnIssueClick(object? sender, EventArgs e)
    {
        try
        {
            int? days = _perpetualBox.Checked ? null : (int)_daysBox.Value;
            var result = LicenseCore.Generate(
                _nameBox.Text,
                _emailBox.Text,
                _levelBox.SelectedItem?.ToString() ?? string.Empty,
                days,
                _perpetualBox.Checked,
                string.IsNullOrWhiteSpace(_privateKeyBox.Text) ? null : _privateKeyBox.Text.Trim(),
                _issuerBox.Text,
                (int)_seatsBox.Value,
                _reasonBox.Text);

            _outputBox.Text = "激活码（复制整行发给用户）：" + Environment.NewLine
                              + result.Code + Environment.NewLine + Environment.NewLine
                              + "授权明细：" + result.Summary;
            _codeBox.Text = result.Code;
        }
        catch (Exception ex)
        {
            _outputBox.Text = "[错误] " + ex.Message;
        }
    }

    private void OnVerifyClick(object? sender, EventArgs e)
    {
        try
        {
            var result = LicenseCore.Verify(
                _codeBox.Text.Trim(),
                string.IsNullOrWhiteSpace(_publicKeyBox.Text) ? null : _publicKeyBox.Text.Trim());
            _outputBox.Text = (result.Ok ? "[通过] " : "[失败] ") + result.Detail;
        }
        catch (Exception ex)
        {
            _outputBox.Text = "[错误] " + ex.Message;
        }
    }

    private void OnKeygenClick(object? sender, EventArgs e)
    {
        try
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "选择密钥对的保存目录",
                ShowNewFolderButton = true,
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            var privatePath = Path.Combine(dialog.SelectedPath, "license.private.xml");
            if (File.Exists(privatePath)
                && MessageBox.Show(
                    this,
                    privatePath + " 已存在。" + Environment.NewLine
                    + "覆盖后，旧私钥签发的全部激活码将失效。确定覆盖吗？",
                    "确认覆盖",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }

            var result = LicenseCore.Keygen(dialog.SelectedPath, force: true);
            _outputBox.Text = "已生成密钥对：" + Environment.NewLine
                              + "  私钥 " + result.PrivatePath + "（务必保密）" + Environment.NewLine
                              + "  公钥 " + result.PublicPath + "（需内嵌到编辑器后重新编译）";
        }
        catch (Exception ex)
        {
            _outputBox.Text = "[错误] " + ex.Message;
        }
    }

    private void OnCopyClick(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_outputBox.Text))
        {
            return;
        }

        // 输出区首行是说明、第二行是激活码——复制时优先只复制激活码本身。
        var lines = _outputBox.Text.Split('\n');
        var code = lines.Length >= 2 && lines[1].StartsWith(LicenseCore.CodePrefix, StringComparison.Ordinal)
            ? lines[1].Trim()
            : _outputBox.Text;
        Clipboard.SetText(code, TextDataFormat.UnicodeText);
    }
}
