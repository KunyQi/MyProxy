using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MyProxy.Tests;

/// <summary>
/// 所有图标几何的自动验收：把每个 <c>PathGeometry</c> 的**墨迹包围盒**算出来，
/// 断言它居中于自己的图标盒、且不溢出。
///
/// 为什么不靠肉眼：power 图标的第一版把圆心算到了盒子上方，弧只剩两截「括号」
/// 露在盒内——而那在 140px 的球上不放大**仍然像个 power 图标**，目视验收直接放过。
/// 包围盒是可计算的，就不该交给眼睛。
///
/// 新增图标必须在 <see cref="Icons"/> 里补一行，否则等于没有验收。
/// </summary>
[TestClass]
public sealed class IconGeometryTests
{
    /// <summary>半像素级容差：Bounds 是解析解，只留一点给浮点。</summary>
    private const double Tolerance = 0.05;

    /// <summary>
    /// (几何键, 盒尺寸的 token 键, 描边宽度的 token 键, 是否要求居中)。
    ///
    /// 尺寸只许写 token 键，不许写数值：写死的话，改了 Size.TrafficArrow 而不动几何坐标时
    /// 这套测试会拿旧盒子去算、照样报绿，而界面上图标已经靠向盒子左上角了——那正是
    /// 这套测试本该拦住的一类偏移。
    /// </summary>
    private static readonly (string Key, string BoxToken, string StrokeToken, bool Centred)[] Icons =
    [
        ("Geometry.PowerArc", "Size.OrbIcon", "Stroke.OrbIcon", false),   // 与 PowerStem 合起来才居中
        ("Geometry.PowerStem", "Size.OrbIcon", "Stroke.OrbIcon", false),
        ("Geometry.ArrowUp", "Size.TrafficArrow", "Stroke.Icon", true),
        ("Geometry.ArrowDown", "Size.TrafficArrow", "Stroke.Icon", true),
        ("Geometry.SpinnerArc", "Size.Spinner", "Stroke.Spinner", false), // 只占圆的一个象限
    ];

    [TestMethod]
    public void EveryIconFitsInsideItsBox() => RunSta(() =>
    {
        ResourceDictionary tokens = LoadTokens();

        foreach ((string key, string boxToken, string strokeToken, bool centred) in Icons)
        {
            double box = (double)tokens[boxToken];
            var geometry = (Geometry)tokens[key];
            Rect ink = geometry.GetRenderBounds(PenFor((double)tokens[strokeToken]));

            Assert.IsTrue(
                ink.Left >= -Tolerance && ink.Top >= -Tolerance
                && ink.Right <= box + Tolerance && ink.Bottom <= box + Tolerance,
                $"{key} 的墨迹溢出 {box}×{box} 盒：{Describe(ink)}");

            if (!centred)
            {
                continue;
            }

            Assert.AreEqual(box / 2, (ink.Left + ink.Right) / 2, Tolerance,
                $"{key} 水平未居中：{Describe(ink)}");
            Assert.AreEqual(box / 2, (ink.Top + ink.Bottom) / 2, Tolerance,
                $"{key} 垂直未居中：{Describe(ink)}");
        }
    });

    [TestMethod]
    public void PowerGlyphInkIsCentredInItsBox() => RunSta(() =>
    {
        ResourceDictionary tokens = LoadTokens();
        Pen pen = PenFor((double)tokens["Stroke.OrbIcon"]);
        double box = (double)tokens["Size.OrbIcon"];

        Rect ink = Rect.Union(
            ((Geometry)tokens["Geometry.PowerArc"]).GetRenderBounds(pen),
            ((Geometry)tokens["Geometry.PowerStem"]).GetRenderBounds(pen));

        Assert.AreEqual(box / 2, (ink.Left + ink.Right) / 2, Tolerance, $"水平未居中：{Describe(ink)}");
        Assert.AreEqual(box / 2, (ink.Top + ink.Bottom) / 2, Tolerance, $"垂直未居中：{Describe(ink)}");

        // 形状健全性：power 图标是「开口朝上的弧 + 一条竖线」，弧必须占到盒子下半部分。
        // 这一条专门拦 large-arc / sweep 填反导致圆心跑到弦另一侧的情况。
        Rect arc = ((Geometry)tokens["Geometry.PowerArc"]).GetRenderBounds(pen);
        Assert.IsTrue(arc.Bottom > box * 0.75,
            $"弧没伸到盒子下部，圆心多半在弦的另一侧：{Describe(arc)}");
        Assert.IsTrue(arc.Width > box * 0.55, $"弧太窄，不像一个环：{Describe(arc)}");
    });

