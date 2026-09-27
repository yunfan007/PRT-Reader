#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""CRS 9.6 扣分项自评生成器 —— PRT 阅读器工程自评材料（可复算）

用法
----
    python tools/selfscore.py                          # 生成自评报告（stdout）
    python tools/selfscore.py --out docs/自评报告.md     # 同时写文件
    python tools/selfscore.py --warnings 0             # 注入构建警告数（D-21 用）
    python tools/selfscore.py --check                  # 门禁模式，任一硬门禁不达标 → 退出码 1
    python tools/selfscore.py --json r.json            # 机器可读结果

依据
----
《CRS 标准》9.6.1（A 组 17 项）/ 9.6.2（B 组 15 项）/ 9.6.3（通用锚点）/
9.6.4（逐项锚点）/ 9.6.5（一票否决）/ 9.2（可维护性定级）/ 9.4.1~9.4.3 与 9.4.5
（安全合规定级）/ 9.5（综合定级）/ 9.1（对外五档）。

═══════════════════════════════════════════════════════════════════════════
★ 丈量口径（§4.14 明文要求写进脚本说明；这七条是实测踩过的坑，不是理论担忧）
═══════════════════════════════════════════════════════════════════════════

1. **分母定义必须先写成可复算的算式**。不同口径能差一个数量级：D-09 在「宽口径
   （把只有注释没有语句的 catch 也算违规）」下是 37 处，按 9.6.4 的三要素锚点
   （有规范依据 + 有降级说明即不算违规）真实待补只有 11 处。脚本对每项都打印分母。

2. **间接调用要沿调用链**。纯文本 grep 只看得到直接 I/O：D-28 直接口径 2 处，
   沿 1 跳调用链 ≥ 9 处——低估 4 倍以上。本脚本对 lock 块做 1 跳私有方法展开。

3. **默认重载要按语言语义还原**。`string.Equals(a,b)` 与字符串 `a == b` 默认即
   Ordinal 值比较，语言语义上不是区域敏感缺陷；不还原，D-17 会从 1 处虚报成 13 处。
   D-17 只统计 `ToLower()/ToUpper()` 参与比较，以及 `string.Compare` / `CompareTo`
   的**区域敏感重载**（未显式传 StringComparison）。

4. **抽样 ≥ 30 处或全量**（取小者）。auto 项一律全量；标注「人工」的项给抽样提示，
   且 9.6.3 要求写明命中率——没有证据的定档不予采信。

5. **工具优先项标注**。9.6.4 标 `[工具]` 的项（D-05、D-06、D-11a、D-11b、D-13、
   D-16、D-18、D-24）附工具名与报告；工具与人工结论冲突时**以更严重者为准**。

6. **UI 层不强制 ConfigureAwait**（9.6.1 D-25）：`Prt.App` / `Prt.Prta` 不计入
   D-25 分子，也不计入 D-25 分母。（Prt.Prta 为编译器与图形前端合一的 UI 工程，
   2026-09-25 由 Prt.Prta.Compiler + Prt.Prta.Gui 两工程合并而来。）

7. **纯托管对象依赖 GC**（9.6.1 D-11c）：`MemoryStream` / `StringContent` /
   `SemaphoreSlim` 之类未 Dispose **不等于泄漏**，只计 D-11c（建议，权重 1），
   不得升格到 D-11a（必修）。

另两条易错项：
  · **`async void` 事件处理器除外**（D-22）：只有非事件处理器的 `async void` 计分。
  · **手动 `Close()` 包在 `finally` / `using` 里是合规的**（D-11d 逐项锚点），
    不包才扣分。

═══════════════════════════════════════════════════════════════════════════
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
from dataclasses import dataclass, field, asdict

try:  # 中文输出在 Windows 控制台 / 重定向下都要稳
    sys.stdout.reconfigure(encoding="utf-8")  # type: ignore[attr-defined]
except Exception:  # pragma: no cover
    pass

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SRC = os.path.join(ROOT, "src")
SKIP_DIRS = {"obj", "bin"}

# UI 工程：D-25 不适用、D-14/D-15 才适用（9.6.1 明文「UI 层不强制」ConfigureAwait）
UI_PROJECTS = ("Prt.App", "Prt.Prta")
# 库工程：D-25 适用
LIB_PROJECTS = ("Perisc.Safety", "Perisc.Safety.SafeGuard", "Prt.Core")

# CLOCK_ALLOWLIST：D-20 的豁免名单，只收「时钟实现」本身——也就是把系统时间读取
# 收敛到那一个类型的地方。每个可执行工程都要有自己的取时点（宿主 AppClock、安装器
# InstallClock……），否则 D-20 会拿「读了几处」当扣分项，而它真正反对的是"没收敛"。
# 新增一个取时点时，请把该类型所属的文件加进来，而不是在业务代码里绕开检查。
CLOCK_ALLOWLIST = re.compile(
    r"src/Perisc\.Safety/SafetyClock\.cs|src/Perisc\.Safety\.SafeGuard/GuardClock\.cs"
    r"|src/Prt\.Tools\.LicenseGen/ToolClock\.cs|src/Prt\.App/Services/AppClock\.cs"
    r"|src/Prt\.Installer/Install/InstallClock\.cs")


# ───────────────────────────────────────────────────────────────────────────
# 1. 源码预处理：注释 / 字符串剥离（保住行号与列位）
# ───────────────────────────────────────────────────────────────────────────

def strip_comments(text: str) -> str:
    """抹掉注释（保留字符串字面量），用于需要看字符串内容的检查（D-19）。"""
    out, i, n = [], 0, len(text)
    while i < n:
        ch = text[i]
        if ch == '"':
            j = i + 1
            verbatim = i > 0 and text[i - 1] == '@'
            while j < n:
                if verbatim:
                    if text[j] == '"' and not (j + 1 < n and text[j + 1] == '"'):
                        break
                    if text[j] == '"':
                        j += 2
                        continue
                else:
                    if text[j] == '\\':
                        j += 2
                        continue
                    if text[j] == '"' or text[j] == '\n':
                        break
                j += 1
            j = min(j + 1, n)
            out.append(text[i:j])
            i = j
        elif ch == '/' and i + 1 < n and text[i + 1] == '*':
            j = text.find('*/', i + 2)
            j = n if j < 0 else j + 2
            out.append(re.sub(r'[^\n]', ' ', text[i:j]))
            i = j
        elif ch == '/' and i + 1 < n and text[i + 1] == '/':
            j = text.find('\n', i)
            j = n if j < 0 else j
            out.append(' ' * (j - i))
            i = j
        else:
            out.append(ch)
            i += 1
    return ''.join(out)


def strip_comments_and_strings(text: str) -> str:
    """抹掉注释与字符串/字符字面量，用于结构与花括号配平（避免字面量里的 {} 干扰）。"""
    out, i, n = [], 0, len(text)
    while i < n:
        ch = text[i]
        if ch == '/' and i + 1 < n and text[i + 1] == '*':
            j = text.find('*/', i + 2)
            j = n if j < 0 else j + 2
            out.append(re.sub(r'[^\n]', ' ', text[i:j]))
            i = j
        elif ch == '/' and i + 1 < n and text[i + 1] == '/':
            j = text.find('\n', i)
            j = n if j < 0 else j
            out.append(' ' * (j - i))
            i = j
        elif ch == '"':
            verbatim = i > 0 and text[i - 1] == '@'
            j = i + 1
            while j < n:
                if verbatim:
                    if text[j] == '"':
                        if j + 1 < n and text[j + 1] == '"':
                            j += 2
                            continue
                        break
                    j += 1
                else:
                    if text[j] == '\\':
                        j += 2
                        continue
                    if text[j] == '"' or text[j] == '\n':
                        j += 1
                        break
                    j += 1
            j = min(j, n) if not verbatim else min(j + 1, n)
            out.append(re.sub(r'[^\n]', ' ', text[i:j]))
            i = j
        elif ch == "'":
            j = i + 1
            while j < n:
                if text[j] == '\\':
                    j += 2
                    continue
                if text[j] == "'" or text[j] == '\n':
                    j += 1
                    break
                j += 1
            out.append(re.sub(r'[^\n]', ' ', text[i:min(j, n)]))
            i = min(j, n)
        else:
            out.append(ch)
            i += 1
    return ''.join(out)


@dataclass
class FileText:
    rel: str                      # 相对仓库根的路径（正斜杠）
    project: str                  # 工程名，如 Prt.App
    raw: str
    code: str                     # 注释 + 字符串均抹白
    lit: str                      # 仅注释抹白（保留字符串）
    lines: list[str]
    code_lines: list[str]
    lit_lines: list[str]

    @property
    def is_ui(self) -> bool:
        return self.project in UI_PROJECTS

    @property
    def is_lib(self) -> bool:
        return self.project in LIB_PROJECTS


def collect() -> list[FileText]:
    files: list[FileText] = []
    for dirpath, dirnames, filenames in os.walk(SRC):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for name in sorted(filenames):
            if not name.endswith(".cs"):
                continue
            full = os.path.join(dirpath, name)
            raw = open(full, encoding="utf-8", errors="replace").read()
            rel = os.path.relpath(full, ROOT).replace("\\", "/")
            files.append(FileText(
                rel=rel,
                project=rel.split("/")[1] if rel.count("/") > 1 else "",
                raw=raw,
                code=strip_comments_and_strings(raw),
                lit=strip_comments(raw),
                lines=raw.splitlines(),
                code_lines=strip_comments_and_strings(raw).splitlines(),
                lit_lines=strip_comments(raw).splitlines(),
            ))
    return sorted(files, key=lambda f: f.rel)


@dataclass
class F:
    rel: str
    line: int
    text: str

    def render(self, limit: int = 108) -> str:
        return f"{self.rel}:{self.line}  {self.text.strip()[:limit]}"


# ───────────────────────────────────────────────────────────────────────────
# 2. 语法骨架：花括号配平 / 方法与方法体 / 循环体 / lock 体
# ───────────────────────────────────────────────────────────────────────────

def brace_match(code: str, open_idx: int) -> int:
    """从 `{` 配平到闭括号位置；未闭合返回 -1。"""
    depth, i, n = 0, open_idx, len(code)
    while i < n:
        c = code[i]
        if c == '{':
            depth += 1
        elif c == '}':
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return -1


def line_of(code: str, idx: int) -> int:
    return code.count('\n', 0, idx) + 1


CONTROL_KEYWORDS = {
    "if", "else", "while", "for", "foreach", "switch", "lock", "using", "fixed",
    "catch", "try", "do", "return", "case", "when", "checked", "unchecked",
    "sizeof", "typeof", "nameof", "default", "stackalloc", "await", "throw", "new",
}

TYPE_DECL = re.compile(
    r"^\s*(?:\[[^\]]*\]\s*)*(?:public|internal|private|protected|sealed|static|abstract|partial|readonly|ref|unsafe|\s)*"
    r"\b(class|struct|record(?:\s+class|\s+struct)?|interface|enum)\s+([A-Za-z_]\w*)", re.M)

MEMBER_DECL = re.compile(
    r"(?m)^(?P<indent>[ \t]+)"
    r"(?P<sig>(?:\[[^\]]*\][ \t]*)*"
    r"(?:public|private|protected|internal|static|sealed|override|virtual|abstract|async|extern|unsafe|partial|new|readonly|volatile)[ \t\r\n]*"
    r"(?:public|private|protected|internal|static|sealed|override|virtual|abstract|async|extern|unsafe|partial|new|readonly|volatile)[ \t\r\n]*)*"
    r"(?:[A-Za-z_][\w<>\[\],\.\?\*]*[ \t]+)*"
    r"(?P<name>[A-Za-z_]\w*|~[A-Za-z_]\w*|this)\s*"
    r"(?P<generic><[^;{}()]*>)?\s*"
    r"\((?P<args>[^;{}]*?)\)\s*"
    r"(?::\s*[^{;]*?)?\s*"
    r"(?P<tail>\{|=>|$)")

ASSIGNMENT_LIKE = re.compile(r"[^=!<>+\-*/%&|^]=[^=>]")


