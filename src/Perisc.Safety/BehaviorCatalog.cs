using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Perisc.Safety;

/// <summary>
/// 敏感行为目录（PSS 标准 3.6，规范性）：9 类 63 项，编号不得自造。
/// DefaultDecision 是「未申报/拦截兜底」的默认裁决语义（3.4/3.6）；
/// Ask 始终交由用户决定（3.4 的 ask 语义），目录默认值决定拦截层与文档展示口径。
/// </summary>
public static class BehaviorCatalog
{
    /// <summary>一个行为项的只读描述。</summary>
    public sealed class Item
    {
        public Item(string id, string category, string title, DecisionStatus defaultDecision)
        {
            Id = id;
            Category = category;
            Title = title;
            DefaultDecision = defaultDecision;
        }

        /// <summary>行为编号，如 "FS-02"。</summary>
        public string Id { get; }

        /// <summary>类别码（FS / NET / …）。</summary>
        public string Category { get; }

        /// <summary>条目名称（3.6 原文）。</summary>
        public string Title { get; }

        /// <summary>默认裁决（3.6）：deny＝必须显式申报，ask＝默认交用户决定。</summary>
        public DecisionStatus DefaultDecision { get; }
    }

    private static readonly Dictionary<string, Item> s_items = Build();

    /// <summary>全部 63 项（按 3.6 顺序）。</summary>
    public static IReadOnlyList<Item> All { get; } = new ReadOnlyCollection<Item>(
        new List<Item>(s_items.Values));

    /// <summary>类别码全集（3.1）。</summary>
    public static IReadOnlyList<string> Categories { get; } =
        new[] { "FS", "NET", "PROC", "CFG", "DEV", "CRED", "CODE", "PRIV", "RES" };

    /// <summary>按编号查行为项；不存在返回 null。</summary>
    public static Item? Find(string action) =>
        action is not null && s_items.TryGetValue(action, out var item) ? item : null;

    /// <summary>是否存在该行为编号（大小写敏感，3.2）。</summary>
    public static bool Exists(string action) => action is not null && s_items.ContainsKey(action);

