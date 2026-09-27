# PRT 阅读器（Perisc Standard Richtext 解析 / 查看 / 编辑）

> 编写：软体程式部　　评议通过：专家委员会

面向 **PRT（Perisc Standard Richtext）** 的解析、查看与编辑工具。核心为与界面无关的类库，
界面为 **WPF 原生实现**，不使用 WebView / WebBrowser / 任何浏览器内核。

- **实现范围**：**CORE 与 EXT** 等级（声明的规范基准版本为 v2.1，见下）
- **明确不支持**：**COMP（受限计算）**。`{{ 表达式 }}`、`::: set/if/loop/run`、`@日期`
  等 COMP 结构一律按《PRT 标准》第 13 章降级处理（插值输出原文、计算块保留源码），
  既不作任何求值，也不丢失内容。代码中以 `PrtCapabilities.SupportsComputation = false`
  与显式中文注释标注该边界。
- **安全接入**：按《软体程式认证与安全化规范》（PSS，`CSRO-CRS-26001`）**v2.0** 以
  **接入模式 A（原生全接入）** 接入四层安全模块。主程序编译为 **DLL**，
  **唯一启动入口是宿主启动器** `Perisc.Safety.SafeGuard.exe`——**双击它即可运行**，不需要参数
  （启动器在自身目录内推断主程序；它是 GUI 子系统程序，**双击时不会出现控制台窗口**，同时显示一个「安全系统启动中」的
  图形界面交代真实启动阶段——**至少显示 800 ms**，显示期间主程序尚未启动，满 800 ms 后才拉起，
  界面随主程序接入淡出）；一切敏感行为收口到
  模块四 SRT（`SafeRuntime` 类型族）。行为清单见 [docs/行为清单.md](docs/行为清单.md)。

> **已知滞后**：仓库内发布的《PRT 标准》已是 **v4.0**（计算部分改为「唯一计算原则」），
> 而本工具声明的基准版本仍是 **v2.1**；对照 v4.0 的逐项重新核定尚未完成，
> 详见 [docs/符合性声明.md](docs/符合性声明.md) 第一节与
> [docs/部署文档.md](docs/部署文档.md) 第十节（遗留事项）。

---

## 一、工程结构

```
Prt/
├─ Prt.sln                     解决方案（6 个工程，另含 src / docs 两个解决方案文件夹）
├─ build.ps1                   构建脚本（全流程，带开关）
├─ build.cmd                   构建脚本的命令行入口
├─ samples/                    示例文档（sample.prt，随程序分发）
├─ tools/                      自查工具（零外部依赖，随仓库走）
│   └─ selfscore.py            CRS 9.6 自评生成器 + 硬门禁（产出 docs/自评报告.md）
├─ libs/                       随仓库分发的构建期依赖（不参与运行时分发）
│   └─ packs/                  目标包与 apphost（约 94 MB），使任意 .NET SDK 都能离线构建
│                              ——结构见「离线编译」一节，勿改目录名与版本号目录名
├─ docs/                       交付文档
│   ├─ 使用指南.md
│   ├─ 部署文档.md
│   ├─ 系统适配说明.md
│   ├─ 符合性声明.md
│   ├─ 行为清单.md
│   ├─ 工程改进方案.md        改造依据登记处（§4.8 文件拆分、§4.14 丈量口径）
│   ├─ 设计取舍.md            设计取舍与已知妥协（源码注释的引用目标）
│   ├─ 分析器门禁.md          分析器规则三档色阶与基线（.editorconfig 的同源文档）
│   ├─ 质量检查表.md          复核命令 / 人工打勾项 / 已知例外 / 改造验收门
│   ├─ 虚拟化改造方案.md
│   └─ 自评报告.md
└─ src/
    ├─ Prt.Core/               类库（net8.0，无界面依赖）
    ├─ Prt.App/                桌面应用（net8.0-windows，WPF 原生；UseAppHost=false → 产出 DLL）
    ├─ Perisc.Safety/          安全模块一/二/四（PSS 标准；含 SRT 运行时）
    ├─ Perisc.Safety.SafeGuard/  安全模块三：宿主启动器（程序的唯一入口；
    │                           net8.0-windows，含双击时的「安全系统启动中」原生 WPF 启动窗口）
    ├─ Prt.Tools.LicenseGen/   离线激活码签发端（俱乐部侧使用，**不随安装包分发**）
    ├─ Prt.Prta/               PRTA → JavaScript 编译器与图形前端（合一工程；源码保留
    │                           Prt.Prta.Compiler / Prt.Prta.Gui 两个命名空间）
    ├─ Prt.Installer/          自研 WPF 安装器（`build.ps1 -Task installer` 产出
    │                           `publish\installer\PRT-Installer.exe`；阅读器、便携运行时、
    │                           卸载器全部内嵌，发布目录里只应出现这一个 exe）
    └─ Prt.Uninstaller/        卸载器（框架依赖单文件，随装常驻 `<安装目录>\uninstall.exe`）
```