@dataclass
class Method:
    rel: str
    name: str
    start_line: int
    end_line: int
    open_idx: int
    close_idx: int
    expr_bodied: bool

    @property
    def length(self) -> int:
        return self.end_line - self.start_line + 1


def depth_map(code: str) -> list[int]:
    depths, d = [0] * (len(code) + 1), 0
    for i, c in enumerate(code):
        depths[i] = d
        if c == '{':
            d += 1
        elif c == '}':
            d -= 1
    depths[len(code)] = d
    return depths


def methods_of(f: FileText) -> list[Method]:
    code = f.code
    depths = depth_map(code)
    out: list[Method] = []
    for m in MEMBER_DECL.finditer(code):
        name = m.group('name')
        if name in CONTROL_KEYWORDS:
            continue
        # 只认类型体内的成员（1 = 顶层类型成员，2 = 嵌套类型成员）——
        # 更深的必然是方法体内的控制流块，不是方法。
        if depths[m.start()] not in (1, 2):
            continue
        first_line = code[code.rfind('\n', 0, m.start()) + 1:
                           (lambda k: k if k >= 0 else len(code))(code.find('\n', m.start()))]
        if first_line.strip().startswith("new "):
            continue
        before_args = code[m.start():m.start('args')]
        if '=' in before_args or re.search(r"\bnew\b", before_args):
            continue  # 字段初始化器形状与方法签名相同，须排除
        tail = m.group('tail')
        if tail == '=>':
            k = code.find(';', m.end())
            end = k if k >= 0 else m.end()
            out.append(Method(f.rel, name, line_of(code, m.start()), line_of(code, end),
                              m.start(), end, True))
        elif tail == '{':
            oi = code.find('{', m.end() - 1)
            ci = brace_match(code, oi)
            if ci < 0:
                continue
            out.append(Method(f.rel, name, line_of(code, m.start()), line_of(code, ci),
                              oi, ci, False))
    return out


LOOP_START = re.compile(r"\b(for|foreach|while|do)\b[^{;]*[{(]")


def loop_spans(code: str) -> list[tuple[int, int]]:
    spans = []
    for m in LOOP_START.finditer(code):
        oi = code.find('{', m.start())
        if oi < 0:
            continue
        ci = brace_match(code, oi)
        if ci > 0:
            spans.append((oi, ci))
    return spans


LOCK_START = re.compile(r"\block\s*\([^)]*\)\s*\{")


def lock_spans(code: str) -> list[tuple[int, int]]:
    spans = []
    for m in LOCK_START.finditer(code):
        ci = brace_match(code, m.end() - 1)
        if ci > 0:
            spans.append((m.end() - 1, ci))
    return spans


CATCH_START = re.compile(r"\bcatch\b(?:\s*\([^)]*\))?\s*\{")


def catch_spans(code: str) -> list[tuple[int, int, int]]:
    """返回 (catch 关键字位置, body 起, body 止)。"""
    out = []
    for m in CATCH_START.finditer(code):
        ci = brace_match(code, m.end() - 1)
        if ci > 0:
            out.append((m.start(), m.end(), ci))
    return out


def inside(idx: int, spans: list[tuple[int, int]]) -> bool:
    return any(a < idx < b for a, b in spans)


# ───────────────────────────────────────────────────────────────────────────
# 3. 项定义：权重来自 9.6.1 / 9.6.2，锚点边界来自 9.6.4
# ───────────────────────────────────────────────────────────────────────────

@dataclass
class Item:
    id: str
    group: str            # 'A' | 'B'
    weight: int           # 必修=3 / 重要=2 / 建议=1
    tier: str             # 必修 / 重要 / 建议
    title: str
    denom_name: str
    b1: int               # 命中 ≤ b1 → 1
    b2: int               # b1 < 命中 ≤ b2 → 2；> b2 → 3
    mode: str             # 'auto' | 'auto+人工' | '人工'
    tool: bool = False
    cap: str | None = None   # 9.6.5 一票否决上限（该编号判 3 时所属维度上限）

# A 组 17 项（9.6.1）：必修 10×3 + 重要 7×2 → 满分 3×44 = 132
# B 组 15 项（9.6.2）：重要 7×2 + 建议 8×1 → 满分 3×22 = 66
ITEM_SPECS: list[Item] = [
    # ── A 组 ────────────────────────────────────────────────────────────
    Item("D-09", "A", 3, "必修", "空 catch / 吞异常", "catch 块总数", 2, 9, "auto", cap="C"),
    Item("D-11a", "A", 3, "必修", "非托管句柄未 Dispose", "非托管句柄创建位置数", 2, 9, "auto", tool=True, cap="C+"),
    Item("D-11b", "A", 3, "必修", "写流未 Flush / 未 Dispose", "写流创建位置数", 2, 9, "auto", tool=True, cap="C+"),
    Item("D-12", "A", 3, "必修", "长期存活对象未 Dispose", "长存活 IDisposable 字段数", 2, 9, "auto+人工"),
    Item("D-14", "A", 3, "必修", "UI 线程阻塞 / DoEvents", "UI 阻塞调用位置数", 2, 9, "auto", cap="C+"),
    Item("D-15", "A", 3, "必修", "跨线程直接改控件", "非 UI 线程写控件位置数", 2, 9, "auto+人工", cap="C+"),
    Item("D-19", "A", 3, "必修", "口令 MD5 / 明文；日志含敏感信息", "口令与敏感日志位置数", 2, 9, "auto", cap="C"),
    Item("D-22", "A", 3, "必修", "async void（事件处理器除外）", "async void 声明数", 2, 9, "auto", cap="C"),
    Item("D-23", "A", 3, "必修", "金额 / 财务计算用 double / float", "金额相关浮点位置数", 2, 9, "auto", cap="C"),
    Item("D-24", "A", 3, "必修", "字符串拼接 SQL / 命令", "命令 / 路径构造位置数", 2, 9, "auto", tool=True, cap="C"),
    Item("D-11d", "A", 2, "重要", "手动 Close 未包 finally", "手动 Close/Dispose 调用数", 2, 9, "auto"),
    Item("D-18", "A", 2, "重要", "Parse 不 TryParse", "Parse 调用位置数", 2, 9, "auto", tool=True, cap="C"),
    Item("D-20", "A", 2, "重要", "DateTime.Now 遍地 / Random 每次 new", "时间读取位置数", 2, 9, "auto", cap="C"),
    Item("D-25", "A", 2, "重要", "库代码缺 ConfigureAwait(false)", "库中 await 位置数", 2, 9, "auto"),
    Item("D-26", "A", 2, "重要", "LINQ 多次枚举", "IEnumerable 多遍遍历位置数", 2, 9, "auto+人工"),
    Item("D-27", "A", 2, "重要", "公共 API 的 null 契约不清", "公共 API 返回 null 位置数", 2, 9, "auto+人工"),
    Item("D-28", "A", 2, "重要", "lock 内做 I/O / 调外部服务", "lock 块总数（含 1 跳）", 2, 9, "auto"),
    # ── B 组 ────────────────────────────────────────────────────────────
    Item("D-01", "B", 2, "重要", "命名不可读（a / tmp / data / obj …）", "可疑短名 / 无义名位置数", 2, 9, "auto"),
    Item("D-03b", "B", 2, "重要", "常量 / 方法 / 类名大小写违例", "命名大小写违例位置数", 2, 9, "auto"),
    Item("D-07", "B", 2, "重要", "public static 全局可变状态", "可写静态成员数", 2, 9, "auto", cap="B-"),
    Item("D-10", "B", 2, "重要", "throw ex / 异常做流程控制", "throw ex 位置数", 2, 9, "auto"),
    Item("D-13", "B", 2, "重要", "Task.Run 包同步 / Thread.Sleep 等待", "该形态位置数", 2, 9, "auto", tool=True),
    Item("D-17", "B", 2, "重要", "ToLower 比较 / 区域敏感比较", "字符串比较位置数", 2, 9, "auto"),
    Item("D-21", "B", 2, "重要", "未启用 NRT / 忽略警告", "未附理由抑制 + 警告数", 2, 9, "auto", cap="B-"),
    Item("D-02", "B", 1, "建议", "匈牙利命名（strName / iCount / btnSubmit）", "匈牙利命名位置数", 2, 9, "auto"),
    Item("D-03a", "B", 1, "建议", "私有字段 m_ 前缀", "m_ 前缀字段数", 2, 9, "auto"),
    Item("D-04", "B", 1, "建议", "控件 / 事件序号命名", "序号命名位置数", 2, 9, "auto"),
    Item("D-05", "B", 1, "建议", "using 位置 / 命名空间与目录不符", "文件数", 2, 9, "auto", tool=True),
    Item("D-06", "B", 1, "建议", "一文件多类 / 超长方法 / region 滥用", "多类型文件 + 超 60 行方法", 2, 9, "auto", tool=True),
    Item("D-08", "B", 1, "建议", "静态类遍地 / 依赖直接 new", "静态类 + 硬编码 new 位置数", 2, 9, "auto+人工", cap="B-"),
    Item("D-11c", "B", 1, "建议", "纯托管 IDisposable 未 Dispose", "纯托管 IDisposable 创建位置数", 2, 9, "auto"),
    Item("D-16", "B", 1, "建议", "循环内字符串拼接", "循环内拼接位置数", 2, 9, "auto", tool=True),
]

SPEC_BY_ID = {s.id: s for s in ITEM_SPECS}
A_IDS = [s.id for s in ITEM_SPECS if s.group == "A"]
B_IDS = [s.id for s in ITEM_SPECS if s.group == "B"]
CAPS = {s.id: s.cap for s in ITEM_SPECS if s.cap}


# ───────────────────────────────────────────────────────────────────────────
# 4. 逐项丈量
# ───────────────────────────────────────────────────────────────────────────

@dataclass
class Measure:
    hits: list[F]
    denom: int
    note: str = ""
    qualifier: int | None = None   # 定性升级线（如"存在 300+ 行方法 → 至少 2"）
    forced: int | None = None      # 9.6.4 逐项锚点**定性**时直接给定分数（优先于计数档）
    gate_hits: list[F] | None = None   # 硬门禁只看这一份（默认与 hits 相同）
                                #   例：D-09 计分含「吞异常」，门禁只认「空 catch / 静默 return null」；
                                #       D-14 计分含软落点，门禁只认 PushFrame / DoEvents。


HANDLE_TYPES = [
    "FileStream", "Socket", "TcpClient", "TcpListener", "UdpClient", "NetworkStream",
    "DbConnection", "DbCommand", "SqlConnection", "SqlCommand", "OdbcConnection",
    "OleDbConnection", "Bitmap", "HttpClient", "HttpResponseMessage", "RegistryKey",
    "Process", "FileSystemWatcher", "Mutex", "NamedPipeServerStream",
    "AnonymousPipeServerStream", "SafeFileHandle", "PerformanceCounter", "EventLog",
    "X509Certificate2", "RSACryptoServiceProvider", "CryptoStream",
]
WRITE_STREAM_TYPES = ["StreamWriter", "BinaryWriter", "CryptoStream"]
MANAGED_DISPOSABLE_TYPES = [
    "MemoryStream", "StringContent", "StringWriter", "StreamContent",
    "ByteArrayContent", "FormUrlEncodedContent", "SemaphoreSlim",
    "ManualResetEventSlim", "CancellationTokenSource", "MemoryCache",
]
WRITE_FILEMODE = re.compile(r"FileMode\.(Create|CreateNew|Append|OpenOrCreate|Truncate)")
HANDLE_RE = re.compile(r"\bnew\s+(" + "|".join(HANDLE_TYPES) + r")\s*\(")
WRITE_RE = re.compile(
    r"\bnew\s+(" + "|".join(WRITE_STREAM_TYPES) + r")\s*\("
    r"|\bnew\s+FileStream\s*\([^)]*")
MANAGED_RE = re.compile(r"\bnew\s+(" + "|".join(MANAGED_DISPOSABLE_TYPES) + r")\s*\(")
ASSIGN_NAME = re.compile(r"([A-Za-z_]\w*)\s*=\s*new\b")


def _scope_of(methods: list[Method], idx: int, code: str) -> tuple[int, int]:
    for m in methods:
        if m.open_idx < idx < m.close_idx:
            return m.open_idx, m.close_idx
    return 0, len(code)


