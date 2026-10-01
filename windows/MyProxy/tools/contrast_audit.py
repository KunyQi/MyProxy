#!/usr/bin/env python3
"""对 DesignTokens.xaml 的配色做 WCAG 对比度审计。

按前景与背景组合校验文字、图标和控件的最小对比度。

用法（在仓库根目录）：

    python windows/MyProxy/tools/contrast_audit.py

任何一项不达标就以非零码退出，可以直接挂在 CI 或提交前跑。
新增前景与背景组合时，在 PAIRS 中添加相应检查。
"""

from __future__ import annotations

import pathlib
import re
import sys

TOKENS = pathlib.Path(__file__).resolve().parents[1] / "Themes" / "DesignTokens.xaml"

# (前景 token, 背景 token, 用途, 要求的最小对比度)
# WCAG 2.1 AA：正文 4.5:1；大字（>=18.66px 或 14px 粗体）3:1；非文本图形 3:1。
# 极细边框是纯装饰，不受 WCAG 约束，这里只设一个「至少看得见」的下限。
PAIRS: list[tuple[str, str, str, float]] = [
    ("Color.TextPrimary", "Color.Background", "状态文字 16px / 模式选中项 13px", 4.5),
    ("Color.TextPrimary", "Color.Surface", "滑块上的选中文字 13px", 4.5),
    ("Color.TextSecondary", "Color.Background", "标题 / 副文案 / 流量标签 13px", 4.5),
    ("Color.TextSecondary", "Color.SurfaceSunken", "模式未选中项 13px", 4.5),
    ("Color.TextDisabled", "Color.Surface", "禁用态检测按钮 13px", 3.0),
    ("Color.AccentText", "Color.Surface", "检测按钮文字 13px（乳白底）", 4.5),
    ("Color.AccentText", "Color.Background", "Sparkline 上行描边（图形）", 3.0),
    ("Color.SuccessText", "Color.Background", "自检通过结论 13px", 4.5),
    ("Color.ErrorText", "Color.Background", "自检失败结论 / InlineError 13px", 4.5),
    ("Color.Border", "Color.Background", "极细边框（装饰，非 WCAG 要求）", 1.2),
]

# 白字压在实底上的组合，单列出来因为前景不是 token。
WHITE_ON: list[tuple[str, str, float]] = [
    ("Color.AccentText", "BindView 主按钮白字", 4.5),
]


def load_colors() -> dict[str, str]:
    text = TOKENS.read_text(encoding="utf-8")
    return {
        m.group(1): m.group(2).upper()
        for m in re.finditer(r'<Color x:Key="(Color\.[\w.]+)">#([0-9A-Fa-f]{6})</Color>', text)
    }


def _linear(channel: int) -> float:
    c = channel / 255.0
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def luminance(hex_rgb: str) -> float:
    r, g, b = (int(hex_rgb[i : i + 2], 16) for i in (0, 2, 4))
    return 0.2126 * _linear(r) + 0.7152 * _linear(g) + 0.0722 * _linear(b)


def contrast(fg: str, bg: str) -> float:
    a, b = luminance(fg), luminance(bg)
    hi, lo = max(a, b), min(a, b)
    return (hi + 0.05) / (lo + 0.05)


def main() -> int:
    colors = load_colors()
    missing = [
        key
        for pair in PAIRS
        for key in pair[:2]
        if key not in colors
    ]
    if missing:
        print(f"token 不存在：{', '.join(sorted(set(missing)))}", file=sys.stderr)
        return 2

    rows: list[tuple[str, float, float, bool, str]] = []
    for fg_key, bg_key, use, need in PAIRS:
        ratio = contrast(colors[fg_key], colors[bg_key])
        rows.append((f"{fg_key} #{colors[fg_key]}", ratio, need, ratio >= need, use))

    for bg_key, use, need in WHITE_ON:
        ratio = contrast("FFFFFF", colors[bg_key])
        rows.append((f"#FFFFFF on {bg_key}", ratio, need, ratio >= need, use))

    width = max(len(r[0]) for r in rows)
    print(f"{'前景':{width}} {'对比度':>7} {'要求':>6}  判定 用途")
    failures = 0
    for label, ratio, need, ok, use in rows:
        if not ok:
            failures += 1
        print(f"{label:{width}} {ratio:7.2f} {need:6.1f}  {'OK  ' if ok else 'FAIL'} {use}")

    if failures:
        print(f"\n{failures} 项不达标", file=sys.stderr)
        return 1

    print(f"\n全部 {len(rows)} 项达标")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
