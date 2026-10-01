#!/usr/bin/env python3
"""把 Windows(WPF) 主题字典里的**值型令牌**机械转换成 Avalonia 字典。

    python linux/MyProxyLinux/tools/port_theme_tokens.py --check
    python linux/MyProxyLinux/tools/port_theme_tokens.py --write

为什么要生成而不是手抄：Windows 端的三套皮肤（`DesignTokens.xaml` 是默认那套，
`Ceramic.xaml` / `Porcelain.xaml` 是完整的两套替换字典）里，
间距、尺寸、字号、圆角、颜色、几何坐标是**逐值权威**的。手抄一遍就等于把这些
数字抄错的机会乘以三。生成器只做机械映射，能转的转、不能转的**列出来**，
绝不静默丢东西。

值型映射（WPF → Avalonia）：

| WPF | Avalonia |
| --- | --- |
| `<sys:Double>` | `<x:Double>` |
| `<Color>` | `<Color>`（同） |
| `<SolidColorBrush>` | 同（`Color="{StaticResource ...}"` 保留） |
| `<LinearGradientBrush>` / `<RadialGradientBrush>` | 同（属性名一致） |
| `<Thickness>` / `<CornerRadius>` | 同（有 TypeConverter） |
| `<FontFamily>` | 同 |
| `<Duration>0:0:0.24</Duration>` | `<x:TimeSpan>` |
| `<CubicEase>` / `<SineEase>` / `<ElasticEase>` / `<BackEase>` | 同名（Avalonia.Animation.Easings） |
| `<PathGeometry Figures="…">` | `<StreamGeometry>…</StreamGeometry>`（Avalonia 用路径标记文本） |
| `<DropShadowEffect Direction ShadowDepth BlurRadius>` | `<DropShadowEffect OffsetX OffsetY BlurRadius>`（角度+距离换算成偏移） |

**不转换**的（必须手工按 Avalonia 方言重写，脚本会全部列出来）：`<Style>`。
"""

from __future__ import annotations

import argparse
import math
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
WINDOWS_THEMES = ROOT / "windows" / "MyProxy" / "Themes"
LINUX_THEMES = ROOT / "linux" / "MyProxyLinux" / "gui" / "Themes"

XAML_NS = "http://schemas.microsoft.com/winfx/2006/xaml/presentation"
X_NS = "http://schemas.microsoft.com/winfx/2006/xaml"
KEY = f"{{{X_NS}}}Key"

# 值型令牌：可机械转换
VALUE_TAGS = {
    "Double",
    "Color",
    "SolidColorBrush",
    "LinearGradientBrush",
    "RadialGradientBrush",
    "Thickness",
    "CornerRadius",
    "FontFamily",
    "Duration",
    "CubicEase",
    "SineEase",
    "ElasticEase",
    "BackEase",
    "QuadraticEase",
    "PathGeometry",
    "DropShadowEffect",
}

# 逆变换：Angle → (cos, sin) 用 WPF 的约定（0° 指向 +X，顺时针为正，Y 向下）
def direction_to_offset(direction: float, depth: float) -> tuple[float, float]:
    radians = math.radians(direction)
    return (round(depth * math.cos(radians), 4), round(depth * math.sin(radians), 4))


def local(tag: str) -> str:
    return tag.split("}", 1)[-1]


def duration_to_timespan(text: str) -> str:
    """`0:0:0.240` → `0:0:0.240`（Avalonia 的 x:TimeSpan 用同样的格式）。"""
    return text.strip()