def _release_evidence(seg: str, var: str) -> bool:
    """一段文本里是否出现对 var 的释放动作（含所有权移交形态）。"""
    pats = [
        rf"\busing\s*\(\s*var\s+{var}\b",
        rf"\busing\s+var\s+{var}\b",
        rf"\busing\s*\([^)]*\b{var}\s*\)",
        rf"\b{re.escape(var)}\s*\??\.\s*(?:Dispose|Close|Disconnect|Kill|Cancel)\s*\(",
    ]
    return any(re.search(p, seg) for p in pats)


def _repo_release_index(files: list[FileText]) -> set[str]:
    """全仓扫一遍「被释放过的标识符」，用于识别**所有权移交**。

    D-12 的 0 档是「长存活对象由所有者 / 容器统一释放」——所有权移交**不是泄漏**。
    实测例子：`Session._pipe` 全文件无 `_pipe.Dispose()`，但接受循环在
    `Program.cs` 里 `ContinueWith(_ => pipe.Dispose())` 并显式注释「所有权移交」。
    口径：标识符去掉前导 `_` 后若能找到释放动作，即认为有主。
    """
    released: set[str] = set()
    for f in files:
        for m in re.finditer(r"\b([A-Za-z_]\w*)\s*\??\.\s*(?:Dispose|Close|Disconnect|Kill|Cancel)\s*\(", f.code):
            released.add(m.group(1).lstrip('_'))
        for m in re.finditer(r"\busing\s*(?:\(|var\s+)(?:var\s+)?([A-Za-z_]\w*)", f.code):
            released.add(m.group(1).lstrip('_'))
    return released


def measure_disposables(files: list[FileText]) -> dict[str, Measure]:
    """D-11a / D-11b / D-11c / D-11d 同时丈量（D-12 的候选另出）。"""
    released = _repo_release_index(files)
    han_hits, wr_hits, mg_hits, c_hits = [], [], [], []
    han_n = wr_n = mg_n = c_n = 0
    field_unds: list[F] = []
    field_n = 0
    for f in files:
        code = f.code
        ms = methods_of(f)
        depths = depth_map(code)
        # `Dispose()` 本身、或 `DisposeXxx` 释放方法，是**释放路径**而非「未包 finally」
        dispose_spans = [(m.open_idx, m.close_idx) for m in ms
                         if re.match(r"(?:Dispose|Cleanup|Release)", m.name)]
        using_spans = _using_spans(code)
        finally_spans = _finally_spans(code)
        catch_body_spans = [(b0, b1) for _s, b0, b1 in catch_spans(code)]

        for label, regex, bucket in (("han", HANDLE_RE, []), ("wr", WRITE_RE, []), ("mg", MANAGED_RE, [])):
            for m in regex.finditer(code):
                line = code[code.rfind('\n', 0, m.start()) + 1:code.find('\n', m.start())]
                if label == "wr":
                    if 'FileStream' in m.group(0) and not WRITE_FILEMODE.search(line):
                        continue
                    if 'CryptoStream' in m.group(0) and 'Write' not in line:
                        continue
                if label == "han":
                    han_n += 1
                elif label == "wr":
                    wr_n += 1
                else:
                    mg_n += 1
                v = _released_or_owner(f, code, m, ms, depths, released, field_unds)
                if not v:
                    (han_hits if label == "han" else wr_hits if label == "wr" else mg_hits).append(
                        _mk(f, code, m.start()))

        # D-11d：手动 Close()/Dispose() 调用。9.6.4 的实质是「异常路径会跳过」——
        # 只有当**同一方法内自己获取**了这个资源、又没有用 using / finally 兜住时才成立。
        # 实测教训：不做这一层过滤会把「停机清理」「Dispose() 幂等自检」「注入对象的拆除」
        # 一并误判（13 处全为误报），D-11d 从 0 虚报成 3。
        for cm in re.finditer(r"\b([A-Za-z_]\w*)\s*\??\.\s*(?:Close|Dispose)\s*\(\s*\)", code):
            c_n += 1
            if (inside(cm.start(), finally_spans) or inside(cm.start(), using_spans)
                    or inside(cm.start(), dispose_spans)):
                continue
            var = cm.group(1)
            lo, hi = _scope_of(ms, cm.start(), code)
            body = code[lo:hi]
            acquired = re.search(
                rf"(?m)^[^\n]*\b{re.escape(var)}\s*=\s*new\b[^\n]*$", body)
            if not acquired:
                continue                      # 不是本方法内自己获取的（注入 / 字段 / 停机清理）
            if re.search(r"\busing\b", acquired.group(0)):
                continue
            # ① 落在 `catch` 块里 = 恰恰是在**异常路径上**释放，与"异常路径会跳过"相反
            if inside(cm.start(), catch_body_spans):
                continue
            # ② 所有权移交：释放后显式置 null（本仓写作 `pipe = null; // 所有权移交`）
            after = code[cm.start():cm.start() + 400]
            if re.search(rf"\b{re.escape(var)}\s*=\s*null\s*;", after):
                continue
            c_hits.append(_mk(f, code, cm.start()))

        # 字段级 IDisposable（D-12 候选）
        for m in re.finditer(
                r"(?m)^[ \t]+(?:private|internal|public|protected)[ \t]+"
                r"(?:static[ \t]+)?(?:readonly[ \t]+)?("
                + "|".join(HANDLE_TYPES + WRITE_STREAM_TYPES + MANAGED_DISPOSABLE_TYPES) +
                r")[ \t]+([A-Za-z_]\w*)[ \t]*;", code):
            if depths[m.start()] != 1:
                continue
            field_n += 1
            name = m.group(2).lstrip('_')
            if name in released or f.code.count(name) and re.search(
                    rf"\b{re.escape(name)}\s*\??\.\s*(?:Dispose|Close|Disconnect)\s*\(", f.raw):
                continue  # 本文件内有主
            field_unds.append(_mk(f, code, m.start()))

    return {
        "D-11a": Measure(han_hits, han_n, "句柄类型：" + "、".join(HANDLE_TYPES[:8]) + " 等"),
        "D-11b": Measure(wr_hits, wr_n, "写流：StreamWriter / BinaryWriter / 写入态 FileStream / 写入态 CryptoStream"),
        "D-11c": Measure(mg_hits, mg_n, "纯托管：MemoryStream / StringContent / SemaphoreSlim 等（GC 可兜底，**不等于泄漏**）"),
        "D-11d": Measure(c_hits, c_n,
                         "分母 = 手动 .Close()/.Dispose() 调用数；包在 `finally` / `using` / `Dispose()` 方法体内即合规"
                         "（9.6.4 逐项锚点明文：不包才扣分）。适用位置另要求「同方法内自行获取且未用 using」——"
                         "注入对象的拆除、停机清理、**异常路径上的释放**（catch 块内）、**所有权移交**（释放后置 null）"
                         "都不属本项"),
        "_D12": Measure(field_unds, field_n,
                        "分母 = 字段级 IDisposable 数；已按「所有权移交」还原——全仓找得到释放动作的标识符视为有主，不判违规"),
    }


def _using_spans(code: str) -> list[tuple[int, int]]:
    """`using (...)` 与 `using { ... }` 两种形态的块范围（必须配平圆括号，不能直接配花括号）。"""
    spans = []
    for um in re.finditer(r"\busing\s*[\(\{]", code):
        start = um.end() - 1
        if code[start] == '{':
            ci = brace_match(code, start)
        else:
            depth, i = 1, start + 1
            while i < len(code) and depth:
                if code[i] == '(':
                    depth += 1
                elif code[i] == ')':
                    depth -= 1
                i += 1
            oi = code.find('{', i - 1)
            if oi < 0:
                continue
            ci = brace_match(code, oi)
        if ci > 0:
            spans.append((start, ci))
    return spans


def _finally_spans(code: str) -> list[tuple[int, int]]:
    spans = []
    for fm in re.finditer(r"\bfinally\s*\{", code):
        ci = brace_match(code, fm.end() - 1)
        if ci > 0:
            spans.append((fm.end() - 1, ci))
    return spans


def _released_or_owner(f: FileText, code: str, m: re.Match, ms: list[Method],
                       depths: list[int], released: set[str],
                       field_unds: list[F]) -> bool:
    """某个 `new` 点是否已释放（或所有权已移交）。返回 True = 合规。"""
    line_start = code.rfind('\n', 0, m.start()) + 1
    line_end = code.find('\n', m.start())
    line = code[line_start:(line_end if line_end > 0 else len(code))]
    if re.search(r"\busing\b", line[:m.start() - line_start]):
        return True
    am = ASSIGN_NAME.search(code[line_start:m.start()])
    is_field = depths[m.start()] == 1
    if am:
        var = am.group(1)
        if _release_evidence(code, var):
            return True
        if var.lstrip('_') in released:
            return True           # 有主（本仓某处释放了它）
        if is_field:
            field_unds.append(_mk(f, code, m.start()))
            return True           # 交 D-12 判定
        return False
    # 无法定位接收者：看整条语句或外层方法是否有 using
    stmt_end = code.find(';', m.end())
    stmt = code[line_start:(stmt_end if stmt_end > 0 else len(code))]
    if re.search(r"\busing\b", stmt):
        return True
    lo, hi = _scope_of(ms, m.start(), code)
    if re.search(r"\busing\b", code[max(0, lo - 200):lo + 1]):
        return True
    if is_field:
        field_unds.append(_mk(f, code, m.start()))
        return True
    return True  # 定位不到 → 不判违规（宁少报，证据清单仍列出位置供人工核）


def _mk(f: FileText, code: str, idx: int) -> F:
    ln = line_of(code, idx)
    return F(f.rel, ln, f.lines[ln - 1] if ln - 1 < len(f.lines) else "")


LOG_CALL = re.compile(r"\b(?:Console\.(?:Error|Out)\.WriteLine|Debug\.WriteLine|Trace\.WriteLine"
                      r"|Log\w*\s*\(|_?log\w*\s*\.\s*Write|WriteLine\s*\()")
# 「已处理」的判据：向上传播 / 返回**调用方可见的**失败结果 / 把异常信息送出去
# （日志或界面）/ 登记降级或诊断。
# 不还原这些会虚报：`catch (Exception ex) { _outputBox.Text = "[错误] " + ex.Message; }`
# 是把失败摆到用户眼前，不是吞。
HANDLED = re.compile(
    r"\bthrow\b|PssCode\.\w+|ex\s*\.\s*(?:Message|ToString)"
    r"|[Dd]iagnostic|\bAddError\b|[Dd]egrade|降级|TrySetResult|SetResult")
# 「只是一条裸出口语句」——除了 return/break/continue 之外什么都没做
BARE_EXIT_ONLY = re.compile(r"^\s*(?:(?:return(?:\s+[^;]*)?|break|continue)\s*;\s*)+$")
CANCEL_TYPE = re.compile(r"catch\s*\(\s*(?:System\.)?(?:OperationCanceledException|TaskCanceledException)")


def _has_reason_comment(raw: str, start: int, end: int, method_doc: bool) -> bool:
    """catch 附近是否有**条款依据 / 降级说明**注释。

    窗口取「最近的 `try` 之上 24 行 → catch 结束」：依据注释通常写在 `try` 之上，
    而不是写在 `catch` 里。实测教训：窗口只取 `catch` 前 12 行时，
    `SafeGuardClient.Whitelist.cs` 的 `// 5.2 第 ⑤ 条`（在 `try` 上方 11 行）被漏掉，
    该处被误判成"完全空 catch"。所在方法带 XML 文档（`///`）同样视为契约已声明。
    """
    if method_doc:
        return True
    lo = raw.rfind('\n', 0, max(0, start))
    idx, seen = lo, 0
    while idx > 0 and seen < 30:
        ls = raw.rfind('\n', 0, idx - 1) + 1
        if re.search(r"^\s*try\b", raw[ls:idx]):
            lo = ls
            break
        idx = ls
        seen += 1
    for _ in range(24):
        prev = raw.rfind('\n', 0, lo)
        if prev < 0:
            lo = 0
            break
        lo = prev
    seg = raw[lo:end]
    return '//' in seg or '/*' in seg