> 两个安装类工程**不进入主发布目录**：`Prt.Prta` 不是运行依赖，两者只随 `-Task installer`
> 产出到 `publish\installer\`；`build.ps1` 收尾时会连它们的 `bin` / `obj` 一起清掉
>（`publish\` 本身永不删）。安装与卸载的口径见[docs/安装说明.md](docs/安装说明.md)。

### Prt.Core（类库）

按《PRT 标准》5.1 的流水线组织，各阶段职责单一、依赖单向（上层依赖下层，无反向依赖）：

| 目录 | 职责 | 主要类型 |
| --- | --- | --- |
| `Text/` | 源文本与行列换算 | `SourceText`、`SourceLine` |
| `Diagnostics/` | 诊断收集、排序去重、错误码 | `Diagnostic`、`DiagnosticBag`、`PrtDiagnosticCodes` |
| `Syntax/` | 语法树节点定义（块 / 行内 / 表格模型） | `PrtBlock`、`PrtInline`、`TableModel`、`IReferenceable` |
| `Parsing/` | 词法与语法解析 | `InlineParser`、`TableParser`、`BlockParser`、`NestingValidator`、`ParsingHelpers` |
| `Structure/` | 结构解析：编号、引用注册表、目录、脚注 | `StructureResolver`、`ReferenceRegistry`、`NumberingFormatter`、`DocumentStructure` |
| `Rendering/` | 主题与命名色调色板 | `PrtTheme`、`ColorPalette`、`SemanticNaming` |
| `Export/` | 三格式导出与降级协议 | `HtmlRenderer`、`MarkdownRenderer`、`PlainTextRenderer` |
| 根 | 对外入口与数据载体 | `PrtParser`、`PrtDocument`、`PrtOptions`、`PrtCapabilities` |

对外入口只有两个：`PrtParser.Parse(text, options)` 与 `PrtDocument`（语法树 + 元数据 +
引用注册表 + 结构结果 + 诊断）。界面层与导出器均只依赖此二者。

### Prt.App（WPF 原生应用）

| 目录 | 职责 |
| --- | --- |
| `Views/` | `MainWindow`（标签页、菜单、目录、诊断、状态栏）、`DocumentView`（单个文档的编辑与预览）、`VirtualPreview`（虚拟化只读预览容器，M2a）、`ActivationWindow`（激活与授权）、`AboutWindow`（关于）、`SettingsWindow`、`PrintPreviewWindow`、`TutorialWindow`、`SplashWindow`、`SecurityCenterWindow`（许可 / 审计 / 行为清单）、`SafetyAskDialog`（授权弹窗） |
| `Rendering/` | `PreviewBuilder`：PRT 语法树 → WPF `FlowDocument` 的原生渲染（打印 / 导出 / 自检快照）；`VirtualPreviewBuilder`：语法树 → 逐块界面元素的虚拟预览渲染（预览区，M2a）；`PreviewStyle`：主题 → 画刷/字体 |
| `Theming/` | `AppPalette`：界面外壳的浅色 / 深色配色；`WinUi3Styles.xaml`：控件样式层；`Brand.xaml`：品牌标矢量资源（与窗口图标同源）；`DarkTitleBar`：系统标题栏明暗同步 |
| `Models/` | `DocumentTab`、`ViewMode`、`DiagnosticRow`、`SampleDocument` |
| `Services/` | `FileService`（读写与编码约定）、`TextNavigation`（逻辑行列换算）、`SelfTest`（启动自检）、`Activation` / `DeviceActivation`（离线授权）、`PortableStorage`（**唯一落点**：程序所在文件夹）、`LegacyConfigMigration`（旧 `%APPDATA%` 配置的一次性迁移）、`SettingsStore` / `SessionService`（设置与会话）、`SafetyBridge` / `SafetyUiHost`（PSS 安全接入桥与界面挂钩） |

关键约束：`Prt.Core` 不引用 `Wpf*` / `System.Windows*`，界面可替换而不影响解析与导出。

**存储策略（纯便携）**：界面设置、会话记录、自定义启动图与异常日志只落**程序所在文件夹**，
不设 `%APPDATA%` 回退——整目录拷走即带走全部配置，同机多份副本互不串扰。程序目录不可写时
明确提示用户，不改写别处。**例外**（各有规范依据）：安全模块的审计（PSS 6.1.6）与宿主启动器的
白名单 / 事件 / 运行期基准快照（PSS 6.2.1，且要求该目录不得放宽为全机可写）仍落在 `%LOCALAPPDATA%`。
反过来，运行体完整性基准 `阅读器\Safe.data` **在**程序目录——它是**发布物**（`build.ps1` 生成、
随包分发），不是运行期数据。
理由与取舍见[设计取舍 第 13 条](docs/设计取舍.md)与[第 14 条](docs/设计取舍.md)。

---

## 二、快速开始

推荐使用构建脚本（自动串联 清理 → 还原 → 严格编译+门禁 → 自检 → 自评 → 发布，可用参数裁剪）：

```powershell
# 全流程：Release 配置 + 框架依赖发布
.\build.ps1