class Converter:
    def __init__(self) -> None:
        self.lines: list[str] = []
        self.converted: list[str] = []
        self.manual: list[tuple[str, str, str]] = []   # (key, tag, reason)
        self.unknown: list[tuple[str, str]] = []       # (key or '?', tag)
        self.losses: list[str] = []                    # 无法逐值对应的参数（如实记账）

    # -- 每个标签一个生成分支 -------------------------------------------
    def convert(self, element: ET.Element) -> None:
        tag = local(element.tag)
        key = element.get(KEY)

        if tag == "Style":
            self.manual.append((key or "?", tag, "Avalonia 用 ControlTheme / Style 选择器，需重写"))
            return
        if tag not in VALUE_TAGS:
            self.unknown.append((key or "?", tag))
            return
        if not key:
            self.unknown.append(("(无 x:Key)", tag))
            return

        handler = getattr(self, f"emit_{tag}", None)
        if handler is None:
            self.unknown.append((key, tag))
            return
        handler(key, element)
        self.converted.append(key)

    def emit_Double(self, key: str, element: ET.Element) -> None:
        self.lines.append(f'    <x:Double x:Key="{key}">{text(element)}</x:Double>')

    def emit_Color(self, key: str, element: ET.Element) -> None:
        self.lines.append(f'    <Color x:Key="{key}">{text(element)}</Color>')

    def emit_SolidColorBrush(self, key: str, element: ET.Element) -> None:
        color = element.get("Color", "")
        opacity = element.get("Opacity")
        extra = f' Opacity="{opacity}"' if opacity else ""
        self.lines.append(f'    <SolidColorBrush x:Key="{key}" Color="{color}"{extra} />')

    def emit_LinearGradientBrush(self, key: str, element: ET.Element) -> None:
        self._gradient("LinearGradientBrush", key, element)

    def emit_RadialGradientBrush(self, key: str, element: ET.Element) -> None:
        self._gradient("RadialGradientBrush", key, element)

    def _gradient(self, tag: str, key: str, element: ET.Element) -> None:
        attributes = []
        for name in ("StartPoint", "EndPoint", "GradientOrigin", "RadiusX", "RadiusY", "Center",
                     "MappingMode", "SpreadMethod", "Opacity"):
            value = element.get(name)
            if value is None:
                continue
            if name == "MappingMode":
                # WPF 的 MappingMode="RelativeToBoundingBox" 是 Avalonia 的默认行为
                continue
            attributes.append(f'{name}="{value}"')
        self.lines.append(f'    <{tag} x:Key="{key}"' + (" " + " ".join(attributes) if attributes else "") + ">")
        for stop in element:
            if local(stop.tag) != "GradientStop":
                continue
            offset = stop.get("Offset", "0")
            color = stop.get("Color", "#00000000")
            self.lines.append(f'        <GradientStop Offset="{offset}" Color="{color}" />')
        self.lines.append(f"    </{tag}>")

    def emit_Thickness(self, key: str, element: ET.Element) -> None:
        self.lines.append(f'    <Thickness x:Key="{key}">{text(element)}</Thickness>')

    def emit_CornerRadius(self, key: str, element: ET.Element) -> None:
        self.lines.append(f'    <CornerRadius x:Key="{key}">{text(element)}</CornerRadius>')

    def emit_FontFamily(self, key: str, element: ET.Element) -> None:
        value = substitute_font(text(element))
        self.lines.append(f'    <FontFamily x:Key="{key}">{value}</FontFamily>')

    def emit_Duration(self, key: str, element: ET.Element) -> None:
        self.lines.append(
            f'    <x:TimeSpan x:Key="{key}">{duration_to_timespan(text(element))}</x:TimeSpan>'
        )

    def emit_easing(self, tag: str, key: str, element: ET.Element) -> None:
        """WPF 的「一个类 + EasingMode」→ Avalonia 的「具体类」。

        Avalonia **没有** `CubicEase` 这种不带方向的类（只有 `CubicEaseIn/Out/InOut`），
        所以 `EasingMode` 不是属性，而是类型名的一部分。
        另外 Avalonia 的弹性/回弹类没有 `Oscillations`/`Springiness`/`Amplitude`
        （`ElasticEaseOut` 是写死的阻尼正弦），这些参数只能丢掉——丢掉的每一处都记进
        报告，别让「三套皮肤看起来一样」掩盖了「手感与 Windows 略有差别」。
        """
        base = tag[: -len("Ease")] if tag.endswith("Ease") else tag
        mode = element.get("EasingMode", "EaseOut")
        suffix = {"EaseIn": "In", "EaseOut": "Out", "EaseInOut": "InOut"}.get(mode, "Out")
        class_name = f"{base}Ease{suffix}"

        dropped = [
            name
            for name in ("Oscillations", "Springiness", "Amplitude", "Exponent")
            if element.get(name) is not None
        ]
        if dropped:
            values = ", ".join(f"{name}={element.get(name)}" for name in dropped)
            self.losses.append(f"{key}（{tag}）丢掉了 Avalonia 没有的参数：{values}")

        # 缓动类型在 Avalonia 里位于 Avalonia.Animation.Easings 命名空间（不是默认命名空间），
        # 所以必须带 ea: 前缀，文件头也要有对应的 xmlns:ea 声明。
        self.lines.append(f'    <ea:{class_name} x:Key="{key}" />')

    emit_CubicEase = lambda self, key, el: self.emit_easing("CubicEase", key, el)          # noqa: E731
    emit_SineEase = lambda self, key, el: self.emit_easing("SineEase", key, el)            # noqa: E731
    emit_ElasticEase = lambda self, key, el: self.emit_easing("ElasticEase", key, el)      # noqa: E731
    emit_BackEase = lambda self, key, el: self.emit_easing("BackEase", key, el)            # noqa: E731
    emit_QuadraticEase = lambda self, key, el: self.emit_easing("QuadraticEase", key, el)  # noqa: E731

    def emit_PathGeometry(self, key: str, element: ET.Element) -> None:
        figures = element.get("Figures", "").strip()
        if not figures and len(element):
            # 用 <PathGeometry.Figures> 展开写的：只报告，不猜
            self.manual.append((key, "PathGeometry", "以子元素展开的路径，需手工转成标记文本"))
            self.converted.remove(key)
            return
        self.lines.append(f'    <StreamGeometry x:Key="{key}">{figures}</StreamGeometry>')

    def emit_DropShadowEffect(self, key: str, element: ET.Element) -> None:
        direction = float(element.get("Direction", 315))
        depth = float(element.get("ShadowDepth", 0))
        blur = float(element.get("BlurRadius", 0))
        opacity = element.get("Opacity")
        color = element.get("Color", "#FF000000")
        offset_x, offset_y = direction_to_offset(direction, depth)
        attributes = [
            f'OffsetX="{offset_x}"',
            f'OffsetY="{offset_y}"',
            f'BlurRadius="{blur}"',
            f'Color="{color}"',
        ]
        if opacity:
            attributes.append(f'Opacity="{opacity}"')
        self.lines.append(f'    <DropShadowEffect x:Key="{key}" ' + " ".join(attributes) + " />")