def measure_d09(files: list[FileText]) -> Measure:
    """9.6.4 D-09 锚点：3 = 存在 `catch (Exception) { }` 或**静默 return null**；
    2 = 吞异常（有语句但既不记日志、也不向上传播、也不给出可见失败结果、也无依据说明）；
    1 = 有 catch 且记录/说明但恢复不完整。

    两处按语言与规范语义还原（不还原会虚报）：
      · **协作式取消**：`catch (OperationCanceledException) { return; }` 是 .NET 的标准
        停机形态；取消不是"被藏起来的错误"，不计违规；
      · **可见失败结果**：`catch (Exception ex) { return new PssResult(PssCode.Internal, …); }`
        是把失败交给调用方，属"处理"，不属"吞"。
    """
    hits3, hits2, n = [], [], 0
    for f in files:
        code, raw = f.code, f.raw
        ms = methods_of(f)
        for s, b0, b1 in catch_spans(code):
            n += 1
            body_code = code[b0:b1]
            method_doc = False
            for mm in ms:
                if mm.open_idx < s < mm.close_idx:
                    head = "\n".join(f.lines[max(0, mm.start_line - 8):max(0, mm.start_line - 1)])
                    method_doc = '///' in head
                    break
            has_comment = _has_reason_comment(raw, s, b1, method_doc)
            has_stmt = bool(body_code.strip())
            has_log = bool(LOG_CALL.search(body_code)) or bool(HANDLED.search(body_code))
            ln = line_of(code, s)
            head_line = f.lines[ln - 1] if ln - 1 < len(f.lines) else ""
            only_exit = (bool(re.fullmatch(r"\s*(?:return|break|continue)\s*;\s*", body_code))
                         or not has_stmt)
            if CANCEL_TYPE.search(head_line) and only_exit:
                continue                     # 协作式取消：合规
            silent_null = (bool(re.fullmatch(r"\s*return\s+null\s*;\s*", body_code))
                           or bool(re.fullmatch(r"\s*return\s+;\s*", body_code))) and not has_comment
            if (not has_stmt and not has_comment) or silent_null:
                hits3.append(_mk(f, code, s))
            elif not has_comment and not has_log and BARE_EXIT_ONLY.match(body_code):
                # 「吞异常」：函数体除了一条裸出口之外什么都没做——既不记日志、
                # 也不把 ex 送出去、也不给出可见的失败结果。判定边界写得窄且可复算：
                # 只要函数体里还有**任何别的语句**（调处理器、写界面、置字段），
                # 就归人工检查表判，不由脚本臆断。
                hits2.append(_mk(f, code, s))
    note = (f"分母 = catch 块总数；判 3 情形（完全空且无说明 / 静默 return）{len(hits3)} 处；"
            f"判 2 情形（有语句但无日志、无可见失败结果、无依据说明）{len(hits2)} 处。"
            f"本项 9.6.4 给的是**定性**锚点，故不按 9.6.3 的计数档走。已还原两处语义："
            f"协作式取消（OperationCanceledException + return/break）不计；"
            f"把失败交给调用方（PssCode.Internal 等可见错误结果）不计")
    if hits3:
        note += "；命中判 3 情形 → 直接判 3，并触发 9.6.5 一票否决（上限 C）"
    forced = 3 if hits3 else (2 if hits2 else 0)
    return Measure(hits3 + hits2, n, note, forced=forced, gate_hits=hits3)


ASYNC_VOID = re.compile(r"\basync\s+void\s+([A-Za-z_]\w*)\s*\(")
EVENT_HANDLER = re.compile(r"\b(object|EventArgs|RoutedEventArgs|CancelEventArgs|"
                           r"MouseButtonEventArgs|KeyEventArgs|DragEventArgs|"
                           r"SelectionChangedEventArgs|TextChangedEventArgs)\b")


def measure_d22(files: list[FileText]) -> Measure:
    hits, n = [], 0
    for f in files:
        code = f.code
        for m in ASYNC_VOID.finditer(code):
            name = m.group(1)
            n += 1
            # 事件处理器判据：① 名字以 _ 结尾形态（Xxx_Yyy）② 参数含 EventArgs ③ 方法签名带 += 绑定
            sig_end = code.find(')', m.end())
            sig = code[m.start():sig_end + 1] if sig_end > 0 else code[m.start():m.start() + 240]
            is_handler = bool(re.search(r"\w+_\w+", name)) or bool(EVENT_HANDLER.search(sig))
            if not is_handler:
                hits.append(_mk(f, code, m.start()))
    return Measure(hits, n,
                   "分母 = async void 声明数；事件处理器（Xxx_Yyy 命名或含 EventArgs 参数）已扣除")


TIME_PATTERN = re.compile(r"\bDateTime(?:Offset)?\.(?:Now|UtcNow|Today)\b")
RANDOM_NEW = re.compile(r"\bnew\s+Random\s*\(")


def measure_d20(files: list[FileText]) -> Measure:
    hits, n = [], 0
    for f in files:
        if CLOCK_ALLOWLIST.search(f.rel):
            continue
        for idx, line in enumerate(f.code_lines, 1):
            if TIME_PATTERN.search(line) or RANDOM_NEW.search(line):
                n += 1
                hits.append(F(f.rel, idx, f.lines[idx - 1]))
    return Measure(hits, n,
                   "分母 = 时间读取位置数（时钟实现 4 个类已白名单豁免）+ Random 每次 new 的位置数")


DISK_SYMBOL = re.compile(
    r"\bFile\.|\bDirectory\.|\bFileStream\b|\bStreamWriter\b|\bBinaryWriter\b|\bFileInfo\b"
    r"|\bSHA256\b|\bIncrementalHash\b|\bPipe\b|\bSocket\b|\bHttpClient\b|\bProcess\.")


def measure_d28(files: list[FileText]) -> Measure:
    # 先建「方法名 → 体内是否触达磁盘/外部服务」索引
    md: dict[str, bool] = {}
    for f in files:
        for m in methods_of(f):
            body = f.code[m.open_idx:m.close_idx]
            md[m.name] = md.get(m.name, False) or bool(DISK_SYMBOL.search(body))
    hits, n = [], 0
    for f in files:
        code = f.code
        for a, b in lock_spans(code):
            n += 1
            body = code[a:b]
            direct = bool(DISK_SYMBOL.search(body))
            indirect = sorted({c for c in re.findall(r"\b([A-Za-z_]\w*)\s*\(", body) if md.get(c)})
            if direct or indirect:
                why = "直接" if direct else "经 1 跳 " + ",".join(indirect)
                hits.append(F(f.rel, line_of(code, a), f"[{why}] " +
                              (f.lines[line_of(code, a) - 1] if line_of(code, a) - 1 < len(f.lines) else "")))
    return Measure(hits, n, "分母 = lock 块总数；命中含「沿 1 跳私有方法调用链触达磁盘 / 网络 / 进程」")


TASK_RUN = re.compile(r"\bTask\.Run\b")
THREAD_SLEEP = re.compile(r"\bThread\.Sleep\b")
# 「Task.Run 包同步方法」的两种可判形态：传方法组/委托变量（而非 lambda），
# 或后面直接 .Result / .Wait() / GetAwaiter().GetResult() 同步阻塞。
TASK_RUN_GROUP = re.compile(r"\bTask\.Run\s*\(\s*([A-Za-z_]\w*)\s*\)")
SYNC_BLOCK = re.compile(r"\.\s*(?:Result|Wait)\s*\(|GetAwaiter\s*\(\s*\)\s*\.\s*GetResult\s*\(")


def measure_d13(files: list[FileText]) -> Measure:
    """D-13 的两种形态；`Task.Run(async () => ...)` / `Task.Run(() => AsyncLoop())`
    是**正常的异步启动**，不是「包同步方法」，已按语言语义还原排除。"""
    hits, n = [], 0
    for f in files:
        code = f.code
        for idx, line in enumerate(f.code_lines, 1):
            if THREAD_SLEEP.search(line):
                n += 1
                hits.append(F(f.rel, idx, f.lines[idx - 1]))
        for m in TASK_RUN_GROUP.finditer(code):
            n += 1
            hits.append(_mk(f, code, m.start()))
        for m in TASK_RUN.finditer(code):
            se = code.find(';', m.end())
            seg = code[m.start():(se if se > 0 else m.end() + 200)]
            if SYNC_BLOCK.search(seg):
                n += 1
                hits.append(_mk(f, code, m.start()))
    seen, uniq = set(), []
    for h in hits:
        if (h.rel, h.line) not in seen:
            seen.add((h.rel, h.line))
            uniq.append(h)
    return Measure(uniq, len(uniq),
                   "分母 = Thread.Sleep 位置数 + `Task.Run(方法组)` 位置数 + `Task.Run(...).Result/.Wait()` 位置数；"
                   "`Task.Run(async () => …)` 属正常异步启动，不计")


UI_BLOCK = re.compile(r"Dispatcher\.PushFrame|\bPushFrame\s*\(|\bApplication\.DoEvents\b|\bDoEvents\s*\(")
# 软落点：需人工确认是否真在 UI 线程。
# **不含裸 `.Wait()`**——`SemaphoreSlim` / `Mutex` / 事件对象的取锁 API 也叫 `Wait()`
# （本仓 `DeviceActivation.Io.Wait()` 就是 SemaphoreSlim 取锁），纯文本分辨不出接收者类型，
# 强判会把 D-14 从 0 虚报成 1 档。
UI_BLOCK_LOOSE = re.compile(
    r"\bThread\.Sleep\b|\.\s*Result\b|GetAwaiter\s*\(\s*\)\s*\.\s*GetResult\s*\(|"
    r"\bTask\.Wait(?:All|Any)?\s*\(")


def measure_d14(files: list[FileText]) -> Measure:
    hard, soft, n = [], [], 0
    for f in files:
        if not f.is_ui:
            continue
        for idx, line in enumerate(f.code_lines, 1):
            if UI_BLOCK.search(line):
                n += 1
                hard.append(F(f.rel, idx, f.lines[idx - 1]))
            elif UI_BLOCK_LOOSE.search(line):
                n += 1
                soft.append(F(f.rel, idx, f.lines[idx - 1]))
    note = (f"分母 = UI 工程内阻塞调用位置数；硬落点（PushFrame / DoEvents）{len(hard)} 处，"
            f"软落点（Thread.Sleep / .Result / GetAwaiter().GetResult()）{len(soft)} 处"
            f"——软落点需人工确认是否在 UI 线程；裸 `.Wait()` 不计（无法区分 SemaphoreSlim 取锁）")
    return Measure(hard + soft, n, note, qualifier=3 if hard else None, gate_hits=hard)


CROSS_THREAD_START = re.compile(r"\bTask\.Run\b|\bThreadPool\.QueueUserWorkItem\b|\bnew\s+Thread\s*\(")
CONTROL_WRITE = re.compile(r"\b(?:[A-Za-z_]\w*\.)?"
                           r"(Text|Content|ItemsSource|IsEnabled|Visibility|SelectedIndex|"
                           r"SelectedItem|Value|IsChecked|Background|Foreground)\s*=")


def measure_d15(files: list[FileText]) -> Measure:
    hits = []
    for f in files:
        if not f.is_ui:
            continue
        code = f.code
        for m in CROSS_THREAD_START.finditer(code):
            oi = code.find('{', m.end())
            if oi < 0:
                continue
            ci = brace_match(code, oi)
            if ci < 0:
                continue
            for w in CONTROL_WRITE.finditer(code[oi:ci]):
                hits.append(_mk(f, code, oi + w.start()))
    return Measure(hits, len(hits) if hits else 0,
                   "分母 = 非 UI 线程入口（Task.Run / ThreadPool / new Thread）体内的控件属性赋值位置数；"
                   "本项为启发式，须人工复核（9.6.3 抽样义务）")


PARSE_RE = re.compile(r"\b(?:int|long|double|float|decimal|DateTime|DateTimeOffset|TimeSpan|"
                      r"byte|short|Guid|bool)\.Parse\s*\(")


