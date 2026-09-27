using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shell;
using Perisc.Safety;
using Prt.App.Models;
using Prt.App.Rendering;
using Prt.App.Views;
using Prt.Core;
using Prt.Core.Diagnostics;
using Prt.Core.Export;
using Prt.Core.Rendering;
using Prt.Core.Syntax;

namespace Prt.App.Services;

/// <summary>自检用例组：激活码 / 设置项收敛（<see cref="SelfTest"/> 的 partial）。</summary>
internal static partial class SelfTest
{
    /// <summary>自检用例组：激活码 / 设置项收敛。</summary>
    private static void RunActivationAndSettingsCases(CaseRunner runner)
    {
        // 11. 编辑器外壳与右键菜单：模板部件必须真实实例化（避免样式缺陷导致编辑器空白 / 无滚动条）。
        runner.Check("编辑器外壳与右键菜单可实例化", () => ActivationAndSettingsCase11());

        // 12. 激活码前缀兼容：三代前缀（PRTL1. 当前 / PSRL1. PSR 时代 / MDPL1. MDP 时代）都必须被识别。
        runner.Check("激活码前缀兼容（PSRL1. 与 MDPL1. 旧码仍被接受）", () => ActivationAndSettingsCase12());

        // 13. 激活码字段扩展：9 段（含签发时间 / 授权量 / 签发人 / 签发理由）与 5 段旧码都要能解析。
        runner.Check("激活码字段扩展（9 段新码与 5 段旧码均可解析）", () => ActivationAndSettingsCase13());

        // 14. 真实签发激活码全链路：base64url 解码 → RSA 签名验证 → 载荷解析，一步都不能少。
        //     回归点——Base64UrlDecode 的补位条件曾写反，凡载荷字节数 %3==2（base64url 段
        //     长度 %4==3）的激活码一律解码失败，生成器自签自验不过、阅读器无法激活；
        //     而用例 12 / 13 都不经过真码解码，恰好漏测。此夹具为真私钥签发的永久码。
        runner.Check("真实签发激活码可全链路验证（载荷 %3==2 形态）", () => ActivationAndSettingsCase14());

        // 45. 便携落点：设置与会话只认程序所在文件夹，不写用户配置目录。
        //     回归点——落点曾是"先程序同级、后 %APPDATA% 回退"，于是装在只读目录时
        //     配置会悄悄跑到用户目录，同机多份副本互相串扰、整目录拷走也带不走设置。
        runner.Check("界面设置与会话为纯便携（落点只在程序目录）", () => ActivationAndSettingsCase45());

        // 46. 旧位置迁移：写后校验一致才删旧文件；程序目录已有同名文件则跳过、且不删旧的。
        //     回归点——迁移最危险的形态是"删了又没搬过去"，故本条把删除的前提钉死在校验之上。
        runner.Check("旧位置配置一次性迁移（校验通过才删旧文件）", () => ActivationAndSettingsCase46());

        // 47. 落点不可写时必须给出可读原因，而不是静默换地方写。
        runner.Check("程序目录不可写时给出可读原因且不改写别处", () => ActivationAndSettingsCase47());
        runner.Check("配置类读写不经安全模块申报（启动期无人答复时仍可用）", () => ActivationAndSettingsCase48());
    }

    /// <summary>用例 11：编辑器外壳与右键菜单可实例化</summary>
    private static string ActivationAndSettingsCase11()
    {
            var view = new DocumentView(new DocumentTab { Text = "示例内容" });
            var editor = view.Editor;
            editor.ApplyTemplate();

            var contentHost = editor.Template?.FindName("PART_ContentHost", editor) as ScrollViewer;
            if (contentHost is null)
            {
                throw new InvalidOperationException("编辑器模板缺少 PART_ContentHost");
            }

            if (contentHost.VerticalScrollBarVisibility != ScrollBarVisibility.Auto)
            {
                throw new InvalidOperationException(
                    $"编辑器滚动条可见性未从控件传递到模板（实际 {contentHost.VerticalScrollBarVisibility}）");
            }

            var menu = editor.ContextMenu;
            if (menu is null)
            {
                throw new InvalidOperationException("编辑器未挂载右键菜单");
            }

            var insertRoot = menu.Items.OfType<MenuItem>()
                .FirstOrDefault(item => item.Header as string == "插入框");
            if (insertRoot is null)
            {
                throw new InvalidOperationException("右键菜单缺少「插入框」分组");
            }

            var groups = insertRoot.Items.OfType<MenuItem>().ToList();
            var semantic = groups.FirstOrDefault(item => (item.Header as string)?.StartsWith("语义框", StringComparison.Ordinal) == true);
            if (semantic is null || semantic.Items.Count != PrtSnippets.InGroup(PrtSnippets.GroupSemantic).Count)
            {
                throw new InvalidOperationException("「语义框」子菜单条目数与片段目录不一致");
            }

            if (groups.Count != 3)
            {
                throw new InvalidOperationException($"「插入框」应有 3 个子分组，实际 {groups.Count} 个");
            }

            return $"编辑器滚动条 Auto，右键菜单含 {groups.Count} 个分组，" +
                   $"语义框 {semantic.Items.Count} 项";
    }