# 仅编译并运行启动自检
.\build.ps1 -Configuration Debug -Task build,selftest

# 只跑门禁与自评（严格编译 + 6 条硬门禁 + CRS 9.6 自评）
.\build.ps1 -Task lint,score

# 生成单文件发布包（框架依赖：依赖全部打进一个 exe，目标机需 .NET 8 桌面运行时）
.\build.ps1 -PublishMode single-file

# 生成自研 WPF 安装器（自包含单文件 exe，自带便携运行时；默认 140 MB）
.\build.ps1 -Task installer -InstallerRuntime portable

# 安装器不带便携运行时（要求目标机已有 .NET 8 桌面运行时，安装包小得多）
.\build.ps1 -Task installer -InstallerRuntime none

# 仅清理构建产物
.\build.ps1 -Task clean
```

可用环节：`clean` / `restore` / `build` / `lint` / `test` / `selftest` / `score` / `publish` /
`installer`。

不指定 `-Task` 即 `all`（默认全流程，其中用 `lint` 取代 `build`，因 `lint` 内部已含严格编译）；
`test` 环节恒跳过——本仓不新建测试工程，测试能力并入启动自检。
`-Task` 是**单个字符串**，多环节要加引号：`-Task 'build,selftest'`。
`-InstallerRuntime` 只有 `portable` / `none` 两个取值，由脚本校验，写错直接报错而不是静默降级。

安装器产物是 `publish\installer\PRT-Installer.exe`——**目录里只应出现这一个 exe**，
它把阅读器、便携运行时与卸载器全部内嵌，因此分发的永远是"安装器自己"，
不存在"素材目录没跟着拷贝"导致的缺文件。用户侧的安装、静默参数与卸载口径见
[docs/安装说明.md](docs/安装说明.md)。

（`build.cmd` 为等价入口，可在 `cmd.exe` 或双击运行。）

也可直接使用 `dotnet` 命令：

```powershell
# 编译整个解决方案
dotnet build Prt.sln

# 启动桌面应用
dotnet run --project src/Prt.App