def text(element: ET.Element) -> str:
    return (element.text or "").strip()


# =====================================================================================
# 别名令牌（alias tokens）
# =====================================================================================
# 为什么需要这一层：Linux 端把 Windows 的三份字典（各 ~200 个令牌 + 45 个样式）
# 收敛成「**一份**共享样式 + 三份令牌」。凡是「同一个样式里同一个属性在三套皮肤下取值
# 不同」的地方，共享样式只能取其中一套的值——那就是默认皮肤以外两套不逐值一致。
# 这些差异本身是**材质设计的一部分**（釉陶的高光是偏心的椭圆、瓷白按下几乎不动），
# 所以不能统一掉，只能升格成令牌：给每一处差异造一支 `Alias.*`，三套皮肤各自取值，
# 共享样式引用别名。
#
# ⚠ 这张表是**手写**的，但它不是「凭记忆抄」。左侧的取值逐个来自下面这条命令的输出
# （`.probe/skin-diff.py <样式名>`：把三份字典里同一个 Style 块逐属性比出来），
# 而且构建期的 `scripts/release_verify.py` 会核对三份生成文件里的别名集合一致——
# 少写一套会在门禁上炸，不会悄悄退化。
#
# 每一项：(别名 key, {皮肤: 取值})。皮肤名与 windows/MyProxy/Themes/<皮肤>.xaml 同名。
# 取值的两种形态：`Brush.*`/`Glaze.*` 这类是**令牌名**，其余是字面量。
ALIAS_TOKENS: list[tuple[str, dict[str, str]]] = [
    # ---- 能量球：釉边与图标色族（Windows 端状态触发器里逐条写死的笔刷）----
    ("Alias.OrbRim.Idle", {"DesignTokens": "Brush.BorderStrong", "Ceramic": "Brush.BorderStrong", "Porcelain": "Brush.Border"}),
    ("Alias.OrbRim.Connected", {"DesignTokens": "Brush.SuccessGlow", "Ceramic": "Brush.SuccessText", "Porcelain": "Brush.SuccessText"}),
    ("Alias.OrbRim.Connecting", {"DesignTokens": "Brush.AccentGlow", "Ceramic": "Brush.AccentText", "Porcelain": "Brush.AccentText"}),
    ("Alias.OrbRim.Error", {"DesignTokens": "Brush.ErrorGlow", "Ceramic": "Brush.ErrorText", "Porcelain": "Brush.ErrorText"}),
    ("Alias.OrbIcon.Idle", {"DesignTokens": "Brush.TextSecondary", "Ceramic": "Brush.TextSecondary", "Porcelain": "Brush.TextSecondary"}),
    ("Alias.OrbIcon.Connected", {"DesignTokens": "Brush.SuccessText", "Ceramic": "Brush.SuccessText", "Porcelain": "Brush.SuccessText"}),
    ("Alias.OrbIcon.Connecting", {"DesignTokens": "Brush.AccentText", "Ceramic": "Brush.AccentText", "Porcelain": "Brush.AccentText"}),
    ("Alias.OrbIcon.Error", {"DesignTokens": "Brush.ErrorText", "Ceramic": "Brush.ErrorText", "Porcelain": "Brush.ErrorText"}),

    # ---- 能量球：镜面高光几何（Ceramic 是偏心的小椭圆，另两套是居中的宽椭圆）----
    ("Alias.OrbSpecularWidth", {"DesignTokens": "Size.OrbSpecularWidth", "Ceramic": "60", "Porcelain": "Size.OrbSpecularWidth"}),
    ("Alias.OrbSpecularHeight", {"DesignTokens": "Size.OrbSpecularHeight", "Ceramic": "36", "Porcelain": "Size.OrbSpecularHeight"}),
    ("Alias.OrbSpecularMargin", {"DesignTokens": "0,40,0,0", "Ceramic": "55,50,0,0", "Porcelain": "0,40,0,0"}),
    ("Alias.OrbSpecularAlign", {"DesignTokens": "Center", "Ceramic": "Left", "Porcelain": "Center"}),

    # ---- 能量球：悬停/按下位移（三套皮肤的「手感」不同：瓷白几乎不动）----
    ("Alias.OrbHoverScale", {"DesignTokens": "1.02", "Ceramic": "1.02", "Porcelain": "1"}),
    ("Alias.OrbPressScale", {"DesignTokens": "0.93", "Ceramic": "0.95", "Porcelain": "0.98"}),

    # ---- 检测按钮：悬停与按下填充（设计令牌与瓷白是压深一档，釉陶是釉面翻转）----
    ("Alias.CheckFill.Hover", {"DesignTokens": "Brush.SurfaceSunken", "Ceramic": "Glaze.Surface.Convex", "Porcelain": "Brush.Border"}),
    ("Alias.CheckFill.Pressed", {"DesignTokens": "Brush.SurfaceSunken", "Ceramic": "Glaze.Surface.Convex", "Porcelain": "Brush.SurfaceSunken"}),
    ("Alias.CheckFill.Disabled", {"DesignTokens": "Brush.SurfaceSunken", "Ceramic": "Brush.SurfaceSunken", "Porcelain": "Brush.SurfaceSunken"}),

    # ---- 焦点环（能量球与检测按钮的 FocusVisualStyle；WPF 里三套取值不同）----
    ("Alias.FocusRingBrush", {"DesignTokens": "Brush.TextPrimary", "Ceramic": "Brush.AccentText", "Porcelain": "Brush.AccentText"}),

    # ---- 状态文字色族（OrbStatusTextStyle 在三套皮肤里的 Connected/Error/Connecting 取值）----
    ("Alias.StatusText.Connected", {"DesignTokens": "Brush.SuccessText", "Ceramic": "Brush.SuccessText", "Porcelain": "Brush.SuccessText"}),
    ("Alias.StatusText.Connecting", {"DesignTokens": "Brush.TextSecondary", "Ceramic": "Brush.TextSecondary", "Porcelain": "Brush.TextSecondary"}),
    ("Alias.StatusText.Error", {"DesignTokens": "Brush.ErrorText", "Ceramic": "Brush.ErrorText", "Porcelain": "Brush.ErrorText"}),

    # ---- 文本链接：hover 的底色与描边（TextLinkButtonStyle 的 IsMouseOver 触发器）----
    ("Alias.TextLink.HoverFill", {"DesignTokens": "Brush.SurfaceSunken", "Ceramic": "Brush.Glaze.Rim", "Porcelain": "Brush.SurfaceSunken"}),
    ("Alias.TextLink.HoverStroke", {"DesignTokens": "Brush.Border", "Ceramic": "Brush.BorderStrong", "Porcelain": "Brush.Border"}),
]