    /// <summary>用例 12：激活码前缀兼容（PSRL1. 与 MDPL1. 旧码仍被接受）</summary>
    private static string ActivationAndSettingsCase12()
    {
            // 内容为 base64url 的「abc」，签名必然验不过——但不应停在「格式不正确」这一步。
            foreach (var prefix in new[] { "PRTL1.", "PSRL1.", "MDPL1." })
            {
                var accepted = Activation.TryVerify(prefix + "YWJj.YWJj", out _, out var error);
                if (!accepted && error.Contains("格式不正确", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"前缀 {prefix} 被判为格式错误：" + error);
                }
            }

            return "三代前缀均通过格式识别（PRTL1. / PSRL1. / MDPL1.）";
    }

    /// <summary>用例 13：激活码字段扩展（9 段新码与 5 段旧码均可解析）</summary>
    private static string ActivationAndSettingsCase13()
    {
            const string Extended =
                "PRTA1|张三|zhang@example.com|Standard|PERPETUAL|2026-09-18T21:30:00|5|俱乐部文档化工作部|老用户补偿";
            if (!Activation.TryParsePayload(Extended, "CODE", out var modern, out var modernError))
            {
                throw new InvalidOperationException("9 段载荷解析失败：" + modernError);
            }

            if (modern.Seats != 5
                || modern.IssuedBy != "俱乐部文档化工作部"
                || modern.Reason != "老用户补偿"
                || modern.IssuedAt != "2026-09-18T21:30:00")
            {
                throw new InvalidOperationException(
                    $"新增字段解析不正确：签发时间={modern.IssuedAt} 授权量={modern.Seats} 签发人={modern.IssuedBy} 理由={modern.Reason}");
            }

            const string Legacy = "PSRA1|李四|li@example.com|Professional|PERPETUAL";
            if (!Activation.TryParsePayload(Legacy, "CODE", out var old, out var legacyError))
            {
                throw new InvalidOperationException("5 段旧载荷解析失败：" + legacyError);
            }

            if (old.HasIssuerInfo || old.Seats is not null)
            {
                throw new InvalidOperationException("旧版 5 段载荷不应带出签发信息");
            }

            // 其它字段数必须被拒绝：否则将来误加字段会悄悄按旧布局解析。
            if (Activation.TryParsePayload("PRTA1|a|b@c.d|Standard|PERPETUAL|extra", "CODE", out _, out _))
            {
                throw new InvalidOperationException("6 段载荷不应通过解析");
            }

            return $"新码 {modern.Seats} 台设备 · 签发人 {modern.IssuedBy}；旧码兼容（授权量未声明）";
    }

