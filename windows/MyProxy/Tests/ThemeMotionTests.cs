using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Core;

namespace MyProxy.Tests;

[TestClass]
public sealed class ThemeMotionTests
{
    private static ResourceDictionary Load(UiTheme theme)
        => (ResourceDictionary)Application.LoadComponent(new Uri(
            $"/MyProxy;component/Themes/{UiThemes.DictionaryFileName(theme)}", UriKind.Relative));

    private static double Ms(ResourceDictionary d, string key)
        => ((Duration)d[key]).TimeSpan.TotalMilliseconds;

    /// <summary>能量球的四条不变量，三套皮肤逐一过一遍（原先只校验出厂皮肤）。</summary>
    [TestMethod]
    public void EveryThemeKeepsTheOrbBreathInvariants() => IconGeometryTests.RunSta(() =>
    {
        foreach (UiTheme theme in UiThemes.All)
        {
            ResourceDictionary d = Load(theme);

            foreach (string motion in new[] { "Breath", "Pulse" })
            {
                double from = (double)d[$"Motion.Orb.{motion}AlphaFrom"];
                double to = (double)d[$"Motion.Orb.{motion}AlphaTo"];
                double scale = (double)d[$"Motion.Orb.{motion}Scale"];

                Assert.IsTrue(from > 0 && from < to && to <= 1,
                    $"{theme}/{motion}: 透明度区间 {from}..{to} 不合法（起点不能是 0，否则读成闪）");
                Assert.IsTrue(scale > 1.0 && scale < 1.15,
                    $"{theme}/{motion}: 缩放 {scale} 越界。只放大不缩小——球下面压着两个承影圆。");
            }

            Assert.IsTrue(
                (double)d["Motion.Orb.PulseAlphaTo"] > (double)d["Motion.Orb.BreathAlphaTo"],
                $"{theme}: 脉冲（连接中）该比呼吸（已连接）更用力");
        }
    });

    /// <summary>光晕渐变的空间梯度，三套皮肤同样都要有。</summary>
    [TestMethod]
    public void EveryThemeHaloFadesOutSmoothly() => IconGeometryTests.RunSta(() =>
    {
        foreach (UiTheme theme in UiThemes.All)
        {
            ResourceDictionary d = Load(theme);
            double rim = (double)d["Size.Orb"] / (double)d["Size.OrbGlow"];

            foreach (string key in new[] { "Brush.Halo.Accent", "Brush.Halo.Success", "Brush.Halo.Error" })
            {
                var brush = (RadialGradientBrush)d[key];
                Assert.AreEqual(0.5, brush.RadiusX, 1e-9, $"{theme}/{key}: RadiusX 必须是 0.5");
                Assert.AreEqual(0.5, brush.RadiusY, 1e-9, $"{theme}/{key}: RadiusY 必须是 0.5");

                GradientStop[] stops = brush.GradientStops.OrderBy(s => s.Offset).ToArray();
                Assert.IsTrue(stops[^1].Offset >= 1.0 - 1e-9 && stops[^1].Color.A == 0,
                    $"{theme}/{key}: 最外一圈必须全透明，否则光晕有硬边");

                byte[] visible = stops.Where(s => s.Offset >= rim - 1e-9).Select(s => s.Color.A).ToArray();
                Assert.IsTrue(visible.Length >= 4, $"{theme}/{key}: 球缘外只有 {visible.Length} 档，撑不出梯度");
                Assert.AreEqual(visible.Length, visible.Distinct().Count(),
                    $"{theme}/{key}: 球缘外有等 alpha 的停靠点，那一段是平的");
                for (int i = 1; i < visible.Length; i++)
                {
                    Assert.IsTrue(visible[i] < visible[i - 1],
                        $"{theme}/{key}: 第 {i} 档没有继续变淡，光晕不是单调衰减");
                }
            }
        }
    });

    /// <summary>
    /// 瓷白是扁平化：交互动效全部压在 200ms 内，而且**不许回弹**。
    /// 弹性是「有质量的物体」的语言，扁平化里没有物体。
    /// </summary>
    [TestMethod]
    public void PorcelainIsQuickAndNeverBounces() => IconGeometryTests.RunSta(() =>
    {
        ResourceDictionary d = Load(UiTheme.Porcelain);

        foreach (string key in new[]
                 {
                     "Motion.Duration.Fast", "Motion.Duration.Base",
                     "Motion.Duration.Press", "Motion.Duration.Spring", "Motion.Duration.Slide"
                 })
        {
            double ms = Ms(d, key);
            Assert.IsTrue(ms > 0 && ms <= 200,
                $"瓷白 {key} = {ms}ms，超出扁平化这一档允许的 0–200ms");
        }

        foreach (string key in new[] { "Motion.Ease.Spring", "Motion.Ease.Slide", "Motion.Ease.Overshoot" })
        {
            object ease = d[key];
            Assert.IsFalse(ease is ElasticEase or BackEase or BounceEase,
                $"瓷白 {key} 是 {ease.GetType().Name}：扁平化不回弹也不过冲，请用 CubicEase。");
        }
    });

    /// <summary>
    /// 釉陶是拟物：按下去要花时间，松手要弹一次。三条时长都必须**明显长于**瓷白，
    /// 否则「陷进胎里再弹回来」这件事根本看不清，两套皮肤的差别也就只剩配色了。
    /// </summary>
    [TestMethod]
    public void CeramicHasMass() => IconGeometryTests.RunSta(() =>
    {
        ResourceDictionary flat = Load(UiTheme.Porcelain);
        ResourceDictionary clay = Load(UiTheme.Ceramic);

        foreach (string key in new[]
                 { "Motion.Duration.Press", "Motion.Duration.Spring", "Motion.Duration.Slide" })
        {
            Assert.IsTrue(Ms(clay, key) > Ms(flat, key),
                $"釉陶 {key} = {Ms(clay, key)}ms，并不比瓷白的 {Ms(flat, key)}ms 长。"
                + "拟物的前提是动作有重量。");
        }

        Assert.IsTrue(Ms(clay, "Motion.Duration.Spring") >= 220,
            $"釉陶回弹只有 {Ms(clay, "Motion.Duration.Spring")}ms，弹性曲线在这个时长里走不完一个来回。");
        Assert.IsTrue(Ms(clay, "Motion.Duration.Press") < Ms(clay, "Motion.Duration.Spring"),
            "按下要跟手、回弹才慢：Press 必须短于 Spring。");

        Assert.IsInstanceOfType<ElasticEase>(clay["Motion.Ease.Spring"],
            "釉陶的松手回弹必须是弹性曲线，这是它与瓷白最核心的一处分野。");
        Assert.IsInstanceOfType<BackEase>(clay["Motion.Ease.Slide"],
            "釉陶的滑块要过冲一点再坐进凹槽，用 BackEase。");
    });
}