# 三套皮肤的源文件，以及从它们建出来的索引（见 index_skin）。
THEMES_FOR_SKIN = {
    skin: WINDOWS_THEMES / f"{skin}.xaml"
    for skin in ("DesignTokens", "Ceramic", "Porcelain")
}
COLORS_FOR_SKIN: dict[str, set[str]] = {}
GRADIENTS_FOR_SKIN: dict[str, dict[str, tuple[str, str, list[tuple[str, str]]]]] = {}
SOLID_BRUSH_COLORS_FOR_SKIN: dict[str, dict[str, str]] = {}
SCALAR_FOR_SKIN: dict[str, dict[str, str]] = {}

# 别名的**类型**。别名 key 在三套皮肤里同名同型，共享样式照样用# `{DynamicResource 别名}` 引用——换肤时第 0 份字典整体被替换，引用自然解析到新皮肤的值。
# 类型必须显式写死在这里，不能从取值猜：`0,40,0,0` 是 Thickness 也可能是别的，
# `Center` 是字符串也可能是枚举，猜错的表现是运行期悄悄回落成默认值。
ALIAS_TYPES: dict[str, str] = {
    "Alias.OrbRim.Idle": "brush",
    "Alias.OrbRim.Connected": "brush",
    "Alias.OrbRim.Connecting": "brush",
    "Alias.OrbRim.Error": "brush",
    "Alias.OrbIcon.Idle": "brush",
    "Alias.OrbIcon.Connected": "brush",
    "Alias.OrbIcon.Connecting": "brush",
    "Alias.OrbIcon.Error": "brush",
    "Alias.OrbSpecularWidth": "double",
    "Alias.OrbSpecularHeight": "double",
    "Alias.OrbSpecularMargin": "thickness",
    "Alias.OrbSpecularAlign": "horizontal-alignment",
    "Alias.OrbHoverScale": "string",
    "Alias.OrbPressScale": "string",
    "Alias.CheckFill.Hover": "brush",
    "Alias.CheckFill.Pressed": "brush",
    "Alias.CheckFill.Disabled": "brush",
    "Alias.FocusRingBrush": "brush",
    "Alias.StatusText.Connected": "brush",
    "Alias.StatusText.Connecting": "brush",
    "Alias.StatusText.Error": "brush",
    "Alias.TextLink.HoverFill": "brush",
    "Alias.TextLink.HoverStroke": "brush",
}


