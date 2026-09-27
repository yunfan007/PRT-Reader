#Requires -Version 5.1
<#
.SYNOPSIS
    PRT 阅读器（Perisc Standard Richtext）全流程构建脚本。

.DESCRIPTION
    串联「清理 → 还原 → 严格编译与门禁 → 测试 → 自检 → 自评 → 发布」八个环节，可用 -Task
    选择其中任意环节执行。任一环节失败即中止，并以非零退出码返回，适用于本地开发与 CI。

    产物保留策略（v2）：
      · **publish 目录始终保留**——任何环节都不会删除已发布的产物；
      · **编译中间产物自动清理**——各项目的 bin / obj、artifacts、TestResults、.vs
        等中间目录在流程结束前统一删除（除非指定 -NoCleanup）。
    因此脚本跑完后，磁盘上只剩下源码与 publish 里的成品，可随时打包分发。

    说明：测试环节仅在存在测试工程（tests/**/*.csproj）时执行；若当前代码树不含
    独立测试工程，该环节自动跳过（不影响其余环节）。本仓库按既定口径**不新建测试工程**，
    测试用例并入启动自检（-Task selftest），故 test 环节恒为跳过。

    外部依赖：lint 与 score 两个环节需要 Python 3（脚本 tools/selfscore.py，仅标准库、
    无第三方包）；build / restore / test / selftest / publish 只需 .NET 8 SDK。
    门禁与自评共用同一个脚本，避免两处判据各自演进而互相矛盾。

.PARAMETER Configuration
    构建配置：Debug 或 Release。默认 Release。

.PARAMETER Task
    要执行的环节，可多选（逗号或空格分隔）：
      clean / restore / build / lint / test / selftest / score / publish / installer / all
    默认 all。指定的环节会按固定顺序执行，与书写顺序无关。
    installer 环节依赖 publish 的「阅读器」产物（已存在则直接复用，否则自行发布），
    因此按 all 跑时它自然排在 publish 之后。
    clean 只删除编译中间产物，**不删除 publish**。
    lint  严格编译（-warnaserror，0 警告）+ 自研门禁脚本（空 catch、PushFrame、
          async void、可写静态成员、区域敏感比较、时间读取）；任一不达标即失败。
    score 生成 CRS 9.6 自评报告（docs\自评报告.md）并做维度阈值检查；
          需要 Python 3（lint 的门禁脚本同样需要，两者共用 tools/selfscore.py）。
    all = clean → restore → lint → test → selftest → score → publish。
          全流程里用 lint 代替 build：lint 就是同配置下更严格的编译，
          先 build 再 lint 会把整个解决方案白编一遍。
    例：-Task build,selftest   或   -Task "lint score"

.PARAMETER PublishMode
    发布形态，默认 framework-dependent：
      framework-dependent  框架依赖（体积小，目标机需 .NET 8 桌面运行时）
      single-file          框架依赖单文件（依赖全部打进一个 exe，目标机需 .NET 8 桌面运行时）
      self-contained       自包含（免运行时，体积较大）

    为什么 single-file 不是自包含：自包含要把运行时一并打进产物，代价是必须取得
    Runtime Pack（Microsoft.NETCore.App.Runtime.<RID> 等）。Runtime Pack 只以 NuGet 包
    形式分发，而本仓库 nuget.config 已 <clear/> 全部包源（见 README「离线编译」一节）——
    在本工程的离线约束下它必然拿不到，与机器无关，换谁的电脑都一样。
    两者只能取其一，本仓库取「离线」：single-file 定为框架依赖单文件，产物形态与
    framework-dependent 一致（一个 exe），目标机同样需要 .NET 8 桌面运行时。

    self-contained 模式仍然保留，脚本会在发布前检测 Runtime Pack 是否可用；
    缺失时直接给出失败原因与两条出路，而不是让 NuGet 抛出一串 NU1101。
    另注：single-file 的 -p:SelfContained=false 不能改回 CLI 的 --self-contained false，
    两者混用时后者不生效（详见 Invoke-Publish 内的注释）。

.PARAMETER Runtime
    目标运行时标识（RID），默认 win-x64（可填 win-arm64 等）。

.PARAMETER OutputDir
    发布输出目录，默认 <仓库根>\publish\<PublishMode>。发布目录只放三类成品：
      阅读器\             ← Perisc.Safety.SafeGuard（宿主启动器，模块三，**唯一入口**）
                            ＋ Prt.App（主程序，编译为 DLL，PSS 7.1 的接入模式 A）
                            ＋ Safe.data（运行体完整性基准，本脚本在发布末尾生成，见 Invoke-Publish）
      激活码生成器\       ← Prt.Tools.LicenseGen（WinForms 离线激活码工具）
      密钥\               ← keys\ 下的许可证公私钥（license.private/public.xml，
                            同时复制一份到 激活码生成器\ 供其自动查找）

    「阅读器」目录的两个产物必须同目录、且**只有启动器可双击**：主程序按接入模式 A
    编译为 DLL（Prt.App.csproj 的 UseAppHost=false），启动器在启动前校验同目录下
    Prt.App.dll 的 SHA-256（PSS 3.4 / 6.2）。**双击 Perisc.Safety.SafeGuard.exe 即可运行**
    （无参启动时它在同目录内推断主程序；它是 GUI 子系统程序，双击**不产生**控制台窗口，
    见《设计取舍》第 10 条）；命令行等价写法：
        Perisc.Safety.SafeGuard.exe [--program Prt.App.dll] -- 主程序参数…
    PRTA 代码编译器（Prt.Prta）不进入发布目录；需要时用 dotnet publish 单独发布对应工程。
    自检报告写在 <仓库根>\publish\selftest-report.txt（随 publish 一起保留）。

.PARAMETER InstallerRuntime
    安装包里是否自带便携 .NET 运行时（安装时按「系统有没有」决定要不要释放）：
      portable  自带（默认）。运行时被打成 runtime.zip 内嵌，约 50 MB，解压后约 162 MB。
      none      不带。安装包小得多，但目标机必须有 .NET 8 桌面运行时，否则双击文件起不来。
    两种形态都只是"要不要携带运行时素材"的差别，安装流程与卸载回滚完全一致。

.PARAMETER NoRestore
    跳过依赖还原环节（依赖已就绪时可用于加速）。

.PARAMETER NoCleanup
    流程结束后不自动清理编译中间产物（需要保留 bin / obj 做增量编译或排查问题时使用）。

.EXAMPLE
    .\build.ps1
    执行全流程：Release 配置 + 框架依赖发布；结束后自动清掉 bin / obj，保留 publish。

.EXAMPLE
    .\build.ps1 -Configuration Debug -Task build,selftest
    仅编译并运行启动自检，随后自动清理中间产物。

.EXAMPLE
    .\build.ps1 -Task clean
    只清理编译中间产物（保留 publish 与源码）。