def measure_d18(files: list[FileText]) -> Measure:
    hits = []
    for f in files:
        for idx, line in enumerate(f.code_lines, 1):
            for _m in PARSE_RE.finditer(line):
                hits.append(F(f.rel, idx, f.lines[idx - 1]))
    return Measure(hits, len(hits), "分母 = Parse 调用位置数（外部输入解析为严重档落点）")


MD5_RE = re.compile(r"\bMD5\b|\bSHA1\b|\bMD5CryptoServiceProvider\b")
# 只认真正的凭据载体词；`密钥` 太泛（许可证工具生成密钥文件是正常输出），不计
CRED_WORD = r"(?:password|passwd|pwd|secret|token|credential|apikey|api_key|privatekey|口令|密码|私钥|凭据)"
LOG_CALL_ANY = re.compile(r"\b(?:Console\.(?:Error|Out)?\.?WriteLine|MessageBox\.Show|"
                          r"Debug\.WriteLine|Trace\.WriteLine|Log\w*)\s*\(")


def _logged_dynamic_credential(arg: str) -> bool:
    """日志实参里是否**真的把凭据的值**写了出去。

    口径（避免误报）：仅当凭据词作为**标识符**出现在插值孔 `{xxx}` 或字符串拼接
    `+ xxx` 中才算命中。`Console.WriteLine("已生成密钥对：")` 这类纯名词文案不算——
    实测中它把 D-19 从 0 虚报成 3。
    """
    for hole in re.finditer(r"\{([^{}:]*)\}", arg):
        if re.search(CRED_WORD, hole.group(1), re.I):
            return True
    for cat in re.finditer(r"\+\s*([A-Za-z_]\w*)", arg):
        if re.search(CRED_WORD, cat.group(1), re.I):
            return True
    return False


def _first_arg(code: str, open_idx: int) -> str:
    depth, i = 1, open_idx + 1
    while i < len(code) and depth:
        if code[i] == '(':
            depth += 1
        elif code[i] == ')':
            depth -= 1
        i += 1
    return code[open_idx + 1:i - 1]


def measure_d19(files: list[FileText]) -> Measure:
    hits = []
    for f in files:
        for m in MD5_RE.finditer(f.lit):
            hits.append(_mk(f, f.lit, m.start()))
        for m in LOG_CALL_ANY.finditer(f.lit):
            arg = _first_arg(f.lit, m.end() - 1)
            if _logged_dynamic_credential(arg):
                hits.append(_mk(f, f.lit, m.start()))
    return Measure(hits, len(hits),
                   "分母 = MD5/SHA1 使用位置数 + 把凭据值写进日志/弹窗的位置数；"
                   "已排除「文案里只出现凭据名词、未写值」的误报")


MONEY_WORD = re.compile(r"(Amount|Price|Total|Sum|Fee|Cost|Balance|Salary|金额|价格|总额|合计|费|余额|利息)", re.I)
FLOAT_DECL = re.compile(r"\b(?:double|float)\s+([A-Za-z_]\w*)")


def measure_d23(files: list[FileText]) -> Measure:
    hits = []
    for f in files:
        for idx, line in enumerate(f.code_lines, 1):
            for m in FLOAT_DECL.finditer(line):
                if MONEY_WORD.search(m.group(1)):
                    hits.append(F(f.rel, idx, f.lines[idx - 1]))
            if MONEY_WORD.search(line) and re.search(r"\b(double|float)\b", line):
                if not any(h.rel == f.rel and h.line == idx for h in hits):
                    hits.append(F(f.rel, idx, f.lines[idx - 1]))
    return Measure(hits, len(hits), "分母 = 金额/财务语义标识符与 double/float 同现的位置数")


CMD_RE = re.compile(r"\b(Process|ProcessStartInfo)\b|SqlCommand|OleDbCommand|OdbcCommand|"
                    r"cmd\.exe|powershell|/c\s")
CONCAT_IN_CMD = re.compile(r"\+\s*[A-Za-z_\"$]|\$\"")


def measure_d24(files: list[FileText]) -> Measure:
    hits, n = [], 0
    for f in files:
        for m in re.finditer(r"\bnew\s+(?:ProcessStartInfo|SqlCommand|OleDbCommand|OdbcCommand)\s*\(", f.code):
            n += 1
            ss = f.code.rfind('\n', 0, m.start()) + 1
            se = f.code.find(';', m.end())
            seg = f.code[ss:(se if se > 0 else len(f.code))]
            if CONCAT_IN_CMD.search(seg):
                hits.append(_mk(f, f.code, m.start()))
    return Measure(hits, n, "分母 = ProcessStartInfo / *Command 构造位置数；命中 = 实参含字符串拼接")


TO_COMPARE = re.compile(r"\.(?:ToLower|ToUpper)\s*\(\s*\)\s*(?:==|!=|\.Equals)|"
                        r"(?:==|!=)\s*[A-Za-z_]\w*\.(?:ToLower|ToUpper)\s*\(\s*\)")
# 只用 `string.Compare`：它没有默认重载，缺 StringComparison 即区域敏感。
# 实测教训：早先把所有 `.CompareTo(` 都算进来，把
# `result.Sort((a, b) => a.Time.CompareTo(b.Time))`（DateTimeOffset 比较）误判成
# 字符串区域敏感比较——不还原接收者的类型，D-17 会从 0 虚报成 1。
CULTURE_COMPARE = re.compile(r"\bstring\.Compare\s*\(")


def measure_d17(files: list[FileText]) -> Measure:
    hits, n = [], 0
    info_compareto = 0
    for f in files:
        for idx, line in enumerate(f.code_lines, 1):
            if TO_COMPARE.search(line):
                n += 1
                hits.append(F(f.rel, idx, f.lines[idx - 1]))
                continue
            m = CULTURE_COMPARE.search(line)
            if m:
                n += 1
                if 'StringComparison' not in line and 'Ordinal' not in line:
                    hits.append(F(f.rel, idx, f.lines[idx - 1]))
            if '.CompareTo(' in line:
                info_compareto += 1
    return Measure(hits, n,
                   "分母 = `string.Compare`（无 StringComparison）位置数 + ToLower/ToUpper 比较位置数；"
                   "已按语言语义还原：字符串 `==` 与 `Equals(a,b)` 默认即 Ordinal 不计；"
                   f"`.CompareTo(` 共 {info_compareto} 处**不直接计入**（须看接收者类型，且 CA1310 已在 error 档把关）；"
                   "[工具] CA1310 / CA1307 / CA1305 已设为 error 档，构建 0 警告即该项的工具证据")


def measure_d07(files: list[FileText]) -> Measure:
    pat = re.compile(r"\bpublic\s+static\s+(?!readonly|const|class|void|int\s+Main)"
                     r"[\w<>?\[\],\s\.]+?\s+[A-Za-z_]\w*\s*\{\s*get;\s*set;\s*\}")
    hits = []
    for f in files:
        for idx, line in enumerate(f.code_lines, 1):
            if pat.search(line):
                hits.append(F(f.rel, idx, f.lines[idx - 1]))
    return Measure(hits, len(hits), "分母 = public static 可写属性/字段位置数")


def measure_d25(files: list[FileText]) -> Measure:
    hits, n = [], 0
    for f in files:
        if not f.is_lib:
            continue
        code = f.code
        for m in re.finditer(r"\bawait\b", code):
            # 取该 await 所在语句（到最近的 `;` 或行尾）
            se = code.find(';', m.end())
            seg = code[m.start():(se if se > 0 else len(code))]
            n += 1
            if 'ConfigureAwait' not in seg:
                hits.append(_mk(f, code, m.start()))
    return Measure(hits, n, "分母 = 库工程中 await 位置数；UI 工程（Prt.App / Prt.Prta）不计入（9.6.1 明文）")


def measure_d26(files: list[FileText]) -> Measure:
    """只在**声明为 `IEnumerable<T>` / `IQueryable<T>` 的形参或局部**上判定。

    9.6.4 的 0 档是「`IEnumerable` 参数只用一遍；需多次使用先实体化」——
    `IReadOnlyList<T>` / `List<T>` / `T[]` / 已知是属性（如 `SortedDiagnostics`）
    本来就已实体化，遍历两次不构成多次枚举。
    实测教训：不区分类型，会把 `document.SortedDiagnostics.Count(d => …)` 这类
    属性上的 LINQ 聚合误判成违规。
    """
    hits, n = [], 0
    for f in files:
        code = f.code
        lazy_vars = set(re.findall(r"\b(?:IEnumerable|IQueryable)\s*<\s*[\w\s,<>\[\]\?\.]+>\s+([A-Za-z_]\w*)", code))
        lazy_vars |= set(re.findall(r"\bvar\s+([A-Za-z_]\w*)\s*=\s*[A-Za-z_]\w*\.(?:Where|Select|Take|Skip|OrderBy|OrderByDescending|Distinct|Concat|Cast|OfType)\b", code))
        n += len(lazy_vars)
        for var in sorted(lazy_vars):
            agg = re.search(r"\b" + re.escape(var) + r"\s*\.\s*(?:Count|Any|First|Single)\s*\(", code)
            if not agg:
                continue
            tail = code[agg.end():]
            if (re.search(r"\bforeach\s*\([^)]*\bin\s+" + re.escape(var) + r"\b", tail)
                    or re.search(r"\b" + re.escape(var) + r"\s*\.\s*(?:Count|Any|First|Single)\s*\(", tail)):
                hits.append(_mk(f, code, agg.start()))
    return Measure(hits, n,
                   "分母 = 声明为 `IEnumerable<T>` / `IQueryable<T>` 或直接来自 `Where/Select/…` 的**延迟序列**变量数；"
                   "命中 = 同一延迟序列先聚合再遍历。已实体化的类型（`List`/`IReadOnlyList`/数组/属性）不计")


PUB_METHOD = re.compile(
    r"(?m)^[ \t]*public[ \t]+(?:static[ \t]+|async[ \t]+|virtual[ \t]+|override[ \t]+|sealed[ \t]+|"
    r"partial[ \t]+|unsafe[ \t]+|new[ \t]+)*"
    r"(?P<ret>[A-Za-z_][\w<>\[\],\.\?]*)[ \t]+(?P<name>[A-Za-z_]\w*)\s*\(")


def measure_d27(files: list[FileText]) -> Measure:
    """只统计**公共 API** 里「返回类型未标可空却 `return null`」的位置。

    9.6.4 的 0 档是「契约单一（NRT 标注 + 文档 / 抛异常 / 空对象 三选一）」——
    因此 `string?` 这类已标注可空的返回**不算违规**（NRT 就是契约）。
    不还原这一点，会把全部 63 处 `return null;`（含私有方法、含 `T?`）虚报成违规。
    实测教训：表达式体方法（`public static string X(..) => …;`）没有函数体，
    若直接找下一个 `{`，会把**下一个方法**的函数体当成它的，从而误报。
    """
    hits, n = [], 0
    for f in files:
        code = f.code
        for m in PUB_METHOD.finditer(code):
            # 先配平参数表的圆括号，再判断这个成员是不是表达式体（`=>`）或声明（`;`）
            depth, i = 1, m.end()
            while i < len(code) and depth:
                if code[i] == '(':
                    depth += 1
                elif code[i] == ')':
                    depth -= 1
                i += 1
            tail = code[i:i + 200].lstrip()
            if tail.startswith('=>') or tail.startswith(';'):
                continue
            oi = code.find('{', i - 1)
            if oi < 0:
                continue
            ci = brace_match(code, oi)
            if ci < 0:
                continue
            body = code[oi:ci]
            if 'return null' not in body:
                continue
            n += 1
            ret = m.group('ret')
            if '?' in ret:
                continue          # 已用 NRT 标注可空 → 契约单一，不计
            hits.append(_mk(f, code, oi + body.index('return null')))
    return Measure(hits, n,
                   "分母 = 公共方法中「返回类型未标 `?` 却 `return null`」的位置数；"
                   "已按 NRT 语义还原：返回类型带 `?` 视为契约已标注，不计；"
                   "表达式体成员不取后继函数体（否则会把下一个方法误算进来）")