def alias_lines(skin: str) -> list[str]:
    """给出这一套皮肤的别名令牌声明。

    笔刷别名不再指向另一支笔刷，而是**就地引用同一个 Color 令牌**：Avalonia 的
    `<SolidColorBrush>` 支持 `Color="{DynamicResource Color.X}"`，于是换肤时颜色照样跟着
    第 0 份字典走，同时别名自身是一支类型正确的笔刷（样式可以直接把它赋给 Background）。
    """
    lines = [
        "",
        "    <!-- ============ 别名令牌 ============",
        "         同一个共享样式里、同一个属性在三套皮肤下取值不同的那些位置。",
        f"         取值从 windows/MyProxy/Themes/{skin}.xaml 逐条比出来（.probe/skin-diff.py）",
        "         并搬到这里，于是共享样式引用别名就能让三套皮肤都逐值一致。",
        "         生成器里的表是三份皮肤共用的：任意一套缺一项，生成器直接报错。 -->",
    ]

    for key, values in ALIAS_TOKENS:
        kind = ALIAS_TYPES[key]
        value = values[skin]
        if kind == "brush":
            lines.append(alias_brush_line(key, skin, value))
        elif kind == "double":
            lines.append(f'    <x:Double x:Key="{key}">{resolve_scalar(skin, key, value)}</x:Double>')
        elif kind == "horizontal-alignment":
            lines.append(f'    <HorizontalAlignment x:Key="{key}">{value}</HorizontalAlignment>')
        elif kind == "thickness":
            lines.append(f'    <Thickness x:Key="{key}">{value}</Thickness>')
        else:
            lines.append(f'    <x:String x:Key="{key}">{value}</x:String>')

    return lines


