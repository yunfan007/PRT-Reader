using System.Runtime.InteropServices;

// P/Invoke 的 DLL 搜索路径限定（CA5392，门禁为 error）。
//
// 宿主启动器用作业对象（kernel32）收进程树：CreateJobObject / AssignProcessToJobObject /
// TerminateJobObject 等入口必须只从系统目录解析。若允许按当前目录搜索同名 DLL，
// 一次「把 kernel32.dll 放到启动目录」就能接管"终止谁"这件事——
// 而终止是不可撤销的最强手段，它的入口不能由调用方环境决定。
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