# 9.6.4 的举例词表。`data` / `bar` / `foo` 这类词在有明确初始化上下文时是自解释的
# （实测：`var data = new SessionData(...)`、`var bar = inner.IndexOf('|')`），
# 只按词表字面统计会把 D-01 从 0 虚报成 13 处，故只保留**无领域含义**的那批。
MEANINGLESS = r"(?:tmp|temp|obj|list1|list2|list3|user1|user2|val1|val2|str1|str2|" \
              r"num1|num2|aaa|bbb|ccc|test1|test2|foobar)"
# 只统计**声明位置**（`var x` / `T x =` / 形参），不统计使用位置——
# 使用位置会把 `bar.Template`、`bar >= 0` 之类的引用重复计入。
DECL_SITE = re.compile(
    r"\b(?:var|string|object|dynamic|int|long|Action|Func<[^>]*>|List<\w+>|"
    r"[A-Z][A-Za-z0-9_]*)\s+(" + MEANINGLESS + r")\b"
    r"|[,\(]\s*(?:[A-Za-z_][\w<>\[\]\?\.]*\s+)?(" + MEANINGLESS + r")\s*[,\)]")


def measure_d01(files: list[FileText]) -> Measure:
    hits = []
    for f in files:
        for m in DECL_SITE.finditer(f.code):
            hits.append(_mk(f, f.code, m.start()))
    return Measure(hits, len(hits),
                   "分母 = 无义名（tmp/temp/obj/list1/val1/aaa 等**无领域含义**词）的**声明位置**数；"
                   "`data`/`item`/`value` 等在明确初始化上下文里自解释，不计；"
                   "单字母名不做全量统计（循环计数器 i/j/k 属惯例），需人工抽样")


# 只保留**不可能与英文词冲突**的匈牙利前缀。
# 实测：原先含 `tab` / `num` / `arr` / `obj` / `int`，把 `tabCountAfterOpen`、
# `tabIndex`、`number`、`object` 一并误判，D-02 从 0 虚报成 7 处。
HUNGARIAN = re.compile(
    r"\b(?:str|btn|txt|lbl|pnl|cmb|chk|lst|bmp|dbl|flt|bln|frm|dlg|mnu|grp|pic|"
    r"lng|sng|hlp|prg|rtf|wnd|ctl)[A-Z][A-Za-z0-9_]*\b")


def measure_d02(files: list[FileText]) -> Measure:
    hits = []
    for f in files:
        for idx, line in enumerate(f.code_lines, 1):
            if HUNGARIAN.search(line):
                hits.append(F(f.rel, idx, f.lines[idx - 1]))
    return Measure(hits, len(hits),
                   "分母 = 匈牙利前缀命名位置数（str/btn/txt/lst/dbl/… 等**无歧义缩写**）；"
                   "`tab*`/`num*`/`arr*`/`obj*`/`int*` 会与英文词冲突，不计")


M_UNDERSCORE = re.compile(r"\bm_[a-z]\w*")


def measure_d03a(files: list[FileText]) -> Measure:
    hits = []
    for f in files:
        for idx, line in enumerate(f.code_lines, 1):
            if M_UNDERSCORE.search(line):
                hits.append(F(f.rel, idx, f.lines[idx - 1]))
    return Measure(hits, len(hits), "分母 = m_ 前缀私有字段位置数（`_field` 下划线开头**不违规**）")


PUBLIC_CONST = re.compile(r"\bpublic\s+const\s+[\w<>\[\],\?\.]+\s+([A-Za-z_]\w*)\s*[=;]")
ALL_CAPS = re.compile(r"^[A-Z][A-Z0-9_]*$")
PASCAL = re.compile(r"^[A-Z][A-Za-z0-9_]*$")
LOWER_METHOD = re.compile(r"(?m)^[ \t]+(?:public|internal|protected|private)[ \t]+"
                          r"(?:static[ \t]+)?(?:async[ \t]+)?(?:override[ \t]+)?"
                          r"[\w<>\[\],\?\.]+\s+([a-z][A-Za-z0-9_]*)\([^)]*\)\s*(?:\{|=>)")
LOWER_TYPE = re.compile(r"(?m)^[ \t]*(?:public|internal|private|protected)[ \t]+"
                        r"(?:sealed[ \t]+|static[ \t]+|abstract[ \t]+|partial[ \t]+)*"
                        r"(?:class|struct|record|interface|enum)\s+([a-z]\w*)")


def measure_d03b(files: list[FileText]) -> Measure:
    """口径分歧处理（**须人工确认**，见 docs/质量检查表.md）。

    9.6.4 的 D-03b 字面写「公共常量非全大写」，但同一行的「正确处理」是
    「遵循**目标语言通行命名规范**」——C# / .NET 的通行规范对公共常量是
    **PascalCase**，不是 SCREAMING_CASE。两种读法结果差一个数量级
    （本仓实测：字面读法上百处 → 判 3；按目标语言惯例约 0 处 → 判 0；
    具体计数见生成的报告，**不在此写死**——它会随代码变动而失效）。

    本脚本取**交集**计分：只统计在两种读法下**都**违规的（名字首字母小写），
    并在证据里同时给出字面读法的计数，供复核者按任一口径复算。
    """
    hits, literal = [], []
    for f in files:
        code = f.code
        for m in PUBLIC_CONST.finditer(code):
            name = m.group(1)
            if not ALL_CAPS.match(name):
                literal.append(_mk(f, code, m.start()))
            if not PASCAL.match(name):     # 两种读法皆违
                hits.append(_mk(f, code, m.start()))
        for pat, what in ((LOWER_METHOD, "方法首字母小写"), (LOWER_TYPE, "类型名小写")):
            for m in pat.finditer(code):
                ln = line_of(code, m.start())
                hits.append(F(f.rel, ln, f"[{what}] " + (f.lines[ln - 1] if ln - 1 < len(f.lines) else "")))
    return Measure(hits, len(hits),
                   f"分母 = 公共常量 / 方法 / 类型命名大小写违例位置数（取两种读法的**交集**）；"
                   f"另按字面读法（公共常量须全大写）另有 {len(literal)} 处——与「遵循目标语言通行规范」"
                   f"（C# 惯例为 PascalCase）冲突，口径需人工确认")


CTRL_NUMBER = re.compile(
    r"\b(?:button|textBox|label|comboBox|listBox|panel|checkBox|radioButton|pictureBox|"
    r"dataGridView|tabControl|groupBox|menuStrip|toolStrip|richTextBox|numericUpDown)\d+\b")
HANDLER_NUMBER = re.compile(r"\b[A-Za-z_]\w*\d+_\w+\s*\(")


def measure_d04(files: list[FileText]) -> Measure:
    hits = []
    for f in files:
        for idx, line in enumerate(f.code_lines, 1):
            if CTRL_NUMBER.search(line) or HANDLER_NUMBER.search(line):
                hits.append(F(f.rel, idx, f.lines[idx - 1]))
    return Measure(hits, len(hits), "分母 = 控件序号名 / 序号事件处理器位置数")


def measure_d05(files: list[FileText]) -> Measure:
    hits, n = [], 0
    for f in files:
        n += 1
        code = f.code
        reason = []
        # ① using 写在 namespace 内（块式 namespace 才有此可能）
        nm = re.search(r"(?m)^\s*namespace\s+([\w\.]+)\s*\{", code)
        if nm:
            if re.search(r"\busing\s+[\w\.]+\s*;", code[nm.end():]):
                reason.append("using 写在 namespace 内")
        # ② 命名空间与目录不一致
        m2 = re.search(r"(?m)^\s*namespace\s+([\w\.]+)\s*;", code)
        if m2 and f.rel.startswith("src/"):
            segs = f.rel.split("/")
            if len(segs) >= 3:
                expect = ".".join(segs[1:-1])
                got = m2.group(1)
                if expect and got != expect and not got.startswith(expect + "."):
                    reason.append(f"命名空间 {got} ≠ 目录 {expect}")
        if reason:
            ln = line_of(code, (nm or m2).start())
            hits.append(F(f.rel, ln, "；".join(reason)))
    return Measure(hits, n, "分母 = 文件总数；两类判据：using 在 namespace 内、命名空间与目录不符")


def measure_d06(files: list[FileText]) -> Measure:
    hits = []
    multi: list[tuple[str, list[str], int]] = []
    multi_pure: list[str] = []          # 纯类型聚合文件（不承接行为）
    longm: list[Method] = []
    regions = 0
    for f in files:
        if f.rel.endswith(".g.cs") or ".g.i." in f.rel:
            continue
        code = f.code
        types = [m.group(2) for m in TYPE_DECL.finditer(code)]
        if len(types) > 1:
            multi.append((f.rel, types, len(f.lines)))
            # 纯聚合判据：文件内除类型声明外不存在方法体成员（即没有任何方法）
            if not methods_of(f):
                multi_pure.append(f.rel)
        for m in methods_of(f):
            if m.length > 60:
                longm.append(m)
        regions += len(re.findall(r"(?m)^[ \t]*#region\b", code))
    for rel, types, total in sorted(multi, key=lambda x: -x[2]):
        hits.append(F(rel, 1, f"[一文件多类 {len(types)} 个] {', '.join(types)}"))
    for m in sorted(longm, key=lambda x: -x.length):
        hits.append(F(m.rel, m.start_line, f"[方法 {m.length} 行] {m.name}()"))
    qual = None
    if any(m.length >= 1000 for m in longm):
        qual = 3
    elif any(m.length >= 300 for m in longm):
        qual = 2
    note = (f"分母 = 多类型文件 {len(multi)} 个 + 超 60 行方法 {len(longm)} 个 + `#region` {regions} 处；"
            f"定性升级线：存在 ≥1000 行方法→3，存在 ≥300 行方法→至少 2。**口径分歧**："
            f"其中 {len(multi_pure)} 个是「纯类型聚合文件」（整份文件只有 record / enum / 小类型声明，"
            f"不承接行为——如 Blocks.cs 的 25 个块类型、Types.cs 的 26 个数据结构）；"
            f"若按「只算承接行为的类型」读法，多类型文件为 {len(multi) - len(multi_pure)} 个。"
            f"本脚本按字面读法计分；两种读法的差额已列出，口径需人工确认")
    return Measure(hits, len(multi) + len(longm) + regions, note, qualifier=qual)


STATIC_CLASS = re.compile(r"\bstatic\s+class\s+([A-Za-z_]\w*)")
# 只认**有状态、长存活**的服务类型。`Writer` / `Resolver` / `Verifier` 之类后缀会命中
# 纯逻辑对象（`new PrtWriter()`、`new StructureResolver(...)`），不是"依赖直接 new"，
# 会把 D-08 从 0 虚报成 5 处。
HARD_NEW = re.compile(r"\bnew\s+([A-Za-z_]\w*(?:Store|Client|Runtime|Service|Bridge|Host))\s*\(")
# 组合根 / 工厂 / 单例访问器 / 连接入口：**依赖被显式创建的唯一合法位置**，不计入 D-08
COMPOSITION = re.compile(
    r"(?:Create|Factory|Build|Compose|Wire|Configure|Startup|Main|Register|Connect|Attach|"
    r"Acquire|Open|Start|GetInstance|Instance|Current|Require|Ensure|Resolve|Lazy)")