def resolve_scalar(skin: str, key: str, value: str) -> str:
    """把 `Size.*` 这类**数值令牌名**换成它的字面值。

    `<x:Double>` 里放不下资源引用——直接写令牌名会报
    `AVLN1000: The input string 'Size.OrbSpecularWidth' was not in a correct format`
    （这条是实测撞出来的，不是推测），所以数值类别名在**生成期**就把值取出来写死。
    三套皮肤各自生成一份、取值各不相同，这正是别名存在的意义；与笔刷别名引用
    Color 令牌是同一件事的两种写法（那边能引用是因为 `<SolidColorBrush>` 装得下 Color）。
    """
    if value in SCALAR_FOR_SKIN[skin]:
        return SCALAR_FOR_SKIN[skin][value]

    try:
        float(value)
    except ValueError as exc:
        raise KeyError(
            f"别名 {key} 在 {skin} 里的取值 {value} 既不是数字，也不是同一份字典里的数值令牌"
        ) from exc

    return value


def alias_brush_line(key: str, skin: str, value: str) -> str:
    """笔刷别名 → 一行 XAML 声明。

    **不能指向另一支笔刷**（`Color="{DynamicResource Brush.X}"` 这种写法 Avalonia 不认，
    笔刷里装的是 Color 不是 Brush），所以分两种情形，各自都保留「换肤跟着走」：
      · 取值是 `<SolidColorBrush>` 令牌（`Brush.*`）→ 引用它背后的 **Color 令牌**；
        Color 令牌同样在第 0 份字典里，换肤照旧生效。
      · 取值是渐变令牌（Ceramic 的 `Glaze.*`）→ 把渐变**整段搬过来**（含色标）。
        这样它自身仍是渐变笔刷，另两套皮肤在同一个 key 上是实色笔刷也不冲突——
        样式只引用别名 key，不关心它是实色还是渐变。
    """
    if value.startswith("Brush."):
        color_key = f"Color.{value[len('Brush.'):]}"
        colors = COLORS_FOR_SKIN[skin]
        if color_key in colors:
            return f'    <SolidColorBrush x:Key="{key}" Color="{{DynamicResource {color_key}}}" />'

        # 这支笔刷的颜色是**内联字面量**（Ceramic 的 `Brush.Glaze.*` 就是这种），
        # 没有可引用的 Color 令牌，于是把颜色值抄进来。字面量本来就是这套皮肤的
        # 定义，抄一次不引入第二处真相——真相还是那份 WPF 字典，这里由生成器搬。
        inline = SOLID_BRUSH_COLORS_FOR_SKIN[skin].get(value)
        if inline is not None:
            return f'    <SolidColorBrush x:Key="{key}" Color="{inline}" />'

        raise KeyError(
            f"别名 {key} 在 {skin} 里指向 {value}，但同一份字典里既没有 {color_key}，"
            "也不是内联颜色的 SolidColorBrush，不能靠猜回落"
        )

    gradient = GRADIENTS_FOR_SKIN[skin].get(value)
    if gradient is None:
        raise KeyError(
            f"别名 {key} 在 {skin} 里的取值 {value} 既不是 Brush.* 令牌也不是已知渐变令牌"
        )

    start, end, stops = gradient
    lines = [
        f'    <LinearGradientBrush x:Key="{key}" StartPoint="{start}" EndPoint="{end}">'
    ]
    lines += [f'        <GradientStop Offset="{offset}" Color="{color}" />' for offset, color in stops]
    lines.append("    </LinearGradientBrush>")
    return "\n".join(lines)