    /// <summary>用例 14：真实签发激活码可全链路验证（载荷 %3==2 形态）</summary>
    private static string ActivationAndSettingsCase14()
    {
            // 真私钥签发的永久码；载荷段 base64url 长度 %4==3——补位 bug 的必败形态。
            // 夹具内容：PRTA1|自检回归用户|selftest@example.com|Professional|PERPETUAL|
            //           2026-09-25T15:15:00|2|俱乐部文档化工作部|解析链路回归
            const string Fixture =
                "PRTL1.UFJUQTF86Ieq5qOA5Zue5b2S55So5oi3fHNlbGZ0ZXN0QGV4YW1wbGUuY29tfFByb2Zlc3Npb25hbHxQRVJQRVRVQUx8MjAyNi0wOS0yNVQxNToxNTowMHwyfOS_seS5kOmDqOaWh-aho-WMluW3peS9nOmDqHzop6PmnpDpk77ot6_lm57lvZI.mc1W7s4UQ-q1oxGYRQ2zW_wOeRXQc3unNLjBZm_XkKEB3W_IwRQ55medlhPB81uY2QhawSw5G_X6W3j0kFgbyjTU77-wz-O8dGvPyKCqwwXE0e2_GTAUvmYN5Z5RwuuEkW-ol0n-GdN8puS1WM8yFYBSHFfd_TLfH9l1KeKUejhy9mqPpFEV34Q7jdoV8EpKKxO_ObaaTE-YGnKcXvjSO4bXJG2v6UIY4xaRELvWBph0l1AryMouKDkRnbRhEs1vyZolZ4LPohbWuTMQOobdmBtSAINsBpcWfJsgcGnm6ykZA6hI3NsutxFFTjPiFHDSWEbSVpXBR4EbiPRLaF-Fzw";

            if (!Activation.TryVerify(Fixture, out var license, out var error))
            {
                throw new InvalidOperationException("真实签发激活码验证失败：" + error);
            }

            if (license.Username != "自检回归用户"
                || license.Level != LicenseLevel.Professional
                || !license.IsPerpetual
                || license.Seats != 2
                || license.IssuedBy != "俱乐部文档化工作部"
                || license.Reason != "解析链路回归")
            {
                throw new InvalidOperationException(
                    $"夹具字段不符：用户={license.Username} 等级={license.Level} 永久={license.IsPerpetual} " +
                    $"授权量={license.Seats} 签发人={license.IssuedBy} 理由={license.Reason}");
            }

            return $"签名与载荷全部还原（{license.Username} · 永久 · {license.Seats} 台设备）";
    }