def measure_d08(files: list[FileText]) -> Measure:
    """D-08 管的是「依赖的**获取方式**」，不是「类是不是 static」。

    因此：纯函数工具静态类（`Matching` / `PssText` / `Wire` …）不计；
    `_field = new X(...)`（构造期初始化）与工厂 / 单例访问器（`Create*` / `Factory*` /
    `Register*` / `Connect*` / `??=` / `.Current`）是**依赖被显式创建的唯一合法位置**，也不计。
    计入的只有：在**非组合点**方法里、赋给**局部变量**的硬编码 `new`（自检代码除外）。
    实测教训：不做这几层还原，会把整仓的组合根与自检代码一并误判（20 处里 19 处是误报）。
    """
    hits, n = [], 0
    static_classes: list[str] = []
    for f in files:
        code = f.code
        ms = methods_of(f)
        static_classes += STATIC_CLASS.findall(code)
        type_names = {m.group(2) for m in TYPE_DECL.finditer(code)}
        is_selftest = "SelfTest" in os.path.basename(f.rel)
        for m in HARD_NEW.finditer(code):
            n += 1
            if is_selftest:
                continue                      # 自检代码构造实例以验证行为，不是依赖获取
            line = code[code.rfind('\n', 0, m.start()) + 1:code.find('\n', m.start())]
            # ① 赋给字段（构造期初始化 / 依赖由所有者创建）
            if re.search(r"_[A-Za-z_]\w*\s*=\s*$", code[max(0, m.start() - 80):m.start()]):
                continue
            if re.search(r"=\s*new\b", line) and re.match(r"\s*_[A-Za-z_]\w*", line):
                continue
            # ② 组合根 / 工厂 / 单例访问器（看**签名**，不是看方法体前 200 字）
            enclosing = None
            for mm in ms:
                if mm.open_idx < m.start() < mm.close_idx:
                    enclosing = mm
                    break
            if enclosing is not None:
                sig = f.lines[enclosing.start_line - 1] if enclosing.start_line - 1 < len(f.lines) else ""
                if COMPOSITION.search(sig) or enclosing.name in type_names:
                    continue          # 工厂 / 组合根 / 构造函数
            if '??=' in line or COMPOSITION.search(line):
                continue
            # ③ `using` 包围的局部
            if re.search(r"\busing\b", line):
                continue
            hits.append(_mk(f, code, m.start()))
    return Measure(hits, n,
                   f"分母 = 硬编码 new 状态类（*Store/*Client/*Runtime/*Service/*Bridge/*Host）位置数；"
                   f"已扣除：① 赋给字段（构造期初始化）② 组合根 / 工厂 / 单例访问器 / 连接入口 "
                   f"③ `using` 包围的局部 ④ 自检代码。静态类另有 {len(static_classes)} 个，"
                   f"**不单独计分**——D-08 管「依赖获取方式」，纯函数工具静态类（Matching / PssText / Wire 等）"
                   f"不是违规；「任意位置可改的静态可变状态」情形属 D-07，批次二已归零")


TO_MANAGED = re.compile(r"\b(?:IDisposable)\b")


def measure_d10(files: list[FileText]) -> Measure:
    hits = []
    for f in files:
        for m in re.finditer(r"\bthrow\s+(?!new\b|;)([A-Za-z_]\w*(?:\s*\?\?\s*[A-Za-z_]\w*)?)\s*;", f.code):
            hits.append(_mk(f, f.code, m.start()))
    return Measure(hits, len(hits), "分母 = `throw <变量>;`（重置堆栈）位置数；异常做流程控制需人工抽样")


LOOP_CONCAT = re.compile(r"\b([A-Za-z_]\w*)\s*\+=")


def measure_d16(files: list[FileText]) -> Measure:
    """只在**循环体内**统计 `s += "<字面量>"` / `s += $"…"` / `s += <字符串局部变量>`。

    实测教训：早先版本还统计了循环体里的 `=` 加算术（`var start = i + 1;`、
    `i = close + 1;`），那是整数运算不是字符串拼接，把 D-16 从 0 虚报成 20 处。
    """
    hits, n = [], 0
    for f in files:
        code = f.code
        # 先收集本文件里声明为 string（或 var xxx = "…"）的局部名，用于 `+= 变量` 形态
        string_locals = set(re.findall(r"\bstring\s+([A-Za-z_]\w*)", code))
        string_locals |= set(re.findall(r"\bvar\s+([A-Za-z_]\w*)\s*=\s*[^;\n]*[\"\']", code))
        for a, b in loop_spans(code):
            body = code[a:b]
            for m in LOOP_CONCAT.finditer(body):
                lhs, tail = m.group(1), body[m.end():m.end() + 60]
                rhs_literal = re.match(r'\s*(?:\$?"|@"|"|string\.Empty|\$@"|@\$")', tail)
                rhs_var = re.match(r"\s*([A-Za-z_]\w*)", tail)
                if rhs_literal or (rhs_var and rhs_var.group(1) in string_locals) or lhs in string_locals:
                    n += 1
                    hits.append(_mk(f, code, a + m.start()))
            for m in re.finditer(r"\b([A-Za-z_]\w*)\s*=\s*[^;\n]*\+\s*(?:\$?\"|'')", body):
                if m.group(1) in string_locals:
                    n += 1
                    hits.append(_mk(f, code, a + m.start()))
    seen, uniq = set(), []
    for h in hits:
        if (h.rel, h.line) not in seen:
            seen.add((h.rel, h.line))
            uniq.append(h)
    return Measure(uniq, len(uniq),
                   "分母 = 循环体内字符串 `+=` / `= … + \"…\"` 位置数（已排除整数算术与数值拼接）")


def measure_d21(files: list[FileText], warnings: int | None) -> Measure:
    hits = []
    nrt_bad = []
    for proj in sorted({f.project for f in files if f.project}):
        csproj = os.path.join(SRC, proj, proj + ".csproj")
        if not os.path.exists(csproj):
            continue
        txt = open(csproj, encoding="utf-8", errors="replace").read()
        if "<Nullable>enable</Nullable>" not in txt and "Nullable" not in txt:
            nrt_bad.append(proj)
    pragma_total, pragma_bare = 0, 0
    for f in files:
        lines = f.lines
        for idx, line in enumerate(f.lines, 1):
            if "#pragma warning disable" in line:
                pragma_total += 1
                ctx = "\n".join(lines[max(0, idx - 3):idx + 1])
                if "//" not in ctx and "/*" not in ctx:
                    pragma_bare += 1
                    hits.append(F(f.rel, idx, line))
    ec = os.path.join(ROOT, ".editorconfig")
    ec_none = 0
    if os.path.exists(ec):
        ec_none = len(re.findall(r"(?m)^\s*(?:dotnet_diagnostic\.)?CA\d{4}\.\w+\s*=\s*none\s*$",
                                 open(ec, encoding="utf-8", errors="replace").read()))
    note = (f"分母 = 未附理由抑制数 + 构建警告数；实测：NRT 未启用工程 {len(nrt_bad)} 个、"
            f"`#pragma warning disable` {pragma_total} 处（未附理由 {pragma_bare} 处）、"
            f".editorconfig none 档条目 {ec_none} 条（逐条附理由，见 docs/分析器门禁.md）、"
            f"构建警告数 {'未注入' if warnings is None else warnings}")
    qual = None
    if pragma_total >= 10 or (warnings is not None and warnings > 20):
        qual = 3                                   # 大面积抑制 / 关闭警告输出
    elif nrt_bad or pragma_bare or (warnings or 0) > 0:
        qual = 2                                   # 未启用 NRT / 未附理由抑制 / 忽略警告
    elif ec_none > 0 or (pragma_total - pragma_bare) > 0:
        qual = 1                                   # 已启用，但存在**已附理由**的规则放行
    if nrt_bad:
        note += "；有工程未启用 NRT → 至少判 2"
    return Measure(hits, pragma_bare + (warnings or 0), note, qualifier=qual)


# ───────────────────────────────────────────────────────────────────────────
# 5. 打分与定级
# ───────────────────────────────────────────────────────────────────────────

LEVELS = ["D-", "D", "D+", "C-", "C", "C+", "B-", "B", "B+", "A-", "A", "A+"]
LEVEL_IDX = {name: i for i, name in enumerate(LEVELS)}


def score_from_count(n_hits: int, b1: int, b2: int, qualifier: int | None) -> int:
    """9.6.3 通用锚点 + 9.6.4 定性升级线。"""
    if n_hits == 0:
        base = 0
    elif n_hits <= b1:
        base = 1
    elif n_hits <= b2:
        base = 2
    else:
        base = 3
    if qualifier is not None:
        base = max(base, qualifier)
    return base


def score_item(spec: Item, m: Measure) -> int:
    """逐项打分。9.6.4 把锚点写成**定性**的（如 D-09）时，以它为准——它是该项的专属锚点。"""
    if m.forced is not None:
        return m.forced
    return score_from_count(len(m.hits), spec.b1, spec.b2, m.qualifier)


def gate_count(m: Measure) -> int:
    """硬门禁的实测数：默认与 hits 相同，只有混装了非门禁落点的项（D-09、D-14）另给一份。"""
    return len(m.gate_hits) if m.gate_hits is not None else len(m.hits)


def maint_level(r: float) -> int:
    """9.2：扣分率 → 十二级（返回 LEVELS 下标）。"""
    if r == 0:
        return LEVEL_IDX["A+"]
    for limit, name in ((0.02, "A"), (0.04, "A-"), (0.07, "B+"), (0.10, "B"),
                        (0.14, "B-"), (0.19, "C+"), (0.25, "C"), (0.32, "C-"),
                        (0.42, "D+"), (0.55, "D")):
        if r <= limit + 1e-12:
            return LEVEL_IDX[name]
    return LEVEL_IDX["D-"]


def run_level(s: float) -> int:
    """9.4.3：0~100 → 十二级。"""
    if s >= 100 - 1e-9:
        return LEVEL_IDX["A+"]
    for limit, name in ((95, "A"), (90, "A-"), (85, "B+"), (80, "B"), (75, "B-"),
                        (70, "C+"), (60, "C"), (50, "C-"), (40, "D+"), (30, "D")):
        if s >= limit - 1e-9:
            return LEVEL_IDX[name]
    return LEVEL_IDX["D-"]


def merge_five(idx: int) -> str:
    """9.1 对外五档。"""
    if idx >= LEVEL_IDX["A-"]:
        return "A"
    if idx >= LEVEL_IDX["B-"]:
        return "B"
    if idx >= LEVEL_IDX["C"]:
        return "C"
    if idx >= LEVEL_IDX["D+"]:
        return "D"
    return "F"


def half_up(x: float) -> int:
    return int(x + 0.5) if x >= 0 else -int(-x + 0.5)


# ───────────────────────────────────────────────────────────────────────────
# 6. 报告
# ───────────────────────────────────────────────────────────────────────────