def index_skin(skin: str):
    """把一套皮肤的字典扫一遍：有哪些 Color 令牌、有哪些渐变令牌（含色标）。

    只认这三样，是因为别名表里只用到这三样；读到别的就当没有——报错由调用方负责。
    """
    tree = ET.parse(THEMES_FOR_SKIN[skin])
    colors: set[str] = set()
    gradients: dict[str, tuple[str, str, list[tuple[str, str]]]] = {}
    solid: dict[str, str] = {}
    scalars: dict[str, str] = {}
    for element in tree.getroot():
        if not isinstance(element.tag, str):
            continue
        key = element.get(KEY)
        if not key:
            continue
        tag = local(element.tag)
        if tag == "Double":
            scalars[key] = (element.text or "").strip()
        elif tag == "Color":
            colors.add(key)
        elif tag == "SolidColorBrush" and element.get("Color", "").startswith("#"):
            solid[key] = element.get("Color")
        elif tag in {"LinearGradientBrush", "RadialGradientBrush"}:
            stops = [
                (stop.get("Offset", "0"), stop.get("Color", "#00000000"))
                for stop in element
                if local(stop.tag) == "GradientStop"
            ]
            gradients[key] = (
                element.get("StartPoint", "0,0"),
                element.get("EndPoint", "1,1"),
                stops,
            )
    return colors, gradients, solid, scalars


# 字体是**唯一**无法逐字节一致的地方：Windows 的 `Segoe UI` 与 `Microsoft YaHei UI`
# 都是 Windows 授权字体，Linux 上没有。移植方式是保留令牌键、换取值：
# 拉丁走随包字体 Inter（Avalonia 的 `.WithInterFont()`），CJK 交给系统按序回退
# （思源黑体 → 文泉驿 → 系统无衬线）。字号、行高与字重层级一字不改。
FONT_SUBSTITUTION = (
    "Inter, Noto Sans CJK SC, Source Han Sans SC, Noto Sans SC, "
    "WenQuanYi Micro Hei, DejaVu Sans, sans-serif"
)


def substitute_font(value: str) -> str:
    """把 Windows 字体族换成 Linux 上真实存在的等价族。"""
    if "Segoe UI" in value or "Microsoft YaHei" in value:
        return FONT_SUBSTITUTION
    return value


def convert_dictionary(path: Path, header: str) -> Converter:
    tree = ET.parse(path)
    converter = Converter()
    for child in tree.getroot():
        if not isinstance(child.tag, str):
            continue
        converter.convert(child)
    return converter