    /// <summary>用例 45：界面设置与会话的落点＝程序所在文件夹（纯便携，不再回退 %APPDATA%）</summary>
    private static string ActivationAndSettingsCase45()
    {
        var baseDir = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        foreach (var path in new[] { SettingsStore.StoragePath, SessionService.StoragePath })
        {
            var full = Path.GetFullPath(path);
            if (!string.Equals(Path.GetDirectoryName(full), baseDir, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"配置落点不在程序同级目录：{full}");
            }

            if (!string.IsNullOrEmpty(appData)
                && full.StartsWith(Path.GetFullPath(appData), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("配置落点仍指向 %APPDATA%（同机副本会互相串扰）：" + full);
            }
        }

        // 旧位置只允许作为"首启迁移的来源"出现，不得成为运行期的读写候选。
        foreach (var legacy in LegacyConfigMigration.LegacyDirectoryPaths())
        {
            if (string.Equals(Path.GetFullPath(legacy), baseDir, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("旧位置枚举混入了程序目录：" + legacy);
            }
        }

        return $"设置 {Path.GetFileName(SettingsStore.StoragePath)}、"
             + $"会话 {Path.GetFileName(SessionService.StoragePath)} 均在程序目录";
    }

    /// <summary>
    /// 用例 46：旧位置配置一次性迁移（校验通过才删旧文件；已有同名文件则跳过且不删）。
    /// 启动图以**真二进制**夹具验证「逐字节保留」——文本夹具测不出「被当文本搬」的损坏。
    /// </summary>
    private static string ActivationAndSettingsCase46()
    {
        var root = Path.Combine(Path.GetTempPath(), "prt-selftest-portable-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "app");
        var legacy = Path.Combine(root, "legacy");
        var legacyResources = Path.Combine(legacy, "resources");

        try
        {
            Fixture(SafeRuntime.File.CreateDirectory(target, "创建自检目标目录"));
            Fixture(SafeRuntime.File.CreateDirectory(legacyResources, "创建自检旧位置目录"));

            const string SettingsJson = "{\"Zoom\":1.5}";
            const string SessionJson = "{\"Paths\":[],\"ActiveIndex\":0}";
            Fixture(SafeRuntime.File.WriteText(Path.Combine(legacy, "settings.json"), SettingsJson, "预置旧位置设置"));
            Fixture(SafeRuntime.File.WriteText(Path.Combine(legacy, "session.json"), SessionJson, "预置旧位置会话"));

            // 启动图夹具必须是**真二进制**（JPEG 魔数 + 无效 UTF-8 字节）。拿文本内容当图片夹具测不出
            // 「二进制被当文本搬」的缺陷——2026-09-26 的实测缺陷（JPEG 的 FF D8 被写成 EF BF BD）
            // 正是这样从用例 46 手下滑过去的。夹具用裸 BCL：它是布景，不是被测对象。
            var splashBytes = new byte[]
            {
                0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0xC0, 0xFF, 0xFE, 0x80,
            };
            System.IO.File.WriteAllBytes(Path.Combine(legacyResources, "splash.png"), splashBytes);

            var summary = LegacyConfigMigration.Migrate(target, [legacy]);
            if (summary.Failed != 0)
            {
                throw new InvalidOperationException("迁移报告失败：" + string.Join(" / ", summary.Notes));
            }

            if (summary.Migrated != 3 || summary.Skipped != 0)
            {
                throw new InvalidOperationException(
                    $"迁移条目数应为「迁移 3 / 跳过 0」，实际「迁移 {summary.Migrated} / 跳过 {summary.Skipped}」");
            }

            if (!PortableStorage.TryReadText(Path.Combine(target, "settings.json"), out var moved)
                || !string.Equals(moved, SettingsJson, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("迁移后的 settings.json 内容与旧位置不一致");
            }

            if (!PortableStorage.TryReadText(Path.Combine(target, "session.json"), out var movedSession)
                || !string.Equals(movedSession, SessionJson, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("迁移后的 session.json 内容与旧位置不一致");
            }

            var migratedSplash = Path.Combine(target, "resources", "splash.png");
            if (!PortableStorage.Exists(migratedSplash, out _))
            {
                throw new InvalidOperationException("启动图未迁移到程序目录的 resources 下");
            }

            // 二进制必须**逐字节**保留：这条断言是本用例的核心价值所在，
            // 缺了它，「图片被当文本搬」的损坏就测不出来（见夹具处的说明）。
            if (!System.IO.File.ReadAllBytes(migratedSplash).AsSpan().SequenceEqual(splashBytes))
            {
                throw new InvalidOperationException(
                    "迁移后的启动图与旧位置字节不一致——二进制文件被当文本读写写坏了。");
            }

            foreach (var source in new[]
                     {
                         Path.Combine(legacy, "settings.json"),
                         Path.Combine(legacy, "session.json"),
                         Path.Combine(legacyResources, "splash.png"),
                     })
            {
                if (PortableStorage.Exists(source, out _))
                {
                    throw new InvalidOperationException("迁移成功后旧文件仍存在：" + source);
                }
            }

            // 幂等：程序目录已有同名文件时不得再迁，更不得删掉旧文件。
            var second = Path.Combine(root, "legacy2");
            Fixture(SafeRuntime.File.CreateDirectory(second, "创建第二处自检旧位置"));
            Fixture(SafeRuntime.File.WriteText(Path.Combine(second, "settings.json"), "{\"Zoom\":0.5}", "预置第二处旧位置设置"));

            var again = LegacyConfigMigration.Migrate(target, [second]);
            if (again.Migrated != 0 || again.Skipped == 0)
            {
                throw new InvalidOperationException(
                    $"程序目录已有同名文件时不应迁移：迁移 {again.Migrated} / 跳过 {again.Skipped}");
            }

            if (!PortableStorage.Exists(Path.Combine(second, "settings.json"), out _))
            {
                throw new InvalidOperationException("跳过迁移时不得删除旧文件（第二处 settings.json 已丢失）");
            }

            return "迁移 3 项（设置 / 会话 / 启动图逐字节保留）后旧文件已删；复迁按跳过处理且不改动现有配置";
        }
        finally
        {
            DeleteTreeQuietly(root);
        }
    }

    /// <summary>用例 47：程序目录不可写时给出可读原因，且不把配置改写别处</summary>
    private static string ActivationAndSettingsCase47()
    {
        var root = Path.Combine(Path.GetTempPath(), "prt-selftest-portable-" + Guid.NewGuid().ToString("N"));
        var obstacle = Path.Combine(root, "obstacle");

        try
        {
            Fixture(SafeRuntime.File.CreateDirectory(root, "创建自检临时目录"));

            // 拿一个「文件」充当目录分量：写入必然失败，等价于程序目录不可写时的结果语义。
            Fixture(SafeRuntime.File.WriteText(obstacle, "not a directory", "造一个必然失败的落点"));

            var ok = PortableStorage.TryWriteTextTo(
                Path.Combine(obstacle, "settings.json"), "{}", "自检：必然失败的写入", out var error);

            if (ok)
            {
                throw new InvalidOperationException("向必然失败的落点写入竟然报告成功");
            }

            if (!error.Contains(PortableStorage.NotWritableHint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("失败原因未含可读提示，用户将无从判断该怎么处理：" + error);
            }

            return "写入失败返回 false，原因含「程序所在文件夹不可写」提示";
        }
        finally
        {
            DeleteTreeQuietly(root);
        }
    }

    /// <summary>
    /// 用例 48：启动期用到的配置类读写不依赖安全模块的申报（回归 2026-09-26 的实测缺陷）。
    /// <para>
    /// 复现方式：把申报答复切成「无人答复」——这正是启动期 <c>SafetyBridge.Initialize</c> 尚未执行时
    /// SRT 自建客户端的处境。此时凡经 SRT 的动作一律被拒；而界面设置、会话、异常日志与旧配置迁移
    /// <b>必须照常可用</b>，因为它们按 PSS 4.6 不属 63 项内的敏感动作、本就不该申报
    /// （理由与边界见 <see cref="PortableStorage"/> 类注释）。
    /// </para>
    /// <para>
    /// 对照必须是<b>全新的对象</b>：命中过许可缓存的行为不会再走申报，拿旧对象做对照测不出拒绝。
    /// </para>
    /// </summary>
    private static string ActivationAndSettingsCase48()
    {
        var probe = Path.Combine(
            Path.GetTempPath(), "prt-selftest-noanswer-" + Guid.NewGuid().ToString("N") + ".txt");
        var root = Path.Combine(Path.GetTempPath(), "prt-selftest-portable-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "app");
        var legacy = Path.Combine(root, "legacy");
        var logProbe = PortableStorage.FilePath("prt-selftest-probe.log");

        SafetyBridge.UseNoAnswerHandlerForSelfTest();
        try
        {
            // ① 前提成立性：无人答复时，经 SRT 的动作确实被拒——否则这条用例什么也证明不了。
            var blocked = SafeRuntime.File.WriteText(probe, "x", "自检对照：无人答复时应被拒绝");
            if (blocked.Executed)
            {
                throw new InvalidOperationException("对照不成立：无人答复时 SRT 竟执行了写入（" + probe + "）");
            }

            // ② 界面设置：写入并读回。
            if (!SettingsStore.TrySave(out var settingsError))
            {
                throw new InvalidOperationException("无人答复时界面设置写不进（这条路径竟走了申报）：" + settingsError);
            }

            if (!PortableStorage.TryReadText(SettingsStore.StoragePath, out var settingsJson) || settingsJson is null)
            {
                throw new InvalidOperationException("无人答复时界面设置读不回来：" + SettingsStore.StoragePath);
            }

            // ③ 会话：同上（授权的读写在 Activation / DeviceActivation，与这两处共用同一落点助手）。
            if (!SessionService.TrySave([], 0, out var sessionError))
            {
                throw new InvalidOperationException("无人答复时会话写不进（这条路径竟走了申报）：" + sessionError);
            }

            if (!PortableStorage.TryReadText(SessionService.StoragePath, out var sessionJson) || sessionJson is null)
            {
                throw new InvalidOperationException("无人答复时会话读不回来：" + SessionService.StoragePath);
            }

            // ④ 异常日志：同一落点助手的追加通道。
            if (!PortableStorage.TryAppendText(
                    logProbe, "self-test probe" + Environment.NewLine, "自检异常日志探针", out var logError))
            {
                throw new InvalidOperationException("无人答复时异常日志写不进：" + logError);
            }

            // ⑤ 旧配置迁移：读旧位置 → 写程序目录 → 校验 → 删旧文件，四类动作整条链路都不经申报。
            // 夹具**不能用 Fixture（它经 SRT）**：本用例的前提就是此刻 SRT 被拒，经它铺夹具必然失败。
            // 故这几行直接调 BCL——它们是布景，不是被测对象。
            System.IO.Directory.CreateDirectory(target);
            System.IO.Directory.CreateDirectory(legacy);
            System.IO.File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"Zoom\":1.25}");
            System.IO.File.WriteAllText(Path.Combine(legacy, "session.json"), "{\"Paths\":[],\"ActiveIndex\":0}");

            var summary = LegacyConfigMigration.Migrate(target, [legacy]);
            if (summary.Migrated != 2 || summary.Failed != 0)
            {
                throw new InvalidOperationException(
                    "无人答复时旧配置迁移失败（这条路径竟走了申报）：迁移 " + summary.Migrated
                    + " / 失败 " + summary.Failed + " —— " + string.Join(" / ", summary.Notes));
            }

            return "无人答复时设置 / 会话 / 异常日志 / 旧配置迁移全部照常（对照：经 SRT 的写入被拒）";
        }
        finally
        {
            SafetyBridge.UseAutoAllowHandlerForSelfTest();
            DeleteTreeQuietly(root);
            PortableStorage.TryDelete(logProbe, "自检清理临时日志探针", out _);
        }
    }
}
