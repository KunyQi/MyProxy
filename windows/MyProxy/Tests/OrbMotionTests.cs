using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MyProxy.Tests;

/// <summary>
/// 能量球呼吸动效的静态验收。断言的是「改坏了会静默」的那几条，不是好不好看。
///
/// 为什么值得写：呼吸的连续感靠的是光晕在**空间上**是渐变的——不同半径处的像素
/// 在不同时刻跨过 8 位量化边界，整帧的变化频率才能远高于单个像素。这件事全部
/// 押在几个渐变停靠点上，而停靠点写错既不会编译失败也不会在静态截图里露馅
/// （静止时光晕的透明度是 0，截图上根本看不见它）。
///
/// <see cref="OrbFrameProbe"/> 能量到真实级数，但它默认 no-op、要人工跑。
/// 这里守住它依赖的前提。
/// </summary>
[TestClass]
public sealed class OrbMotionTests
{
    private static readonly string[] HaloBrushes =
    [
        "Brush.Halo.Accent",
        "Brush.Halo.Success",
        "Brush.Halo.Error"
    ];

    /// <summary>
    /// 球缘在光晕渐变上的 offset = 球直径 ÷ 光晕直径。这个比例以内被球完全盖住。
    /// 从 token 现算而不是写死：改了 Size.OrbGlow 而忘了重算停靠点时，这条才会红。
    /// </summary>
    private static double OrbEdgeOffset(ResourceDictionary tokens)
        => (double)tokens["Size.Orb"] / (double)tokens["Size.OrbGlow"];

    [TestMethod]
    public void HaloGradientsFadeOutAcrossTheVisibleRing() => IconGeometryTests.RunSta(() =>
    {
        ResourceDictionary tokens = IconGeometryTests.LoadTokens();

        foreach (string key in HaloBrushes)
        {
            var brush = (RadialGradientBrush)tokens[key];

            // RadiusX/Y 是相对包围盒的。不是 0.5 的话 offset 1.0 落不到 196 的边上，
            // 渐变要么被截断要么摊得太开——球体渐变上踩过一次同样的坑。
            Assert.AreEqual(0.5, brush.RadiusX, 1e-9, $"{key}: RadiusX 必须是 0.5");
            Assert.AreEqual(0.5, brush.RadiusY, 1e-9, $"{key}: RadiusY 必须是 0.5");

            GradientStop[] stops = brush.GradientStops.OrderBy(s => s.Offset).ToArray();
            Assert.IsTrue(stops[^1].Offset >= 1.0 - 1e-9 && stops[^1].Color.A == 0,
                $"{key}: 最外一圈必须完全透明，否则光晕会有一条硬边");

            // 可见的只有球缘到光晕外缘这一段。要让「不同半径不同 alpha」成立，
            // 这一段里至少得有三个互不相同的 alpha 台阶。
            double rim = OrbEdgeOffset(tokens);
            byte[] visible = stops
                .Where(s => s.Offset >= rim - 1e-9)
                .Select(s => s.Color.A)
                .ToArray();

            Assert.IsTrue(visible.Length >= 4,
                $"{key}: 球缘外只有 {visible.Length} 个停靠点，撑不出空间梯度");
            Assert.AreEqual(visible.Length, visible.Distinct().Count(),
                $"{key}: 球缘外存在等 alpha 的停靠点，那一段是平的");

            for (int i = 1; i < visible.Length; i++)
            {
                Assert.IsTrue(visible[i] < visible[i - 1],
                    $"{key}: 第 {i} 个停靠点没有继续变淡，光晕不是单调衰减的");
            }
        }
    });

    [TestMethod]
    public void BreathAndPulseAlphaRangesAreSaneAndOrdered() => IconGeometryTests.RunSta(() =>
    {
        ResourceDictionary tokens = IconGeometryTests.LoadTokens();

        foreach (string motion in new[] { "Breath", "Pulse" })
        {
            double from = (double)tokens[$"Motion.Orb.{motion}AlphaFrom"];
            double to = (double)tokens[$"Motion.Orb.{motion}AlphaTo"];
            double scale = (double)tokens[$"Motion.Orb.{motion}Scale"];

            Assert.IsTrue(from >= 0 && from < to && to <= 1,
                $"{motion}: 透明度区间 {from}..{to} 不合法");

            // 起点不为 0：完全消失再出现读起来是闪，不是呼吸。
            Assert.IsTrue(from > 0, $"{motion}: 起点透明度不能是 0");

            // 缩放只放大不缩小——球下面压着两个 140 的承影圆，缩回去会露白边。
            Assert.IsTrue(scale > 1.0 && scale < 1.15, $"{motion}: 缩放 {scale} 超出合理范围");
        }

        Assert.IsTrue(
            (double)tokens["Motion.Orb.PulseAlphaTo"] > (double)tokens["Motion.Orb.BreathAlphaTo"],
            "脉冲（连接中）该比呼吸（已连接）更用力，峰值透明度必须更高");
    });
}
