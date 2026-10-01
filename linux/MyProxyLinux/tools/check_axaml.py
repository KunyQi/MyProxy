#!/usr/bin/env python3
"""检查（并可自动修）GUI 的 XAML 是否都是**合法 XML**。

    python linux/MyProxyLinux/tools/check_axaml.py
    python linux/MyProxyLinux/tools/check_axaml.py --fix

为什么值得单独一个脚本：Avalonia 的 XAML 编译器报的 AVLN1001 只给「列位置」，
而这类错误最常见的原因是 **XML 注释里出现了两个连续的连字符**（`--`）——
那在 XML 里是非法的，可是在被注释掉的命令行片段、或「A —— B」这样的中文行文里
又特别自然。在编译之前先跑这个，错误就直接指到文件与行列。

加 `--fix` 会把注释正文里的 `--` 换成破折号 `—`（只动注释正文，不碰标签与属性），
并打印每一处改动。退出码非 0 表示仍有文件不合法。
"""

from __future__ import annotations

import argparse
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
GUI = ROOT / "linux" / "MyProxyLinux" / "gui"
WINDOWS = ROOT / "windows" / "MyProxy"


def repair_comments(text: str) -> tuple[str, list[tuple[int, str, str]]]:
    """把注释正文里的 `--` 换成 `—`。返回（新文本, [(行号, 旧, 新)])。"""
    lines = text.splitlines(keepends=True)
    changes: list[tuple[int, str, str]] = []
    in_comment = False

    for index, line in enumerate(lines):
        if not in_comment and "<!--" not in line:
            continue

        start = 0
        if not in_comment:
            start = line.index("<!--") + 4
            in_comment = True

        end = line.find("-->", start)
        body_end = len(line) if end < 0 else end
        body = line[start:body_end]

        if "--" in body:
            fixed = body.replace("--", "—")
            changes.append((index + 1, body.strip()[:80], fixed.strip()[:80]))
            line = line[:start] + fixed + line[body_end:]
            lines[index] = line

        if end >= 0:
            in_comment = False

    return "".join(lines), changes


def check(root: Path, label: str, fix: bool) -> int:
    bad = 0
    files = sorted(path for path in root.rglob("*.axaml")) + sorted(
        path for path in root.rglob("*.xaml")
    )
    for path in files:
        parts = set(path.parts)
        if {"bin", "obj"} & parts:
            continue
        text = path.read_text(encoding="utf-8", errors="replace")
        try:
            ET.fromstring(text)
            continue
        except ET.ParseError as error:
            line, column = error.position
            lines = text.splitlines()
            context = lines[line - 1] if 0 < line <= len(lines) else ""
            print(f"FAIL {path.relative_to(root)}:{line}:{column}  {error}")
            print(f"     {context.strip()[:120]}")

        if fix:
            repaired, changes = repair_comments(text)
            try:
                ET.fromstring(repaired)
            except ET.ParseError as error:
                print(f"     自动修不动（原因不是注释里的连字符）：{error}")
                bad += 1
                continue

            path.write_text(repaired, encoding="utf-8", newline="")
            print(f"     已修 {len(changes)} 处注释：")
            for number, old, new in changes:
                print(f"       第 {number} 行：{old}  →  {new}")
        else:
            bad += 1
    print(f"{label}: 检查 {len(files)} 个文件，不合法 {bad} 个")
    return bad


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--fix", action="store_true", help="自动修注释里的连字符")
    args = parser.parse_args(argv)

    bad = check(GUI, "Linux GUI", args.fix)
    # Windows 端只查视图与皮肤：那批文件是权威来源，顺手确认它们本身没坏。
    for folder in ("Themes", "Views"):
        target = WINDOWS / folder
        if target.is_dir():
            bad += check(target, f"Windows {folder}", fix=False)
    return 1 if bad else 0


if __name__ == "__main__":
    raise SystemExit(main())