# 直接打开指定文档
dotnet run --project src/Prt.App -- "samples/sample.prt"
```

自检（不显示窗口，验证解析 → 原生渲染 → 三格式导出 → 安全模块链路共 54 项，结果写入文本报告）。
接入模式 A 下主程序是 DLL，用 dotnet 宿主加载：

```powershell
dotnet exec src\Prt.App\bin\Release\net8.0-windows\Prt.App.dll --selftest 报告.txt
```

或经宿主启动器（发布形态下的正常路径）：

```powershell
Perisc.Safety.SafeGuard.exe -- --selftest 报告.txt
```

自评（按《CRS 标准》9.6 的 32 项扣分项自动丈量，产出**可复算**的自评报告）：

```powershell
.\build.ps1 -Task score          # 报告写入 docs/自评报告.md
```

### 离线编译

本解决方案**零外部 NuGet 包**：六个工程只依赖 .NET 8 SDK 自带的基础类库与
Windows Desktop 目标包（随 VS 2022 的「.NET 桌面开发」工作负载安装），
不引用任何第三方包。因此**在任何一个装了 VS（含该工作负载）的环境中断网也能编译**：

- 仓库根的 `nuget.config` 已 `<clear />` 清空全部包源——`dotnet restore / build`
  不会发起任何网络请求；已用 `-p:RestoreSources=`（禁源断网模拟）实测通过，
  6 个工程 0 警告 0 错误。
- 若未来确需第三方库，请改为 **vendor DLL**：下载 DLL 放入 `libs\` 目录用
  `<Reference><HintPath>` 引用，不要加回远程包源（否则破坏离线编译保证）。

构建前提与发布形态（**这两条是硬约束，改动前请先读 build.ps1 内的注释**）：

- **目标包随仓库分发，任意 .NET SDK 都能构建。** SDK 会按目标框架的最新补丁版本
  索取 `Microsoft.NETCore.App.Ref` 等包（net8.0 目前是 8.0.31），而这个版本只随
  「当时的 SDK」安装——用别的版本 SDK 构建就只能联网下载，或碰运气依赖某台机器
  的个人缓存，于是出现"这台能编、换一台就 NU1101"。
  改法是把这些包按 SDK 的 `packs` 目录结构放进 **`libs/packs/`**（约 94 MB，
  含 `Microsoft.NETCore.App.Ref`、`Microsoft.WindowsDesktop.App.Ref`、
  `Microsoft.AspNetCore.App.Ref`、`Microsoft.NETCore.App.Host.win-x64`，各 8.0.31；
  只保留 `ref` / `data` / `analyzers` / `runtimes`，去掉 nupkg 与许可证），
  再由 `Directory.Build.props` 把 `NetCoreTargetingPackRoot` 指过去。
  已用空 NuGet 缓存（`$env:NUGET_PACKAGES` 指向空目录）实测：编译 0 警告 0 错误、
  自检 44/44、两种发布形态均成功。
  **维护**：微软发布新的 8.0 补丁后，若日志开始索取更高版本号，按同样结构拷一份
  新版本目录进来即可；旧版本留着不冲突。`libs/` 不属中间产物，构建清理不会删它。
- **`single-file` 是框架依赖单文件，不是自包含。** 自包含要把运行时打进产物，
  必须取得 Runtime Pack，而 Runtime Pack 只以 NuGet 包分发，与上述离线约束冲突，
  因此在本仓库不成立（脚本会先检测并给出可读的失败提示，而不是抛一串 NU1101）。
  取而代之的框架依赖单文件：依赖全部打进一个 exe，目标机仍需 .NET 8 桌面运行时。

### 品牌图标

窗口与可执行文件图标取自品牌标源文件 `icon.svg`（位于解决方案根目录的上一级，与 `Standard\` 同级），
三者几何与配色必须一致：

| 产物 | 位置 | 说明 |
| --- | --- | --- |
| 源文件 | `..\icon.svg` | 256×256 矢量源，改动后另两处须同步 |
| exe / 窗口图标 | `src/Prt.App/Assets/app.ico` | 10 档尺寸（16–256），由 `.workbuddy/scratch/scripts/make_app_icon.py` 离线烘焙 |
| 界面内矢量标记 | `src/Prt.App/Theming/Brand.xaml` | `Brand.MarkBrush`（完整）/ `Brand.MarkSimpleBrush`（小尺寸简化） |

## 三、文档索引

| 文档 | 内容 |
| --- | --- |
| [docs/使用指南.md](docs/使用指南.md) | 功能概述、操作流程、PRT 语法速查、常见问题 |
| [docs/安装说明.md](docs/安装说明.md) | 安装器形态、安装条件、图形向导、静默安装参数、卸载、故障排查。**终端用户与批量部署者先看这份** |
| [docs/部署文档.md](docs/部署文档.md) | 环境依赖、构建与安装、配置、启动与验证、故障排查 |
| [docs/系统适配说明.md](docs/系统适配说明.md) | 支持平台、硬件要求、第三方依赖、**跨平台限制**、已知限制 |
| [docs/符合性声明.md](docs/符合性声明.md) | 符合等级、已实现项、不支持项、降级行为、边界说明 |
| [docs/行为清单.md](docs/行为清单.md) | PSS 安全行为申报口径（接入模式 A，含 SRT 覆盖表与已知取舍，随程序分发） |
| [docs/工程改进方案.md](docs/工程改进方案.md) | 改造依据登记处：§4.8（超大文件 partial 化，29 处源码注释的引用目标）、§4.14（丈量口径入脚本）与第 3/7/10/13 项。**原始方案未随仓库保存**，本文件只登记实际引用的编号，其余条目号刻意留空 |
| [docs/虚拟化改造方案.md](docs/虚拟化改造方案.md) | 预览与源码编辑器的虚拟化改造方案（M2 / M3）与 M2a 落地记录 |
| [docs/设计取舍.md](docs/设计取舍.md) | CRS 1.3 八条不适用场景的逐条声明、19 条已知妥协及其缓解与复核方式、锁序与共享状态约定。**源码注释里「见《设计取舍》」的引用目标** |
| [docs/分析器门禁.md](docs/分析器门禁.md) | 分析器门禁的三档色阶（`error` 9 条 / `none` 14 条 / 其余棘轮）、逐条判据、基线计数、复核命令。与 `.editorconfig`、`Directory.Build.props` 三处同源 |
| [docs/质量检查表.md](docs/质量检查表.md) | 机器可判项复核命令 + 需人工打勾项 + 已知例外 4 条 + 改造验收门（结构指纹逐字节比对）。`build.ps1` 棘轮注释与 `selfscore.py` 口径分歧说明的引用目标 |
| [docs/自评报告.md](docs/自评报告.md) | CRS 9.6 自评结果（由 `tools/selfscore.py` 生成，含逐项文件+行号证据） |