    private static Dictionary<string, Item> Build()
    {
        var list = new List<Item>();

        // ── FS 文件系统（9 项）──
        void Fs(string id, string title) => list.Add(new Item(id, "FS", title, DecisionStatus.Deny));
        Fs("FS-01", "创建文件或目录");
        Fs("FS-02", "写入用户目录（文档、图片、桌面、下载）");
        Fs("FS-03", "写入程序自身目录以外的地方");
        Fs("FS-04", "删除文件或目录");
        Fs("FS-05", "改名或移动");
        Fs("FS-06", "写入可执行文件或脚本");
        Fs("FS-07", "访问用户目录之外（系统目录、其它用户目录、网络位置）");
        Fs("FS-08", "大规模遍历（> 1000 个条目/次）");
        Fs("FS-09", "创建符号链接 / 联接点");

        // ── NET 网络（8 项）──
        void Net(string id, string title) => list.Add(new Item(id, "NET", title, DecisionStatus.Deny));
        Net("NET-01", "建立出站连接");
        Net("NET-02", "监听端口（接受入站连接）");
        Net("NET-03", "上传用户数据");
        Net("NET-04", "检查更新（用户主动触发除外）");
        Net("NET-05", "下载可执行内容");
        Net("NET-06", "使用代理或隧道");
        Net("NET-07", "P2P 连接");
        Net("NET-08", "局域网设备发现（广播/多播）");

        // ── PROC 进程与系统（10 项）──
        void Proc(string id, string title) => list.Add(new Item(id, "PROC", title, DecisionStatus.Deny));
        Proc("PROC-01", "创建子进程");
        Proc("PROC-02", "结束或挂起其它进程");
        Proc("PROC-03", "向其它进程注入或读写其内存");
        Proc("PROC-04", "加载原生库（DLL/so）");
        Proc("PROC-05", "安装或启动系统服务");
        Proc("PROC-06", "创建计划任务或定时唤醒");
        Proc("PROC-07", "设置开机自启");
        Proc("PROC-08", "请求管理员/超级用户权限");
        Proc("PROC-09", "访问驱动或内核接口");
        Proc("PROC-10", "修改其它进程的窗口或输入");

        // ── CFG 配置与注册表（6 项）──
        void Cfg(string id, string title) => list.Add(new Item(id, "CFG", title, DecisionStatus.Deny));
        Cfg("CFG-01", "写入系统配置或注册表");
        Cfg("CFG-02", "修改文件关联或协议处理器");
        Cfg("CFG-03", "持久化环境变量");
        Cfg("CFG-04", "修改电源、显示、输入法等系统设置");
        Cfg("CFG-05", "注册卸载信息或全局钩子");
        Cfg("CFG-06", "修改其它程序的配置");

        // ── DEV 外设与媒体（8 项）──
        void Dev(string id, string title) => list.Add(new Item(id, "DEV", title, DecisionStatus.Deny));
        Dev("DEV-01", "打开摄像头");
        Dev("DEV-02", "打开麦克风");
        Dev("DEV-03", "屏幕捕获或截屏");
        Dev("DEV-04", "读取剪贴板");
        Dev("DEV-05", "写入剪贴板");
        Dev("DEV-06", "访问 USB / 串口 / 并口设备");
        Dev("DEV-07", "使用打印机");
        Dev("DEV-08", "获取地理位置");

        // ── CRED 凭据与密钥（7 项）──
        void Cred(string id, string title) => list.Add(new Item(id, "CRED", title, DecisionStatus.Deny));
        Cred("CRED-01", "读取系统凭据存储");
        Cred("CRED-02", "读取私钥文件");
        Cred("CRED-03", "生成或导出私钥");
        Cred("CRED-04", "采集用户口令（输入框）");
        Cred("CRED-05", "加密或解密用户数据");
        Cred("CRED-06", "读取浏览器或其它程序的凭据");
        Cred("CRED-07", "使用平台密钥链/证书存储");

        // ── CODE 代码执行与反射（7 项）──
        void Code(string id, string title) => list.Add(new Item(id, "CODE", title, DecisionStatus.Deny));
        Code("CODE-01", "动态求值或编译（eval、脚本、表达式树）");
        Code("CODE-02", "脚本注入宿主环境");
        Code("CODE-03", "反射调用非公开成员");
        Code("CODE-04", "反序列化不受信数据为可执行对象");
        Code("CODE-05", "加载第三方插件或模块");
        Code("CODE-06", "运行随包携带的脚本");
        Code("CODE-07", "修改自身可执行文件或内存映像");

        // ── PRIV 用户数据与隐私（6 项）──
        void Priv(string id, string title) => list.Add(new Item(id, "PRIV", title, DecisionStatus.Deny));
        Priv("PRIV-01", "读取文档库/图片库/音乐库");
        Priv("PRIV-02", "读取通讯录或日历");
        Priv("PRIV-03", "读取浏览器历史或书签");
        Priv("PRIV-04", "上报使用统计或遥测");
        Priv("PRIV-05", "上报崩溃信息（含数据）");
        Priv("PRIV-06", "读取其它程序的数据目录");

        // ── RES 资源占用（2 项，默认 ask）──
        list.Add(new Item("RES-01", "RES", "长时间后台常驻（无界面运行 > 5 分钟）", DecisionStatus.Ask));
        list.Add(new Item("RES-02", "RES", "密集计算占用（持续 > 80% 单核 > 30 秒）", DecisionStatus.Ask));

        var map = new Dictionary<string, Item>(list.Count, System.StringComparer.Ordinal);
        foreach (var item in list)
        {
            map.Add(item.Id, item);
        }
        return map;
    }
}