    [TestMethod]
    public void ArrowsAreMirrorImagesWithMatchingWeight() => RunSta(() =>
    {
        ResourceDictionary tokens = LoadTokens();
        Pen pen = PenFor((double)tokens["Stroke.Icon"]);
        double box = (double)tokens["Size.TrafficArrow"];

        Rect up = ((Geometry)tokens["Geometry.ArrowUp"]).GetRenderBounds(pen);
        Rect down = ((Geometry)tokens["Geometry.ArrowDown"]).GetRenderBounds(pen);

        // 上下行标签左右并排，两个箭头的视重必须一致，否则一眼就看出一边重一边轻。
        Assert.AreEqual(up.Width, down.Width, Tolerance, "两个箭头的宽度必须相同");
        Assert.AreEqual(up.Height, down.Height, Tolerance, "两个箭头的高度必须相同");

        // 光比包围盒不够——两个毫不相干的形状也能有同样的外框。逐点比对：
        // 把 ArrowDown 关于盒子水平中线翻过来（y -> box - y），应当与 ArrowUp 逐点重合。
        // 不用 FillContains：这两个几何是开放的描边路径，填充判定会先把它们隐式闭合，
        // 比的就不是真正画出来的形状了。
        Geometry flipped = ((Geometry)tokens["Geometry.ArrowDown"]).Clone();
        flipped.Transform = new MatrixTransform(1, 0, 0, -1, 0, box);

        Point[] mirrored = Outline(flipped);
        Point[] expected = Outline((Geometry)tokens["Geometry.ArrowUp"]);

        Assert.AreEqual(expected.Length, mirrored.Length,
            "翻转后的 ArrowDown 与 ArrowUp 的顶点数应相同");
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(expected[i].X, mirrored[i].X, Tolerance,
                $"第 {i} 个顶点的 X 不匹配：{mirrored[i]} vs {expected[i]}");
            Assert.AreEqual(expected[i].Y, mirrored[i].Y, Tolerance,
                $"第 {i} 个顶点的 Y 不匹配：{mirrored[i]} vs {expected[i]}");
        }
    });

    /// <summary>展平成顶点序列，用来逐点比对两个几何是否真的一样。</summary>
    private static Point[] Outline(Geometry geometry)
    {
        PathGeometry flat = geometry.GetFlattenedPathGeometry();
        var points = new List<Point>();
        foreach (PathFigure figure in flat.Figures)
        {
            points.Add(figure.StartPoint);
            foreach (PathSegment segment in figure.Segments)
            {
                if (segment is PolyLineSegment poly)
                {
                    points.AddRange(poly.Points);
                }
                else if (segment is LineSegment line)
                {
                    points.Add(line.Point);
                }
            }
        }

        return points.ToArray();
    }

    /// <summary>OrbMotionTests 也用它：两套测试读的必须是同一份 DesignTokens。</summary>
    internal static ResourceDictionary LoadTokens()
        => (ResourceDictionary)Application.LoadComponent(
            new Uri("/MyProxy;component/Themes/DesignTokens.xaml", UriKind.Relative));

    private static Pen PenFor(double thickness) => new(Brushes.Black, thickness)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round
    };

    private static string Describe(Rect r) => string.Create(
        CultureInfo.InvariantCulture,
        $"L={r.Left:0.00} T={r.Top:0.00} R={r.Right:0.00} B={r.Bottom:0.00} " +
        $"cx={(r.Left + r.Right) / 2:0.00} cy={(r.Top + r.Bottom) / 2:0.00}");

    /// <summary>
    /// 诊断工具，默认 no-op：把 <c>MYPROXY_ICON_PROBE</c> 设成一个输出文件路径，
    /// 它会把四种 large-arc / sweep 组合的包围盒写进去，用来确定该填哪一组。
    /// 这两个 flag 的语义靠读文档很容易记反，量一次比推一次可靠。
    /// </summary>
    [TestMethod]
    public void DumpArcFlagCombinations()
    {
        string? outFile = Environment.GetEnvironmentVariable("MYPROXY_ICON_PROBE");
        if (string.IsNullOrWhiteSpace(outFile))
        {
            return;
        }

        RunSta(() =>
        {
            Pen pen = PenFor(1.8);
            foreach (int large in new[] { 0, 1 })
            {
                foreach (int sweep in new[] { 0, 1 })
                {
                    Geometry g = Geometry.Parse($"M 10.54,13.36 A 12,12 0 {large} {sweep} 29.46,13.36");
                    File.AppendAllText(
                        outFile,
                        $"large={large} sweep={sweep}  {Describe(g.GetRenderBounds(pen))}{Environment.NewLine}");
                }
            }
        });
    }

    internal static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "icon geometry test timed out");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