def render(name: str, source: Path, converter: Converter) -> str:
    body = "\n".join(converter.lines) + "\n".join(alias_lines(name))
    # 注意：XML 注释里不能出现两个连续的连字符（`--`），所以下面这段说明里
    # 不能写命令行开关的名字。这不是洁癖——写进去之后 Avalonia 的 XAML 编译器
    # 会直接报 AVLN1001「not well-formed」，而错误位置指向注释本身，很难一眼看出。
    return f"""<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:ea="using:Avalonia.Animation.Easings">
    <!--
    ================================================================================
    由 windows/MyProxy/Themes/{source.name} 机械生成，**不要手工编辑**。
    ================================================================================
    重新生成：见本仓库 linux/MyProxyLinux/tools/port_theme_tokens.py 头部注释
    （加写盘开关即可，具体开关见脚本的帮助文本）。

    这里只包含**值型令牌**：颜色、笔刷、渐变、尺寸、边距、圆角、字体、动效时长与缓动、
    图标几何、阴影。取值与 Windows 端逐值相同（生成器只做方言映射，不做单位或数值换算，
    唯一的算式是 WPF 的「角度 + 距离」阴影换成 Avalonia 的「偏移」）。

    不在这里的东西：40 多个样式。Avalonia 用 ControlTheme / 选择器表达同样的东西，
    那部分在共用的样式文件里维护（重写的是**表达方式**，不是取值）。
    ================================================================================
    -->
{body}
</ResourceDictionary>
"""


def assert_well_formed(text: str, target: Path) -> None:
    """写盘前自检：生成的必须是合法 XML。

    这条自检是有来历的：XML 注释里出现 `--` 会让文件在 Avalonia 编译器那里报
    AVLN1001，而错误信息指向注释所在的列——排查要绕一圈。生成器自己先炸，
    错误就落在这里。
    """
    ET.fromstring(text)
    _ = target


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--write", action="store_true", help="写出生成结果")
    parser.add_argument("--check", action="store_true", help="只报告，不写文件")
    args = parser.parse_args(argv)
    if not args.write and not args.check:
        parser.error("choose --check or --write")

    total_converted = 0
    problems = 0

    # 先把三套皮肤的源字典索引一遍：别名表里那些「指向另一支令牌」的取值要在这里核对，
    # 核对不过就直接抛（宁可不生成，也不生成一份悄悄回落成默认值的字典）。
    for skin in THEMES_FOR_SKIN:
        (COLORS_FOR_SKIN[skin], GRADIENTS_FOR_SKIN[skin],
         SOLID_BRUSH_COLORS_FOR_SKIN[skin], SCALAR_FOR_SKIN[skin]) = index_skin(skin)

    for source in sorted(WINDOWS_THEMES.glob("*.xaml")):
        converter = convert_dictionary(source, source.stem)
        target = LINUX_THEMES / f"{source.stem}.Tokens.axaml"
        output = render(source.stem, source, converter)

        print(f"===== {source.name} → {target.name}")
        print(f"  值型令牌转换 {len(converter.converted)} 项")
        if converter.manual:
            print(f"  需手工重写 {len(converter.manual)} 项：")
            for key, tag, reason in converter.manual[:6]:
                print(f"     {tag:16s} {key:34s} {reason}")
            if len(converter.manual) > 6:
                print(f"     …另有 {len(converter.manual) - 6} 项")
        if converter.unknown:
            problems += len(converter.unknown)
            print(f"  !! 未识别的元素 {len(converter.unknown)} 项（生成器不认识的类型，会丢失）：")
            for key, tag in converter.unknown:
                print(f"     {tag:20s} {key}")
        if converter.losses:
            print(f"  注：以下参数在 Avalonia 里没有对应物，已如实丢掉（{len(converter.losses)} 处）：")
            for loss in converter.losses:
                print(f"     {loss}")
        total_converted += len(converter.converted)

        if args.write:
            target.parent.mkdir(parents=True, exist_ok=True)
            assert_well_formed(output, target)
            target.write_text(output, encoding="utf-8", newline="\n")
            print(f"  已写出 {target.relative_to(ROOT)}")
        print()

    print(f"合计转换 {total_converted} 项；未识别 {problems} 项")
    return 1 if problems else 0


if __name__ == "__main__":
    raise SystemExit(main())