def build_report(root: str, warnings: int | None, s_run: float | None,
                 runtime_note: str) -> tuple[str, dict]:
    files = collect()
    total_lines = sum(len(f.lines) for f in files)

    measures: dict[str, Measure] = {}
    dispos = measure_disposables(files)
    measures.update({k: v for k, v in dispos.items() if not k.startswith("_")})
    measures["D-12"] = dispos["_D12"]
    measures["D-09"] = measure_d09(files)
    measures["D-22"] = measure_d22(files)
    measures["D-20"] = measure_d20(files)
    measures["D-28"] = measure_d28(files)
    measures["D-13"] = measure_d13(files)
    measures["D-14"] = measure_d14(files)
    measures["D-15"] = measure_d15(files)
    measures["D-18"] = measure_d18(files)
    measures["D-19"] = measure_d19(files)
    measures["D-23"] = measure_d23(files)
    measures["D-24"] = measure_d24(files)
    measures["D-17"] = measure_d17(files)
    measures["D-07"] = measure_d07(files)
    measures["D-25"] = measure_d25(files)
    measures["D-26"] = measure_d26(files)
    measures["D-27"] = measure_d27(files)
    measures["D-01"] = measure_d01(files)
    measures["D-02"] = measure_d02(files)
    measures["D-03a"] = measure_d03a(files)
    measures["D-03b"] = measure_d03b(files)
    measures["D-04"] = measure_d04(files)
    measures["D-05"] = measure_d05(files)
    measures["D-06"] = measure_d06(files)
    measures["D-08"] = measure_d08(files)
    measures["D-10"] = measure_d10(files)
    measures["D-16"] = measure_d16(files)
    measures["D-21"] = measure_d21(files, warnings)

    scores: dict[str, int] = {}
    for s in ITEM_SPECS:
        m = measures[s.id]
        scores[s.id] = score_item(s, m)

    # ── A 组 ──
    a_ded = sum(scores[i] * SPEC_BY_ID[i].weight for i in A_IDS)
    a_full = 3 * sum(SPEC_BY_ID[i].weight for i in A_IDS)
    r_corr = a_ded / a_full
    s_corr = 100.0 * (1 - r_corr)
    veto_a = [i for i in A_IDS if scores[i] == 3 and CAPS.get(i)]
    cap_a = None
    if veto_a:
        cap_a = min((LEVEL_IDX[CAPS[i]] for i in veto_a))

    # ── B 组 ──
    b_ded = sum(scores[i] * SPEC_BY_ID[i].weight for i in B_IDS)
    b_full = 3 * sum(SPEC_BY_ID[i].weight for i in B_IDS)
    r_maint = b_ded / b_full
    lvl_maint = maint_level(r_maint)
    veto_b = [i for i in B_IDS if scores[i] == 3 and CAPS.get(i)]
    cap_b = None
    if veto_b:
        cap_b = min((LEVEL_IDX[CAPS[i]] for i in veto_b))
    lvl_maint_capped = min(lvl_maint, cap_b) if cap_b is not None else lvl_maint

    # ── 安全合规维度 ──
    if s_run is None:
        lvl_sec, s_sec, lvl_sec_raw = None, None, None
    else:
        s_sec = 0.60 * s_run + 0.40 * s_corr
        lvl_sec_raw = run_level(s_sec)
        lvl_sec = min(lvl_sec_raw, cap_a) if cap_a is not None else lvl_sec_raw

    # ── 综合（9.5）──
    composite = None
    lvl_comp = None
    if lvl_sec is not None:
        usage = None  # 易用程度需 9.3 实测/人工，本脚本不臆造
        if usage is not None:
            pass
        weighted = lvl_sec * 0.70 + lvl_maint_capped * 0.15 + usage * 0.15
        lvl_comp = half_up(weighted)
        floor = min(lvl_sec, lvl_maint_capped, usage)
        if floor < LEVEL_IDX["C"]:
            lvl_comp = min(lvl_comp, floor)
        composite = weighted

    # ── 报告正文 ──
    L: list[str] = []
    L.append("# PRT 阅读器 · CRS 9.6 自评报告")
    L.append("")
    L.append("> 本文件由 `tools/selfscore.py` 生成，**可复算**：在同一份代码树上重跑得到同一结果。")
    L.append("> 依据《CRS 标准》9.6.1 / 9.6.2 / 9.6.3 / 9.6.4 / 9.6.5 / 9.2 / 9.4 / 9.5 / 9.1。")
    L.append(f"> 扫描范围：`src/**/*.cs`（已排除 `obj/`、`bin/`），{len(files)} 个文件、{total_lines} 行。")
    L.append("")
    L.append("## 一、A 组「正确性与安全」17 项（满分 132）")
    L.append("")
    L.append("| D 项 | 内容 | 权重 | 分母 | 命中 | 得分 | 扣分 | 判据 |")
    L.append("| --- | --- | --- | --- | --- | --- | --- | --- |")
    for i in A_IDS:
        s = SPEC_BY_ID[i]
        m = measures[i]
        L.append(f"| **{i}** | {s.title} | {s.tier}({s.weight}) | {m.denom} | {len(m.hits)} | "
                 f"**{scores[i]}** | {scores[i] * s.weight} | {s.mode}{' [工具]' if s.tool else ''} |")
    L.append("")
    L.append(f"**扣分 = Σ(得分 × 权重) = {a_ded}**；`r_corr = {a_ded} / {a_full} = {r_corr * 100:.2f}%`；"
             f"`S_corr = 100 × (1 − r_corr) = {s_corr:.2f}`")
    L.append("")
    if veto_a:
        caps = "、".join(f"{i}→{CAPS[i]}" for i in veto_a)
        L.append(f"**碰 9.6.5 一票否决（A 组）**：{caps}（只作用于安全合规**维度**，不直接封综合）")
    else:
        L.append("**未碰 9.6.5 一票否决（A 组）**。")
    L.append("")
    L.append("## 二、B 组「一致性与可维护性」15 项（满分 66）")
    L.append("")
    L.append("| D 项 | 内容 | 权重 | 分母 | 命中 | 得分 | 扣分 | 判据 |")
    L.append("| --- | --- | --- | --- | --- | --- | --- | --- |")
    for i in B_IDS:
        s = SPEC_BY_ID[i]
        m = measures[i]
        L.append(f"| **{i}** | {s.title} | {s.tier}({s.weight}) | {m.denom} | {len(m.hits)} | "
                 f"**{scores[i]}** | {scores[i] * s.weight} | {s.mode}{' [工具]' if s.tool else ''} |")
    L.append("")
    L.append(f"**扣分 = {b_ded}**；`r_maint = {b_ded} / {b_full} = {r_maint * 100:.2f}%`")
    L.append("")
    if veto_b:
        caps = "、".join(f"{i}→{CAPS[i]}" for i in veto_b)
        L.append(f"**碰 9.6.5 一票否决（B 组）**：{caps}，本维度等级不得高于 **{LEVELS[cap_b]}**")
    else:
        L.append("**未碰 9.6.5 一票否决（B 组）**。")
    L.append("")
    L.append("## 三、维度定级")
    L.append("")
    L.append("| 维度 | 依据 | 数值 | 原始等级 | 封顶后 |")
    L.append("| --- | --- | --- | --- | --- |")
    L.append(f"| 一致性与可维护性 | 9.2 `r_maint` | {r_maint * 100:.2f}% | {LEVELS[lvl_maint]} | "
             f"**{LEVELS[lvl_maint_capped]}** |")
    if s_run is None:
        L.append(f"| 安全合规 | 9.4.5 `S_sec = 0.60·S_run + 0.40·S_corr` | `S_corr = {s_corr:.2f}`，"
                 f"`S_run` 待运行时材料 | 待定 | 待定 |")
    else:
        L.append(f"| 安全合规 | 9.4.5 `S_sec` | `S_run = {s_run:.2f}`、`S_corr = {s_corr:.2f}` → "
                 f"`S_sec = {s_sec:.2f}` | {LEVELS[lvl_sec_raw]} | **{LEVELS[lvl_sec]}** |")
    L.append("| 易用程度 | 9.3（上手等级锚点 + 扣档项） | 需 3 人实测（9.3.4） | 待定 | 待定 |")
    L.append("")
    if s_run is None:
        L.append(f"> `S_run` 未注入：{runtime_note}")
        L.append("> 用 `--run-score <0~100>` 注入后即可得安全合规维度与综合等级。")
    else:
        L.append("> 综合等级还缺易用程度维度（9.3.4 要求 3 名评测人实测，脚本不臆造）。")
    L.append("")
    L.append("## 四、逐项证据（文件 + 行号）")
    L.append("")
    for s in ITEM_SPECS:
        m = measures[s.id]
        L.append(f"### {s.id} {s.title} —— 得分 {scores[s.id]}（{s.tier} {s.weight}）")
        L.append("")
        L.append(f"- 口径：{m.note}")
        L.append(f"- 分母：**{m.denom}**；命中：**{len(m.hits)}**")
        if not m.hits:
            L.append("- 证据：无命中（该项为 0 分）。")
        else:
            shown = m.hits[:30]
            for h in shown:
                L.append(f"  - `{h.render()}`")
            if len(m.hits) > len(shown):
                L.append(f"  - …… 另有 {len(m.hits) - len(shown)} 处")
        L.append("")
    L.append("## 五、硬门禁（`--check` 用）")
    L.append("")
    L.append("| 门禁 | 要求 | 实测 | 结果 |")
    L.append("| --- | --- | --- | --- |")
    for gid, gname, want in HARD_GATES:
        got = gate_count(measures[gid])
        ok = got <= want
        L.append(f"| {gid} {gname} | ≤ {want} | {got} | {'通过' if ok else '**未通过**'} |")
    L.append("")
    L.append("> **门禁口径**：只看「9.6.5 一票否决」与批次二验收门列明的那几种情形——"
             "D-09 只认「完全空 catch / 静默 return null」（不含「吞异常」，后者是 2 分档、需人工判），"
             "D-14 只认 `PushFrame` / `DoEvents`（不含 `Thread.Sleep` 等软落点）。")
    L.append("")
    L.append("## 六、纯逻辑层等价性")
    L.append("")
    L.append("本轮全部改动**未触碰** `Prt.Core` 的结构解析、匹配、编号算法与数据结构；")
    L.append("等价性由 `samples/` 三格式导出快照逐字节比对证明（见 `docs/质量检查表.md` 改造验收门）。")
    L.append("")

    data = {
        "files": len(files), "lines": total_lines,
        "a_deduction": a_ded, "a_full": a_full, "r_corr": r_corr, "S_corr": s_corr,
        "b_deduction": b_ded, "b_full": b_full, "r_maint": r_maint,
        "level_maint": LEVELS[lvl_maint_capped],
        "level_maint_raw": LEVELS[lvl_maint],
        "s_run": s_run, "S_sec": s_sec if s_run is not None else None,
        "level_sec": LEVELS[lvl_sec] if lvl_sec is not None else None,
        "veto_a": veto_a, "veto_b": veto_b,
        "scores": scores,
        "hits": {i: len(measures[i].hits) for i in measures},
        "gates": {i: gate_count(measures[i]) for i in measures},
        "denoms": {i: measures[i].denom for i in measures},
    }
    return "\n".join(L), data


HARD_GATES = [
    ("D-09", "空 catch / 静默吞异常", 0),
    ("D-22", "非事件处理器的 async void", 0),
    ("D-14", "UI 线程阻塞（PushFrame / DoEvents）", 0),
    ("D-07", "public static 可写成员", 0),
    ("D-17", "区域敏感字符串比较", 0),
    ("D-20", "直接读系统时间（时钟实现除外）", 0),
]


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="CRS 9.6 自评生成器（PRT 阅读器）")
    ap.add_argument("--out", help="把报告写到指定 Markdown 文件")
    ap.add_argument("--json", dest="json_out", help="把机器可读结果写到指定 JSON 文件")
    ap.add_argument("--warnings", type=int, default=None, help="dotnet build 的警告数（D-21 用）")
    ap.add_argument("--run-score", type=float, default=None, help="运行时得分 S_run（9.4.1~9.4.3）")
    ap.add_argument("--check", action="store_true", help="门禁模式：任一硬门禁不达标即退出码 1")
    ap.add_argument("--gates-only", action="store_true",
                    help="只查 6 条硬门禁，不查维度阈值（build.ps1 的 lint 环节用这个）")
    ap.add_argument("--min-maint", default="B+", choices=LEVELS,
                    help="可维护性维度的下限（默认 B+；维度阈值是 score 环节的职责）")
    args = ap.parse_args(argv)

    runtime_note = ("9.4.1 的五项运行时判据（接入模式完整度 / 申报一致性 / 越权记录 / "
                    "恶意软件扫描 / 行为清单与透明度）需要审计与扫描报告，脚本不臆造。")
    report, data = build_report(ROOT, args.warnings, args.run_score, runtime_note)

    print(report)
    if args.out:
        os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
        open(args.out, "w", encoding="utf-8", newline="\n").write(report)
        print(f"\n[已写入] {args.out}")
    if args.json_out:
        open(args.json_out, "w", encoding="utf-8", newline="\n").write(
            json.dumps(data, ensure_ascii=False, indent=2))
        print(f"[已写入] {args.json_out}")

    if args.check:
        print("\n=== 门禁检查 ===")
        ok = True
        for gid, gname, want in HARD_GATES:
            got = data["gates"][gid]
            good = got <= want
            ok = ok and good
            print(f"  [{'通过' if good else '未通过'}] {gid} {gname}：实测 {got}，要求 ≤ {want}")
        need = LEVEL_IDX[args.min_maint]
        got_lvl = LEVEL_IDX[data["level_maint"]]
        good = got_lvl >= need
        if args.gates_only:
            print(f"  [跳过] 可维护性维度阈值检查（--gates-only）：本次实测 {data['level_maint']}"
                  f"（r_maint = {data['r_maint'] * 100:.2f}%）；该阈值由 score 环节检查")
        else:
            ok = ok and good
            print(f"  [{'通过' if good else '未通过'}] 可维护性维度：{data['level_maint']}"
                  f"（r_maint = {data['r_maint'] * 100:.2f}%），要求 ≥ {args.min_maint}")
        print(f"\n门禁{'全部通过' if ok else '未通过'}。")
        return 0 if ok else 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
