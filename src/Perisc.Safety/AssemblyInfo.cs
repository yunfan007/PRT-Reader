using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// P/Invoke 的 DLL 搜索路径限定（CA5392，门禁为 error）。
//
// 模块四的剪贴板函数直接调用 user32 / kernel32（不引用 WPF/WinForms，好让本库保持 net8.0）。
// 这些入口必须只从系统目录解析：若允许按调用方进程的当前目录搜索同名 DLL，
// 一次「放入同目录的同名 dll」就能把敏感动作的申报路径换掉——这与本库要守的东西正面冲突。
// 因此限定为 SafeDirectories（仅系统目录），这也是 CA5392 要求显式声明的原因。
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]

// 启动自检宿主可见性（9.6.4 / D-11 相关证据链的一环）
//
// 背景：本工程按 4.7「对外只读」把实现细节（匹配、许可存储、审计状态机、环境持有者）
// 一律声明为 internal，公开面只保留 4.3 / 4.5 / 4.6 规定的契约。
//
// 但 9.6.3 要求非 0 分项给出可复现的证据，而这些证据恰恰只能落在库内类型上：
// 6.1.3 的回拨保护要用 ManualClock 主动把时间调回去才发现得了，
// 6.1.6 的落盘顺序与损坏行计数要真的写盘、真的掺坏一行才验证得了。
//
// 两种做法里选了后者：
//   ① 为测试把这些类型提升为 public —— 会无谓扩大受规范约束的公开契约面，
//      以后任何改动都变成"改标准"，代价被永久背上；
//   ② 用 InternalsVisibleTo 把自检宿主列为友元 —— 公开面一点不动，自检照样够得着。
//
// 限定到具体程序集（不是全开放），且只对自检宿主开放。
[assembly: InternalsVisibleTo("Prt.App")]