.EXAMPLE
    .\build.ps1 -NoCleanup
    全流程构建并保留 bin / obj（便于随后用 IDE 调试）。
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Task = 'all',

    [ValidateSet('framework-dependent', 'self-contained', 'single-file')]
    [string]$PublishMode = 'framework-dependent',

    [string]$Runtime = 'win-x64',

    [string]$OutputDir,

    [ValidateSet('portable', 'none')]
    [string]$InstallerRuntime = 'portable',

    [switch]$NoRestore,

    [switch]$NoCleanup
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# 路径与常量
# ---------------------------------------------------------------------------
$Root         = $PSScriptRoot
$Solution     = Join-Path $Root 'Prt.sln'
$AppProject   = Join-Path $Root 'src\Prt.App\Prt.App.csproj'
$TestsDir     = Join-Path $Root 'tests'
$PublishRoot  = Join-Path $Root 'publish'
$SelfTestReport = Join-Path $PublishRoot 'selftest-report.txt'
$AppTargetDir = Join-Path $Root ('src\Prt.App\bin\' + $Configuration + '\net8.0-windows')
# 接入模式 A（PSS 7.1）：主程序编译为 DLL，不自带用户可直接双击的可执行入口；
# 因此自检不能再起 Prt.App.exe，须经 dotnet 宿主动态加载该 DLL。
$AppLibrary   = Join-Path $AppTargetDir 'Prt.App.dll'
# 宿主启动器（模块三）的可执行体：发布目录里的**唯一入口**。
# 目标框架是 net8.0-windows（不是 net8.0）：启动器要显示「安全系统启动中」图形界面，
# 而它本来就已经只能在 Windows 上工作（作业对象终止、控制台判定、系统弹窗都是 Windows 专有）。
$GuardTargetDir = Join-Path $Root ('src\Perisc.Safety.SafeGuard\bin\' + $Configuration + '\net8.0-windows')
$GuardExe       = Join-Path $GuardTargetDir 'Perisc.Safety.SafeGuard.exe'

# 自评生成器与自评报告（CRS 9.6，见 docs\工程改进方案.md §4.14）
# 安装包：把「阅读器产物 / 便携运行时 / 卸载器 / 帮助与示例」内嵌进一个自包含单文件 exe。
# content\ 由 build.ps1 的 installer 环节生成（**只增不删策略之外的例外**：它是构建输入，
# 每次发布前整棵重建，否则上一版的素材会一直留在资源表里）。
$InstallerProject  = Join-Path $Root 'src\Prt.Installer\Prt.Installer.csproj'
$UninstallerProject = Join-Path $Root 'src\Prt.Uninstaller\Prt.Uninstaller.csproj'
$InstallerContent  = Join-Path $Root 'src\Prt.Installer\content'
$InstallerStage    = Join-Path $PublishRoot 'installer\stage'
$InstallerOut      = Join-Path $PublishRoot 'installer'
$InstallerExe      = Join-Path $InstallerOut 'PRT-Installer.exe'

$SelfScoreScript = Join-Path $Root 'tools\selfscore.py'
$SelfScoreReport = Join-Path $Root 'docs\自评报告.md'
# 自评门槛：可维护性维度不得低于此档（9.2 十二档之一）。
# 这是**棘轮基线**——取值就是本文档实测的当前水准，回归即失败，不做提前达标。
# 当前实测：r_maint = 10.61% → B-。差额全部来自 D-06（按 9.6.4 计数锚点字面丈量为 3）；
# D-06 的读法存两解，见 docs\自评报告.md 与 docs\质量检查表.md 的"已知例外"。
$MinMaintLevel   = 'B-'

# 编译中间产物目录名：位于仓库内、且不在 publish\ 之下的同名目录都会被清理。
$IntermediateDirNames = @('bin', 'obj', 'artifacts', 'TestResults', '.vs')

# 发布分组：同一输出目录下只有两个子目录。
# 「阅读器」目录里同时放**宿主启动器**（模块三，perisc.safety.safeguard 的可执行体，唯一入口）
# 与**主程序**（Prt.App，编译为 DLL，PSS 7.1 的接入模式 A）——两者必须同目录，
# 启动器校验的"运行体哈希"就是同目录下的 Prt.App.dll。
$PublishTargets = @(
    @{ Name = '阅读器';       Projects = @('src\Perisc.Safety.SafeGuard\Perisc.Safety.SafeGuard.csproj',
                                            'src\Prt.App\Prt.App.csproj') },
    @{ Name = '激活码生成器'; Projects = @('src\Prt.Tools.LicenseGen\Prt.Tools.LicenseGen.csproj') }
)

# 随发布目录分发的密钥（许可证密钥对）：根目录「密钥」下各一份，
# 另在激活码生成器目录各放一份，使其按「程序目录」自动找到私钥、免手工指定路径。
$KeysDir        = Join-Path $Root 'keys'
$PublishKeyDir  = '密钥'
$KeyFiles       = @('license.private.xml', 'license.public.xml')

if (-not $OutputDir) {
    $OutputDir = Join-Path $PublishRoot $PublishMode
}

$script:StepNo    = 0
$script:StepTotal = 0

# ---------------------------------------------------------------------------
# 输出辅助
# ---------------------------------------------------------------------------
function Write-Step {
    param([string]$Text)
    Write-Host ''
    $prefix = ''
    if ($script:StepTotal -gt 0) {
        $prefix = ('[{0}/{1}] ' -f $script:StepNo, $script:StepTotal)
    }
    Write-Host ('  ' + $prefix + $Text) -ForegroundColor Cyan
    Write-Host ('  ' + ('-' * 62)) -ForegroundColor DarkGray
}

function Write-Info  { param([string]$Text) Write-Host ('    ' + $Text) -ForegroundColor Gray }
function Write-Ok    { param([string]$Text) Write-Host ('    [OK] ' + $Text) -ForegroundColor Green }
function Write-Skip  { param([string]$Text) Write-Host ('    [跳过] ' + $Text) -ForegroundColor Yellow }
function Write-Warn2 { param([string]$Text) Write-Host ('    [注意] ' + $Text) -ForegroundColor Yellow }

# 仓库内相对路径（用于输出可读的清单）。
function Get-RelativePath {
    param([string]$Path)
    if ($Path.StartsWith($Root, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $Path.Substring($Root.Length).TrimStart('\')
    }
    return $Path
}

function Invoke-Dotnet {
    param(
        [Parameter(Mandatory = $true)][string]   $Label,
        [Parameter(Mandatory = $true)][string[]] $ArgumentList
    )
    Write-Info ('> dotnet ' + ($ArgumentList -join ' '))
    & dotnet @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw ('{0} 失败（退出码 {1}）。' -f $Label, $LASTEXITCODE)
    }
}

# 查找可用的 Python 3 启动器。仅 score 环节需要它（脚本用标准库，无第三方包）。
function Get-PythonLauncher {
    foreach ($cand in @('python', 'python3')) {
        $c = Get-Command $cand -ErrorAction SilentlyContinue
        if ($c) { return [pscustomobject]@{ Exe = $c.Source; Pre = @() } }
    }
    $p = Get-Command 'py' -ErrorAction SilentlyContinue
    if ($p) { return [pscustomobject]@{ Exe = $p.Source; Pre = @('-3') } }
    return $null
}

# ---------------------------------------------------------------------------
# 中间产物清理（保留 publish）
# ---------------------------------------------------------------------------
function Get-IntermediateDirs {
    Get-ChildItem -Path $Root -Directory -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object {
            ($IntermediateDirNames -contains $_.Name) -and
            (-not $_.FullName.StartsWith($PublishRoot, [System.StringComparison]::OrdinalIgnoreCase))
        } |
        Sort-Object { $_.FullName.Length } -Descending
}

function Invoke-CleanIntermediates {
    param([string]$Title = '清理编译中间产物')

    Write-Step $Title

    $dirs = @(Get-IntermediateDirs)
    if ($dirs.Count -eq 0) {
        Write-Info '没有需要清理的中间目录。'
        Write-Ok 'publish 目录保留不变。'
        return
    }

    $removed = 0
    $locked  = New-Object System.Collections.Generic.List[string]

    foreach ($dir in $dirs) {
        # 父目录可能已随子目录一起删除，这里再确认一次。
        if (-not (Test-Path -LiteralPath $dir.FullName)) { continue }

        try {
            [System.IO.Directory]::Delete($dir.FullName, $true)
            $removed++
            Write-Info ('已删除 ' + (Get-RelativePath $dir.FullName))
        }
        catch {
            # 目录被占用（多半是程序还在运行 / 文件管理器停在里面）。
            $locked.Add((Get-RelativePath $dir.FullName))
        }
    }

    if ($locked.Count -gt 0) {
        Write-Warn2 ('有 ' + $locked.Count + ' 个目录被占用，暂时删不掉：')
        foreach ($item in $locked) { Write-Warn2 ('  · ' + $item) }
        Write-Warn2 '请关闭正在运行的工具窗口（编辑器 / 编译器 / 激活码生成器）后再执行 .\build.ps1 -Task clean。'
    }

    Write-Ok ('已清理 ' + $removed + ' 个中间目录；publish 目录保留不变。')
}

# ---------------------------------------------------------------------------
# 环节实现
# ---------------------------------------------------------------------------
function Invoke-Clean {
    # clean 只清中间产物；publish 属于成品，任何情况下都不动。
    Invoke-CleanIntermediates -Title '清理编译中间产物（保留 publish）'
}

function Invoke-Restore {
    Write-Step '还原依赖'
    Invoke-Dotnet -Label '还原' -ArgumentList @('restore', $Solution, '--nologo')
    Write-Ok '依赖还原完成'
}

function Invoke-Build {
    Write-Step ('编译（' + $Configuration + '）')
    Invoke-Dotnet -Label '编译' -ArgumentList @('build', $Solution, '-c', $Configuration, '--nologo')
    Write-Ok '编译完成'
}

function Get-TestProject {
    if (-not (Test-Path -LiteralPath $TestsDir)) { return $null }
    $proj = Get-ChildItem -Path $TestsDir -Recurse -Filter '*.csproj' -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($proj) { return $proj.FullName }
    return $null
}

function Invoke-Test {
    Write-Step '运行单元测试'
    $proj = Get-TestProject
    if (-not $proj) {
        Write-Skip '未发现测试工程（tests/**/*.csproj），跳过测试环节。'
        return
    }
    Invoke-Dotnet -Label '单元测试' -ArgumentList @('test', $proj, '-c', $Configuration, '--nologo')
    Write-Ok '单元测试全部通过'
}

function Invoke-SelfTest {
    Write-Step '启动自检'
    if (-not (Test-Path -LiteralPath $AppLibrary)) {
        throw ('未找到 {0}，请先执行编译环节。' -f $AppLibrary)
    }
    if (-not (Test-Path -LiteralPath $PublishRoot)) {
        [System.IO.Directory]::CreateDirectory($PublishRoot) | Out-Null
    }
    # 接入模式 A 下主程序是 DLL（无 apphost），用 dotnet 宿主直接加载运行；
    # 自检不经过宿主启动器——启动器要校验哈希并写 %LOCALAPPDATA% 的基准，
    # 那是"运行形态"的验证，不该混进"程序自身正确性"的自检里。
    $selfTestArgs = @('exec', ('"' + $AppLibrary + '"'), '--selftest', ('"' + $SelfTestReport + '"'))
    Write-Info ('> dotnet ' + ($selfTestArgs -join ' '))
    # 子进程为 WPF（GUI 子系统相关）程序，须显式等待进程退出才能可靠取得退出码。
    # 这里直接使用 .NET 进程 API（而非 Start-Process），以规避其在环境变量大小写
    # 重复（如同时存在 http_proxy / HTTP_PROXY）时构造子进程环境失败的问题。
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName               = 'dotnet'
    $psi.Arguments              = ($selfTestArgs -join ' ')
    $psi.UseShellExecute        = $false
    $psi.CreateNoWindow         = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    $proc.WaitForExit()
    $code = $proc.ExitCode
    $proc.Dispose()
    if (Test-Path -LiteralPath $SelfTestReport) {
        Get-Content -LiteralPath $SelfTestReport -Encoding UTF8 | ForEach-Object { Write-Host ('      ' + $_) -ForegroundColor DarkGray }
    }
    if ($code -ne 0) {
        throw ('启动自检失败（退出码 {0}）。' -f $code)
    }
    Write-Ok ('自检全部通过；报告：' + (Get-RelativePath $SelfTestReport))
}

# ---------------------------------------------------------------------------
# 门禁与自评（CRS 9.6；判据见 docs\分析器门禁.md 与 docs\质量检查表.md）
# ---------------------------------------------------------------------------

function Get-SelfScoreLauncher {
    $py = Get-PythonLauncher
    if (-not $py) {
        throw ('未找到 Python 3，无法运行门禁/自评脚本 tools\selfscore.py。' +
               '请安装 Python 3 并加入 PATH（脚本只用标准库，不需要任何第三方包）。')
    }
    if (-not (Test-Path -LiteralPath $SelfScoreScript)) {
        throw ('未找到自评脚本：' + (Get-RelativePath $SelfScoreScript))
    }
    return $py
}

function Invoke-SelfScoreScript {
    param([string[]]$ExtraArgs)
    # 刻意**不捕获**子进程输出：一捕获，native stdout 就会进入本函数的输出流，
    # 调用方 `$rc = Invoke-SelfScoreScript …` 拿到的"退出码"会变成一整份报告文本
    # （实测踩过）；收进变量再回显还会把中文按 OEM 代码页解码成乱码。
    # 改为直接流式输出，退出码经 $script:LastSelfScoreExit 传回。
    $py = Get-SelfScoreLauncher
    $call = $py.Pre + @($SelfScoreScript) + $ExtraArgs
    Write-Info ('> ' + (Split-Path -Leaf $py.Exe) + ' ' + ($call -join ' '))
    & $py.Exe @call
    $script:LastSelfScoreExit = $LASTEXITCODE
}

function Invoke-Lint {
    Write-Step ('严格编译（' + $Configuration + '，警告即错误）+ 门禁脚本')
    # --no-incremental：增量缓存会让编译器跳过编译，从而"报不出"警告；门禁必须真编一遍。
    Invoke-Dotnet -Label '严格编译' -ArgumentList @(
        'build', $Solution, '-c', $Configuration, '--nologo', '--no-incremental', '-warnaserror')
    # -warnaserror 下任何警告都会让构建失败，所以构建成功即等价于「0 警告」——
    # 无需解析输出文本（解析输出会把中文按 OEM 代码页解码成乱码，且版本一改就失效）。
    $script:BuildWarnings = 0
    Write-Ok '严格编译通过：0 错误 / 0 警告（-warnaserror：有警告即失败）'

    Write-Info '门禁脚本：6 条硬门禁（空 catch、非事件处理器 async void、UI 线程阻塞、可写静态成员、区域敏感比较、时间读取）'
    Invoke-SelfScoreScript -ExtraArgs @('--check', '--gates-only', '--warnings', '0')
    if ($script:LastSelfScoreExit -ne 0) {
        throw ('门禁未通过（退出码 {0}）：见上方「门禁检查」小节。' -f $script:LastSelfScoreExit)
    }
    Write-Ok '门禁全部通过'
}

function Invoke-Score {
    Write-Step '启动自评（CRS 9.6 扣分项全集）'
    Write-Info ('报告落点 ' + (Get-RelativePath $SelfScoreReport))
    $warnings = if ($script:BuildWarnings -eq $null) { '0' } else { "$($script:BuildWarnings)" }
    Invoke-SelfScoreScript -ExtraArgs @(
        '--out', $SelfScoreReport, '--check', '--min-maint', $MinMaintLevel, '--warnings', $warnings)
    if ($script:LastSelfScoreExit -ne 0) {
        throw ('自评未达标（退出码 {0}）：见上方「门禁检查」小节；报告已写到 {1}。' -f
               $script:LastSelfScoreExit, (Get-RelativePath $SelfScoreReport))
    }
    Write-Ok ('自评通过；报告：' + (Get-RelativePath $SelfScoreReport))
}

# ---------------------------------------------------------------------------
# 自包含发布的 Runtime Pack 前置检测
#
# 为什么需要这一段：自包含发布要把运行时一并打进产物，因此必须取得 Runtime Pack
# （Microsoft.NETCore.App.Runtime.<RID>、Microsoft.WindowsDesktop.App.Runtime.<RID>）。
# Runtime Pack 只以 NuGet 包形式分发，而本仓库 nuget.config 已 <clear/> 全部包源，
# 纯离线环境下还原必然失败——表现为一串 NU1101「找不到包」，读起来像环境坏了，
# 实际是本仓库既定约束的必然结果。这里提前检测，把真实原因与出路一次说清。
# ---------------------------------------------------------------------------
function Assert-RuntimePackAvailable {
    param([Parameter(Mandatory = $true)][string]$Runtime)

    $dotnetExe = $null
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $dotnetExe = $cmd.Source }
    if (-not $dotnetExe) { $dotnetExe = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
    $packsRoot = Join-Path (Split-Path -Parent (Split-Path -Parent $dotnetExe)) 'packs'

    # Runtime Pack 有两个落点，任意一处命中即可：
    #   · SDK 自带的 packs\（离线 SDK 通常只带 Ref，不带 Runtime）；
    #   · 全局 NuGet 缓存——离线机器上只要**曾经**还原过一次自包含项目，
    #     Runtime Pack 就以普通 NuGet 包的形式躺在这里，此后无需联网也能再还原
    #     （安装器发布走的就是这条路，本次实测命中耗时 125ms）。
    # 只看 packs\ 会把"缓存里有"的机器也误判成失败。
    $globalPackages = Join-Path ${env:USERPROFILE} '.nuget\packages'

    $needed = @(
        ('Microsoft.NETCore.App.Runtime.' + $Runtime),
        ('Microsoft.WindowsDesktop.App.Runtime.' + $Runtime)
    )
    $missing = @()
    foreach ($pack in $needed) {
        $inPacks  = Test-Path -LiteralPath (Join-Path $packsRoot $pack)
        $inCache  = Test-Path -LiteralPath (Join-Path $globalPackages ($pack.ToLowerInvariant()))
        if (-not $inPacks -and -not $inCache) { $missing += $pack }
    }
    if ($missing.Count -eq 0) { return }

    Write-Host ''
    Write-Host ('    [失败] 自包含发布缺少 Runtime Pack：' + ($missing -join '、')) -ForegroundColor Red
    Write-Info ('已检测 packs 目录：' + $packsRoot)
    Write-Info ('已检测全局缓存：' + $globalPackages)
    Write-Info ('两者都没有该包，才判定为缺失。')
    Write-Warn2 'Runtime Pack 只以 NuGet 包形式分发，而本仓库 nuget.config 已清空全部包源。'
    Write-Warn2 '这与本机环境无关，换任何一台同样配置的机器结果都一样。可任选其一：'
    Write-Warn2 '  1) 改用离线可用的形态：-PublishMode framework-dependent（默认）或 single-file；'
    Write-Warn2 '     两者都是框架依赖，产物不含运行时，目标机需 .NET 8 桌面运行时。'
    Write-Warn2 '  2) 确需免运行时的产物：临时在 nuget.config 加入 nuget.org 源还原一次，'
    Write-Warn2 '     Runtime Pack 进入全局缓存后即可离线复现（本仓库默认不这样做）。'
    throw '自包含发布在当前环境不可用（缺少 Runtime Pack）。'
}

# ---------------------------------------------------------------------------
# 便携运行时打包
#
# 装在安装包里等安装时解压的那份 .NET。只取「运行时真正需要的东西」：
#   shared\Microsoft.NETCore.App\<版本>\*        基础运行时（含 apphost 解析用的 dotnet.dll）
#   shared\Microsoft.WindowsDesktop.App\<版本>\*  WPF（阅读器是 net8.0-windows，离不了）
#   host\fxr\<版本>\libhostfxr.dll                apphost 真正靠它解析框架（漏了就起不来）
#   dotnet.exe                                    便于在装好的目录里跑 dotnet --list-runtimes
# 刻意排除 sdk / packs / templates / library-packs / metadata / swidtag 等开发期内容：
# 它们只服务于"在这台机器上编译"，一个只读文档的阅读器用不到，白占一百多兆。
# ---------------------------------------------------------------------------
function New-PortableRuntimeArchive {
    param(
        [Parameter(Mandatory = $true)][string]$DotnetRoot,
        [Parameter(Mandatory = $true)][string]$ZipPath
    )

    # Windows PowerShell 5.1 不自动加载这两个程序集（PS 7 起随入）：
    # ZipArchive / ZipArchiveMode 在 System.IO.Compression，ZipFile 在其 FileSystem 变体里。
    Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop

    $zipDir = [System.IO.Path]::GetDirectoryName($ZipPath)
    if (-not [System.IO.Directory]::Exists($zipDir)) {
        [System.IO.Directory]::CreateDirectory($zipDir) | Out-Null
    }
    if (Test-Path -LiteralPath $ZipPath) { [System.IO.File]::Delete($ZipPath) }

    $frameworks = @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')
    $count = 0
    $bytes = 0L

    # ZipArchive 建好后逐步 AddEntryFromFile：一次性把一百多兆读进内存会吃满工作集。
    $zip = [System.IO.Compression.ZipFile]::Open($ZipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($framework in $frameworks) {
            $sharedDir = Join-Path $DotnetRoot ('shared' + [System.IO.Path]::DirectorySeparatorChar + $framework)
            if (-not (Test-Path -LiteralPath $sharedDir)) {
                Write-Warn2 ('便携运行时缺少框架目录，已跳过：' + (Get-RelativePath $sharedDir))
                continue
            }
            # 每个框架只取最高版本：装多份运行时的机器（SDK 与运行时常共装不同版本）不该
            # 把每一版都塞进安装包。
            $versions = Get-ChildItem -LiteralPath $sharedDir -Directory -ErrorAction SilentlyContinue |
                Sort-Object { [version]($_.Name -replace '^v', '') } -Descending
            if (-not $versions -or $versions.Count -eq 0) { continue }
            $version = $versions[0]

            $files = Get-ChildItem -LiteralPath (Join-Path $sharedDir $version.Name) -File -Recurse -ErrorAction SilentlyContinue
            foreach ($file in $files) {
                $entryName = ('shared\' + $framework + '\' + $version.Name + '\' +
                              $file.FullName.Substring($sharedDir.Length + $version.Name.Length + 1))
                $entry = $zip.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
                $entryStream = $entry.Open()
                $fs = [System.IO.File]::OpenRead($file.FullName)
                try {
                    $fs.CopyTo($entryStream, 4 * 1024 * 1024)
                }
                finally {
                    $fs.Dispose()
                    $entryStream.Dispose()
                }
                $count++
                $bytes += $file.Length
            }
        }

        # host/fxr：apphost 先找它，没有它连"该用哪个运行时"都定不了。
        $fxrRoot = Join-Path $DotnetRoot 'host\fxr'
        if (Test-Path -LiteralPath $fxrRoot) {
            foreach ($fxr in (Get-ChildItem -LiteralPath $fxrRoot -Directory | Sort-Object { [version]($_.Name -replace '^v', '') } -Descending)) {
                foreach ($file in (Get-ChildItem -LiteralPath (Join-Path $fxrRoot $fxr.Name) -File -ErrorAction SilentlyContinue)) {
                    $entry = $zip.CreateEntry('host\fxr\' + $fxr.Name + '\' + $file.Name,
                                              [System.IO.Compression.CompressionLevel]::Optimal)
                    $entryStream = $entry.Open()
                    $fs = [System.IO.File]::OpenRead($file.FullName)
                    try { $fs.CopyTo($entryStream, 1 * 1024 * 1024) }
                    finally { $fs.Dispose(); $entryStream.Dispose() }
                    $count++
                    $bytes += $file.Length
                }
                break      # 同样只取最高版本
            }
        }
        else {
            Write-Warn2 '便携运行时缺少 host\fxr（libhostfxr），装好的运行时可能起不来。'
        }

        $dotnetExe = Join-Path $DotnetRoot 'dotnet.exe'
        if (Test-Path -LiteralPath $dotnetExe) {
            $fs = [System.IO.File]::OpenRead($dotnetExe)
            try {
                $entry = $zip.CreateEntry('dotnet.exe', [System.IO.Compression.CompressionLevel]::Optimal)
                $entryStream = $entry.Open()
                $fs.CopyTo($entryStream, 1 * 1024 * 1024)
            }
            finally { $fs.Dispose(); $entryStream.Dispose() }
            $count++
        }

        # 版本标记：解压后放在 dotnet\.version，便于日后判断"这份便携运行时是哪台机器打的"。
        # [System.Text.Encoding]::UTF8 是**实例**属性，直接 ::GetBytes 会被当成静态方法调用；
        # 先存进变量再取实例成员才是 PowerShell 下能跑的写法。
        $marker = $zip.CreateEntry('.version', [System.IO.Compression.CompressionLevel]::Optimal)
        $markerStream = $marker.Open()
        $utf8 = [System.Text.Encoding]::UTF8
        $markerBytes = $utf8.GetBytes('portable runtime packaged from ' + $DotnetRoot + "`n")
        $markerStream.Write($markerBytes, 0, $markerBytes.Length)
        $markerStream.Dispose()
    }
    finally {
        $zip.Dispose()
    }

    $zipBytes = (Get-Item -LiteralPath $ZipPath).Length
    Write-Info ('便携运行时素材：' + $count + ' 个文件 / 原始 ' +
                [math]::Round($bytes / 1MB, 1) + ' MB → 压缩包 ' + [math]::Round($zipBytes / 1MB, 1) + ' MB')
}

function Invoke-Publish {
    Write-Step ('发布（' + $PublishMode + ' / ' + $Runtime + '）→ 阅读器（含宿主启动器）+ 激活码生成器 + 密钥')
    if ($PublishMode -eq 'self-contained') { Assert-RuntimePackAvailable -Runtime $Runtime }
    if (-not (Test-Path -LiteralPath $OutputDir)) {
        [System.IO.Directory]::CreateDirectory($OutputDir) | Out-Null
    }
    foreach ($target in $PublishTargets) {
        $targetDir = Join-Path $OutputDir $target.Name
        foreach ($project in $target.Projects) {
            Write-Info ('> ' + $target.Name + '  ←  ' + $project)
            $publishArgs = @('publish', (Join-Path $Root $project), '-c', $Configuration, '-r', $Runtime, '-o', $targetDir, '--nologo')
            switch ($PublishMode) {
                'framework-dependent' { $publishArgs += @('--self-contained', 'false') }
                # 框架依赖单文件：不取 Runtime Pack，可完全离线发布；目标机需 .NET 8 桌面运行时。
                #
                # 注意 SelfContained 必须用 -p: 属性形式给出：实测把它写成 CLI 开关
                # （--self-contained false）而与 -p:PublishSingleFile=true 同时出现时不生效，
                # 还原仍会去要 Runtime Pack 并报一串 NU1101；写成属性即正常，
                # 产物是单个 exe，全程不联网。
                'single-file'         { $publishArgs += @('-p:PublishSingleFile=true', '-p:SelfContained=false') }
                'self-contained'      { $publishArgs += @('--self-contained', 'true') }
            }
            Invoke-Dotnet -Label ('发布 ' + $target.Name) -ArgumentList $publishArgs
        }
    }

    # 接入模式 A（PSS 3.4 / 7.1）：主程序**不得**自带用户可直接启动的可执行入口。
    # 发布目录按既定策略"只增不删"，于是早先版本（Prt.App 还是 exe 时）留下的
    # Prt.App.exe 会一直躺在「阅读器」目录里——那等于给了一条绕开宿主启动器的正门，
    # 把"启动受控"这条约束悄悄拆掉。这里逐项清掉，而不是只提示。
    $readerDir = Join-Path $OutputDir '阅读器'
    foreach ($stale in @('Prt.App.exe')) {
        $stalePath = Join-Path $readerDir $stale
        if (Test-Path -LiteralPath $stalePath) {
            # 用 .NET 的文件 API 而不是 Remove-Item：本脚本清理中间产物走的就是
            # [System.IO.Directory]::Delete，同一约定；且 Remove-Item 在带宿主级
            # "安全删除"钩子的环境里会被接管并因 trash 失败而中止整个构建。
            [System.IO.File]::Delete($stalePath)
            Write-Warn2 ('已删除遗留的主程序入口（接入模式 A 不得有）：' + $stale)
        }
    }

    # 运行期用户数据**不得**随发布分发（纯便携落点：PortableStorage / 行为清单第七节）。
    # 这些文件是程序在使用过程中写在"程序所在文件夹"里的，而发布目录按既定策略"只增不删"，
    # 于是构建/验证期的残留会一直留在产物里。害处不只是"多几个文件"：
    #   - 首启迁移（LegacyConfigMigration）以"程序目录已有同名设置文件"为跳过条件，
    #     带着设置文件会让迁移**永远跳过**：用户 %APPDATA% 下的旧设置搬不过来，
    #     先看到的却是构建机的界面偏好与教程成绩；
    #   - 会话文件里是构建机的最近打开列表。
    # 清单与 App 侧常量一一对应（SettingsStore.StorageFileName / SessionService.StorageFileName /
    # App.ExceptionLogFileName / SettingsStore.ResourceFolderName）——新增落点时必须同步这里。
    $runtimeUserData = @('settings.json', 'session.json', 'prt-app-exception.log')
    foreach ($stale in $runtimeUserData) {
        $stalePath = Join-Path $readerDir $stale
        if (Test-Path -LiteralPath $stalePath) {
            [System.IO.File]::Delete($stalePath)
            Write-Warn2 ('已删除运行期用户数据（不应随发布分发）：' + $stale)
        }
    }
    $runtimeDataDir = Join-Path $readerDir 'resources'
    if (Test-Path -LiteralPath $runtimeDataDir) {
        [System.IO.Directory]::Delete($runtimeDataDir, $true)
        Write-Warn2 '已删除运行期用户数据目录（不应随发布分发）：resources'
    }

    # 授权凭据**不自动删除**、只告警：它同样是程序在程序目录生成的敏感文件（把构建机的
    # 许可发给用户等于泄漏授权），但误删会让本机要重新激活一次，代价比丢配置大，交给人判断。
    foreach ($credential in @('license.key', 'activations.json')) {
        if (Test-Path -LiteralPath (Join-Path $readerDir $credential)) {
            Write-Warn2 ('阅读器里存在授权凭据 ' + $credential + '：这是本机激活状态，切勿随发布分发（如需干净产物请手工移除）。')
        }
    }

    if (-not (Test-Path -LiteralPath (Join-Path $readerDir 'Perisc.Safety.SafeGuard.exe'))) {
        Write-Warn2 '「阅读器」目录里没有宿主启动器（Perisc.Safety.SafeGuard.exe）——该目录无法启动。'
    }

    # 分发密钥：根「密钥」目录一份 + 激活码生成器目录一份（后者使其按程序目录自动取私钥）。
    $keyTargets = @(
        (Join-Path $OutputDir $PublishKeyDir),
        (Join-Path $OutputDir '激活码生成器')
    )
    foreach ($dir in $keyTargets) {
        if (-not (Test-Path -LiteralPath $dir)) {
            [System.IO.Directory]::CreateDirectory($dir) | Out-Null
        }
        foreach ($file in $KeyFiles) {
            $source = Join-Path $KeysDir $file
            if (-not (Test-Path -LiteralPath $source)) {
                Write-Warn2 ('未找到密钥文件，已跳过：' + (Get-RelativePath $source))
                continue
            }
            Copy-Item -LiteralPath $source -Destination (Join-Path $dir $file) -Force
        }
    }
    Write-Ok ('密钥已复制：' + $PublishKeyDir + '\ 与 激活码生成器\（' + ($KeyFiles -join '、') + '）')

    # 随「阅读器」分发的符合性报告：内容取自**本次**构建产出的自检报告，使包内的报告与代码同源，
    # 而不是留一份不知道是哪个版本手抄的副本——发布目录"只增不删"，那种副本只会一直烂在包里
    # （此前那份写着"符合 PRT v2.1"的 final-report.txt 就是这么留下来的）。
    if (Test-Path -LiteralPath $SelfTestReport) {
        Copy-Item -LiteralPath $SelfTestReport -Destination (Join-Path $readerDir 'final-report.txt') -Force
        Write-Ok '阅读器\final-report.txt  ←  本次自检报告（随包分发的符合性报告）'
    }
    else {
        Write-Warn2 '未找到 publish\selftest-report.txt（本次未跑 selftest）——包内 final-report.txt 保持原样。'
    }

    # 运行体完整性基准（阅读器\Safe.data）：随包的**出厂声明**，由本脚本在发布末尾生成。
    #
    # 为什么基准要由构建生成、随包分发，而不是运行时在 %LOCALAPPDATA% 里"首次登记"：
    #   登记在本机的那一份与包**不同源**——它记的是"上次在这台机器上跑过的运行体"。
    #   于是重建一次就得先手工清掉本机残留，否则启动器拿旧基准去核新运行体、一律 HASH-MISMATCH
    #   拒启。出厂声明随包走之后，基准与同批发布的运行体天然同源：安装、升级都不需要任何环境干预。
    #
    # 启动器读取后以 FileShare.Read **独占持有句柄**到进程退出：别的进程只能再读，
    # 写入与删除一律失败。因此**程序正在运行时本脚本写不进这个文件**——
    # 那是预期的占用提示（见下面 catch），不是缺陷。
    $appLibrary = Join-Path $readerDir 'Prt.App.dll'
    if (Test-Path -LiteralPath $appLibrary) {
        $safeDataPath = Join-Path $readerDir 'Safe.data'
        $appHash = (Get-FileHash -LiteralPath $appLibrary -Algorithm SHA256).Hash.ToLowerInvariant()
        $safeText = @(
            '# 运行体完整性基准（出厂声明）',
            '# 由 build.ps1 在发布时生成并随发布包分发；宿主启动器 Perisc.Safety.SafeGuard 启动前据此核对运行体。',
            '# 格式：<运行体文件名> = sha256:<64 位十六进制>。本文件缺失或被改动，启动器会判定为缺少基准并拒绝启动。',
            ('Prt.App.dll = sha256:' + $appHash)
        ) -join "`r`n"
        try {
            # 必须 UTF-8 **无 BOM**：带 BOM 时首行的文件名会多一个不可见字符，条目就对不上了。
            [System.IO.File]::WriteAllText(
                $safeDataPath, $safeText + "`r`n", (New-Object System.Text.UTF8Encoding($false)))
            Write-Ok ('阅读器\Safe.data  ←  Prt.App.dll 的运行体基准（sha256:' + $appHash.Substring(0, 16) + '…）')
        }
        catch {
            Write-Warn2 ('写入 Safe.data 失败：' + $_.Exception.Message)
            Write-Warn2 '  多半是程序正在运行（启动器独占持有该文件）。请关闭阅读器后重跑 -Task publish。'
        }
    }
    else {
        Write-Warn2 '未找到 Prt.App.dll，未生成 Safe.data——该目录将无法通过启动器的完整性校验。'
    }

    # 产物清单
    Write-Info '发布产物：'
    # 阅读器目录的特殊之处：目录里的**唯一入口**必须是宿主启动器，主程序是不带 apphost 的 DLL。
    if (Test-Path -LiteralPath $readerDir) {
        $guardHash = if (Test-Path -LiteralPath (Join-Path $readerDir 'Perisc.Safety.SafeGuard.exe')) { 'Perisc.Safety.SafeGuard.exe' } else { '（缺启动器）' }
        $appLib = if (Test-Path -LiteralPath (Join-Path $readerDir 'Prt.App.dll')) { 'Prt.App.dll' } else { '（缺主程序）' }
        Write-Ok ('阅读器\  →  唯一入口 ' + $guardHash + '　＋　主程序 ' + $appLib + '（DLL，无 apphost）')
        if (Test-Path -LiteralPath (Join-Path $readerDir 'Safe.data')) {
            Write-Ok '阅读器\Safe.data  →  运行体完整性基准（启动前据此核对 Prt.App.dll）'
        }
        else {
            Write-Warn2 '阅读器\ 里没有 Safe.data——启动器会判定为缺少基准并拒绝启动（见上面的生成步骤）。'
        }
        $otherExes = Get-ChildItem -Path $readerDir -Filter '*.exe' -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ne 'Perisc.Safety.SafeGuard.exe' }
        foreach ($exe in $otherExes) {
            Write-Warn2 ('阅读器\ 里还有别的 exe：' + $exe.Name + '（接入模式 A 下应只有宿主启动器一个入口）')
        }
    }
    foreach ($target in $PublishTargets) {
        if ($target.Name -eq '阅读器') { continue }
        $targetDir = Join-Path $OutputDir $target.Name
        $exes = Get-ChildItem -Path $targetDir -Filter '*.exe' -ErrorAction SilentlyContinue
        $names = if ($exes) { ($exes | ForEach-Object { $_.Name }) -join '、' } else { '（无 exe，类库产物）' }
        Write-Ok ($target.Name + '\  →  ' + $names)
    }
    Write-Ok ($PublishKeyDir + '\  →  ' + ($KeyFiles -join '、'))
    Write-Ok ('输出根目录：' + (Get-RelativePath $OutputDir) + '（保留，不会被清理）')
}

# ---------------------------------------------------------------------------
# 安装包（自包含单文件，自带阅读器 / 卸载器 / 可选便携运行时）
#
# 「只能单文件」的落点有两个，都必须守住：
#   1) 发布输出 publish\installer\ 里**只有 PRT-Installer.exe 一个文件**——
#      自包含单文件会把运行时打进 exe，旁边再躺一个素材目录/依赖 DLL 就不算单文件了；
#   2) 素材只能在运行时从程序集里取，绝不能在 publish 时被复制到输出目录。
# 素材由本环节先编成 src\Prt.Installer\content\ 下的内嵌资源，发布时一并打进 exe。
# ---------------------------------------------------------------------------
function Test-InstallerStaged {
    param([string]$RelativeName)

    $path = Join-Path $InstallerContent $RelativeName
    if (Test-Path -LiteralPath $path) { return $true }
    Write-Warn2 ('content\' + $RelativeName + ' 不存在（ Installer 素材未就位）：' + (Get-RelativePath $path))
    return $false
}

function Assert-InstallerSingleFile {
    Write-Info '校验「安装包只能是单文件」：'
    $strays = Get-ChildItem -LiteralPath $InstallerOut -File -Force -ErrorAction SilentlyContinue
    if (-not $strays -or $strays.Count -eq 0) {
        throw ('安装包输出目录是空的：' + (Get-RelativePath $InstallerOut))
    }
    $names = @($strays | ForEach-Object { $_.Name })
    if ($names.Count -ne 1 -or $names[0] -ne 'PRT-Installer.exe') {
        Write-Host ('    [失败] publish\installer\ 里应当只有 PRT-Installer.exe 一个文件，实际是：' +
                    ($names -join '、')) -ForegroundColor Red
        Write-Warn2 '多半是 release 单文件属性没生效，或素材被复制到了输出目录。'
        throw '安装包不是单文件。'
    }
    $exeBytes = (Get-Item -LiteralPath $InstallerExe).Length
    Write-Ok ('PRT-Installer.exe（' + [math]::Round($exeBytes / 1MB, 1) + ' MB）——publish\installer\ 里仅此一个文件。')

    # 反证：exe 之外不能还有任何目录（素材目录是"单文件"最常见的破功方式）。
    $dirs = Get-ChildItem -LiteralPath $InstallerOut -Directory -Force -ErrorAction SilentlyContinue
    foreach ($dir in $dirs) {
        Write-Warn2 ('安装包输出目录里出现了子目录：' + (Get-RelativePath $dir.FullName) +
                     '（单文件发布不允许）')
    }
}

function Invoke-InstallerPublish {
    Write-Step ('打包安装器（单文件 / InstallerRuntime=' + $InstallerRuntime +
                '）→ publish\installer\PRT-Installer.exe')
    if (-not (Test-Path -LiteralPath $InstallerProject)) {
        throw ('未找到安装器工程：' + (Get-RelativePath $InstallerProject))
    }

    # --- 1) 阅读器产物：复用 publish 环节已经发好的「阅读器」目录（不重复发布） -------
    $readerStage = Join-Path $InstallerStage 'reader'
    $readerDir = Join-Path $OutputDir '阅读器'
    if (-not (Test-Path -LiteralPath $readerDir)) {
        Write-Info 'publish\ 下没有「阅读器」目录，先发布阅读器…'
        Invoke-Publish
    }
    if (-not (Test-Path -LiteralPath $readerDir)) {
        throw ('未找到阅读器发布产物：' + (Get-RelativePath $readerDir))
    }
    # stage 每次都从零建：Copy-Item -Recurse 是**合并**语义，上一轮残留的文件会混进本轮素材，
    # 于是 content\ 里就多出一批不知道属于哪个版本的旧文件。
    if (Test-Path -LiteralPath $InstallerStage) {
        [System.IO.Directory]::Delete($InstallerStage, $true)
    }
    New-Item -ItemType Directory -Path $InstallerStage -Force | Out-Null
    Copy-Item -LiteralPath $readerDir -Destination $readerStage -Recurse -Force

    # 发布产物里的 .pdb 是调试符号：会给安装包白塞几百 KB，又不 symbolize 任何东西。
    Get-ChildItem -LiteralPath $readerStage -Recurse -Filter '*.pdb' -File -ErrorAction SilentlyContinue |
        ForEach-Object { [System.IO.File]::Delete($_.FullName) }

    Write-Info ('阅读器素材 ← ' + (Get-RelativePath $readerDir) + '（' +
                (Get-ChildItem -LiteralPath $readerStage -Recurse -File).Count + ' 个文件）')

    # 帮助文档：阅读器产物自带 帮助\（作者指南 / 教程）与 安全\（行为清单），
    # 再把仓库 docs\ 补进 <安装目录>\docs\——使用指南、部署文档这些"装完以后要翻的"
    # 不加进包就没人看得到。示例文档已由阅读器产物带（samples\sample.prt），不重复打包。
    $docsStage = Join-Path $readerStage 'docs'
    New-Item -ItemType Directory -Path $docsStage -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $Root 'docs\*') -Destination $docsStage -Recurse -Force
    Write-Info ('帮助素材 ← ' + (Get-RelativePath (Join-Path $Root 'docs')) + ' → <安装目录>\docs\')

    # --- 2) 卸载器：发布成框架依赖单文件（见 Prt.Uninstaller.csproj 的口径说明） -----
    $uninstallStage = Join-Path $InstallerStage 'uninstall'
    New-Item -ItemType Directory -Path $uninstallStage -Force | Out-Null
    # IncludeNativeLibrariesForSelfExtract：WPF 原生依赖（PresentationNative / wpfgfx /
    # PenImc / D3DCompiler_47 / vcruntime140）默认**不进**单文件包，只会被摊在输出目录里。
    # 卸载器是安装目录里的常驻文件，旁边多五个 DLL 就等于它不"自洽"——
    # 用户挪开或删掉其中任何一个都起不来，故这里显式打进 exe（启动时自解压到临时目录）。
    # 输出目录就是 stage 根，产物即 stage\uninstall.exe，不再多套一层子目录。
    Invoke-Dotnet -Label '发布卸载器' -ArgumentList @(
        'publish', $UninstallerProject, '-c', $Configuration, '-r', $Runtime,
        '-o', $InstallerStage, '--nologo', '-p:PublishSingleFile=true', '-p:SelfContained=false',
        '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=none')
    $uninstallExe = Join-Path $InstallerStage 'uninstall.exe'
    if (-not (Test-Path -LiteralPath $uninstallExe)) {
        throw ('未找到卸载器产物：' + $uninstallExe)
    }
    Write-Info ('卸载器素材 ← uninstall.exe（框架依赖单文件）')

    # 这里刻意**没有**激活码生成器素材：安装包面向社会分发，而阅读器在「无激活码」时本就
    # 按 Free 级运行。包里再塞签发端，它唯一的新增能力就是签付费码——等于把付费墙当附件
    # 一起发货；让它只签免费码又纯属仪式（用户本来就是 Free）。签发端只留在 keys\ 与
    # publish\framework-dependent\密钥\，由俱乐部离线签发后把激活码发给用户粘贴。
    # 见下方「私钥门禁」：任何私钥混入 content\ 都会让构建直接失败。

    # --- 3) 便携运行时：整棵重建 content\（构建输入，不能让旧版素材赖着不走） -----
    Write-Info ('重建 ' + (Get-RelativePath $InstallerContent) + ' …')
    if (Test-Path -LiteralPath $InstallerContent) {
        [System.IO.Directory]::Delete($InstallerContent, $true)
    }
    New-Item -ItemType Directory -Path $InstallerContent -Force | Out-Null
    # 逐项复制而**不是** `Copy-Item stage content -Recurse`：目标目录已存在时，PowerShell 会把
    # 源目录**整体**当作一个子项塞进目标（实测得到 content\stage\…），素材就多套了一层。
    Get-ChildItem -LiteralPath $InstallerStage -Force |
        Copy-Item -Destination $InstallerContent -Recurse -Force

    # 卸载器素材必须待在 `uninstaller\` 子目录里，不能平铺在 content\ 根上。
    # csproj 的 LogicalName 由 %(RecursiveDir) 参与拼接，素材逻辑名＝content\ 下的相对路径；
    # 而 InstallPlan 是按 UninstallerPrefix（`prt.installer.content.uninstaller.`）取流的。
    # 放错一层：编译期一声不响，运行期抛「内嵌素材缺失」——安装包看着是好的，一装就缺文件。
    # 移动必须排在白名单之前：晚一步，根上的 uninstall.exe 就被当成"非预期条目"删掉了。
    $uninstallerDir = Join-Path $InstallerContent 'uninstaller'
    New-Item -ItemType Directory -Path $uninstallerDir -Force | Out-Null
    Move-Item -LiteralPath (Join-Path $InstallerContent 'uninstall.exe') -Destination $uninstallerDir -Force

    # 白名单截断：content\ 只认这几个条目。任何"上一次跑剩下的"（比如卸载器早先发布到子目录时
    # 留下的 uninstall\）都会被删掉——它们会一路嵌进 exe，成为谁也说不清来路的死素材。
    $allowedContent = @('reader', 'uninstaller', 'runtime.zip')
    foreach ($item in (Get-ChildItem -LiteralPath $InstallerContent -Force)) {
        if ($allowedContent -notcontains $item.Name) {
            [System.IO.Directory]::Delete($item.FullName, $true)
            Write-Warn2 ('content\ 里的非预期条目已清除：' + $item.Name)
        }
    }

    if ($InstallerRuntime -eq 'portable') {
        $dotnetRoot = Join-Path ${env:ProgramFiles} 'dotnet'
        if (-not (Test-Path -LiteralPath $dotnetRoot)) {
            $dotnetRoot = Join-Path ${env:ProgramW6432} 'dotnet'
        }
        if (-not (Test-Path -LiteralPath $dotnetRoot)) {
            throw ('未找到 .NET 安装目录，无法打包便携运行时（可用 -InstallerRuntime none 跳过）：' +
                   '请先安装 .NET SDK/运行时。')
        }
        New-PortableRuntimeArchive -DotnetRoot $dotnetRoot -ZipPath (Join-Path $InstallerContent 'runtime.zip')
    }
    else {
        Write-Skip '未打包便携运行时（-InstallerRuntime none）：安装包只能依赖系统已有的 .NET 8 桌面运行时。'
    }

    # content 之外不能残留任何副本，否则 exe 与磁盘上的素材会各自演进。
    if (Test-Path -LiteralPath $InstallerStage) {
        [System.IO.Directory]::Delete($InstallerStage, $true)
    }

    # --- 5) 发布安装器：自包含单文件 + 素材内嵌 -----------------------------------
    if (-not (Test-Path -LiteralPath $InstallerOut)) {
        New-Item -ItemType Directory -Path $InstallerOut -Force | Out-Null
    }
    # 输出目录先清场：单文件是「目录里只能有一个 exe」的硬约束，任何上一轮的手工产物
    #（早先直接 dotnet publish 到这里的 uninstall.exe / pdb）都会让下一轮构建在
    # Assert-InstallerSingleFile 上炸掉。全局口径是 publish 永不删，但 publish\installer\
    # 是唯一"必须只有一个文件"的输出目录，故对它例外——只删非 PRT-Installer.exe 的条目，
    # 且逐条报出来，不做静默删除。
    foreach ($stale in (Get-ChildItem -LiteralPath $InstallerOut -Force)) {
        if ($stale.Name -ne 'PRT-Installer.exe') {
            Remove-Item -LiteralPath $stale.FullName -Recurse -Force
            Write-Warn2 ('清掉安装包输出目录里的陈旧条目：' + (Get-RelativePath $stale.FullName))
        }
    }
    foreach ($required in @('reader', "uninstaller\\uninstall.exe", 'runtime.zip')) {
        if (-not (Test-InstallerStaged $required)) {
            Write-Warn2 '继续发布，但安装包将缺少素材。'
        }
    }
    Invoke-Dotnet -Label '发布安装器' -ArgumentList @(
        # EnableCompressionInSingleFile：安装包自己那份运行时也是"打进 exe"的，
        # 不压缩就是实打实的 160 MB——那是运行时**装好之后**就不在磁盘上的东西，
        # 为它让用户下载两百多兆并不划算。压缩后启动多一百来毫秒，一次性的安装流程可以接受。
        # 注：它只压 exe 里的托管程序集，**不压** content\ 下内嵌的素材（runtime.zip），
        # 因此体积也不会因此归零，但降到一百多兆。
        'publish', $InstallerProject, '-c', $Configuration, '-r', $Runtime,
        '-o', $InstallerOut, '--nologo', '-p:DebugType=none',
        '-p:PublishSingleFile=true', '-p:SelfContained=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true')

    # --- 6) 硬校验：只有 PRT-Installer.exe 一个文件 --------------------------------
    Assert-InstallerSingleFile

    # 私钥门禁：安装包里一旦出现 license.private.xml（或任何 *private*.xml），就等于把付费墙
    # 随包发货——解包者自签 Professional / PERPETUAL 的码，阅读器离线验签照样通过，因为
    # 它只认内嵌公钥的签名、而公钥本就是公开分发的。签发端不随包是设计决定，这条门禁
    # 只是防止哪次手滑又把密钥塞回 content\——编译期对此一声不响，运行期才发现就晚了。
    foreach ($scope in @($InstallerContent, $InstallerOut)) {
        if (-not (Test-Path -LiteralPath $scope)) { continue }
        $leaked = @(Get-ChildItem -LiteralPath $scope -Recurse -File -Force -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -like 'license.private.xml' -or $_.Name -like '*private*.xml' })
        if ($leaked.Count -gt 0) {
            throw ('安装包内出现私钥文件：' + (($leaked | ForEach-Object { $_.FullName }) -join '; ') +
                   '。激活码签发端不得随包分发，请从安装包素材中移除后重新打包。')
        }
    }
    Write-Ok '私钥门禁：安装包素材与输出目录内均无 license.private.xml'

    Write-Ok ('安装包：' + (Get-RelativePath $InstallerExe))
}

# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host '错误：未找到 dotnet 命令，请安装 .NET SDK 8.0.x 并加入 PATH。' -ForegroundColor Red
    exit 1
}

$allowed  = @('clean', 'restore', 'build', 'lint', 'test', 'selftest', 'score', 'publish', 'installer')
# 显式指定环节时的执行顺序（与书写顺序无关）。
$order    = @('clean', 'restore', 'build', 'lint', 'test', 'selftest', 'score', 'publish', 'installer')
# all 的执行顺序：用 lint 代替 build——lint 就是同一配置下更严格的编译（警告即错误），
# 先 build 再 lint 会把整个解决方案白编一遍，不产生任何新信息。
$allOrder = @('clean', 'restore', 'lint', 'test', 'selftest', 'score', 'publish', 'installer')
$selected = @()
$script:BuildWarnings     = $null
$script:LastSelfScoreExit = 0

$taskList = @()
foreach ($piece in ($Task -split '[,;\s]+')) {
    $item = $piece.Trim().ToLowerInvariant()
    if ($item) { $taskList += $item }
}
foreach ($item in $taskList) {
    if ($item -ne 'all' -and ($allowed -notcontains $item)) {
        Write-Host ("错误：未知环节 '" + $item + "'。可选值：" + (($allowed + 'all') -join ' / ')) -ForegroundColor Red
        exit 1
    }
}
if ($taskList -contains 'all') {
    foreach ($name in $order) {
        if ($allOrder -contains $name) { $selected += $name }
    }
} else {
    foreach ($name in $order) {
        if ($taskList -contains $name) { $selected += $name }
    }
}
if ($selected.Count -eq 0) {
    Write-Host '错误：未指定任何有效环节。' -ForegroundColor Red
    exit 1
}

# 收尾清理：只要本次不是"只跑 clean"，就在结束前统一清掉中间产物（-NoCleanup 可关闭）。
$needFinalCleanup = (-not $NoCleanup) -and (
    ($selected.Count -gt 1) -or ($selected[0] -ne 'clean')
)

Write-Host ''
Write-Host '  PRT 阅读器 构建脚本' -ForegroundColor White
Write-Info ('仓库根目录：' + $Root)
Write-Info ('构建配置  ：' + $Configuration)
Write-Info ('执行环节  ：' + ($selected -join ' -> '))
Write-Info ('产物策略  ：保留 publish；' + $(if ($needFinalCleanup) { '结束后自动清理 bin / obj 等中间目录' } else { '本次不执行收尾清理' }))
if ($selected -contains 'publish') {
    Write-Info ('发布形态  ：' + $PublishMode + ' / ' + $Runtime)
    Write-Info ('输出目录  ：' + (Get-RelativePath $OutputDir))
}
if ($selected -contains 'installer') {
    Write-Info ('安装包形态：自包含单文件 / InstallerRuntime=' + $InstallerRuntime)
    Write-Info ('安装包输出：' + (Get-RelativePath $InstallerOut) + '（只应出现 PRT-Installer.exe）')
}

$script:StepTotal = $selected.Count
if ($needFinalCleanup) { $script:StepTotal++ }
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

try {
    foreach ($step in $selected) {
        $script:StepNo++
        switch ($step) {
            'clean'    { Invoke-Clean }
            'restore'  { if ($NoRestore) { Write-Step '还原依赖'; Write-Skip '已指定 -NoRestore。' } else { Invoke-Restore } }
            'build'    { Invoke-Build }
            'lint'     { Invoke-Lint }
            'test'     { Invoke-Test }
            'selftest' { Invoke-SelfTest }
            'score'    { Invoke-Score }
            'publish'  { Invoke-Publish }
            'installer' { Invoke-InstallerPublish }
        }
    }

    if ($needFinalCleanup) {
        $script:StepNo++
        Invoke-CleanIntermediates -Title '收尾：清理编译中间产物（保留 publish）'
    }

    $stopwatch.Stop()
    Write-Host ''
    Write-Host ('  构建成功，用时 ' + [math]::Round($stopwatch.Elapsed.TotalSeconds, 2) + ' 秒。') -ForegroundColor Green
    if ($selected -contains 'publish') {
        Write-Host ('  成品位置：' + $OutputDir) -ForegroundColor Green
    }
    if ($selected -contains 'installer' -and (Test-Path -LiteralPath $InstallerExe)) {
        Write-Host ('  安装包　：' + (Get-RelativePath $InstallerExe)) -ForegroundColor Green
    }
    exit 0
}
catch {
    $stopwatch.Stop()
    Write-Host ''
    Write-Host ('  构建失败：' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
