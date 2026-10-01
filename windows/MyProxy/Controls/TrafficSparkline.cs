using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MyProxy.Core;

// 项目的 global using 同时引入了 System.Drawing 与 System.Windows.Media，
// Brush / Brushes / Pen / Point 在两边同名，必须显式指定 WPF 的那一套。
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace MyProxy.Controls;

/// <summary>
/// 上下行速率的对称面积图：中轴以上是上行，以下是下行，两侧共用同一条基线。
///
/// 自绘而非组合现成控件，是因为这里要的每一条都不是 <c>Polyline</c> 能给的：
/// 共享基线的镜像布局、闲置时不放大噪声的归一化、以及样本不足时的右对齐。
/// </summary>
public sealed class TrafficSparkline : FrameworkElement
{
    /// <summary>
    /// 归一化的下限，64 KiB/s。没有它，闲置时几百字节的心跳流量会被拉满整个图高，
    /// 看起来像在疯狂跑流量——Sparkline 的纵轴必须有一个「安静就是安静」的地板。
    /// </summary>
    private const double MinimumScaleBytesPerSecond = 64 * 1024;

    /// <summary>基线两侧各留一点空隙，避免上下两条曲线在零值处糊成一条粗线。</summary>
    private const double BaselineGap = 1.5;

    /// <summary>
    /// 纵轴留 30% 余量。不留的话峰值恰好贴着上下边缘，被裁成一条平顶，看起来像
    /// 信号削波而不是流量高点；8% 太少——半高只有 20px 时那点余量不足 2px，
    /// 峰顶平缓的波形照样读成贴边。
    /// </summary>
    private const double PeakHeadroom = 1.3;

    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples),
        typeof(IReadOnlyList<TrafficRate>),
        typeof(TrafficSparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity),
        typeof(int),
        typeof(TrafficSparkline),
        new FrameworkPropertyMetadata(TrafficRateMonitor.DefaultCapacity, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UplinkBrushProperty = DependencyProperty.Register(
        nameof(UplinkBrush),
        typeof(Brush),
        typeof(TrafficSparkline),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DownlinkBrushProperty = DependencyProperty.Register(
        nameof(DownlinkBrush),
        typeof(Brush),
        typeof(TrafficSparkline),
        new FrameworkPropertyMetadata(Brushes.SeaGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BaselineBrushProperty = DependencyProperty.Register(
        nameof(BaselineBrush),
        typeof(Brush),
        typeof(TrafficSparkline),
        new FrameworkPropertyMetadata(Brushes.Gainsboro, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<TrafficRate>? Samples
    {
        get => (IReadOnlyList<TrafficRate>?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    /// <summary>横轴容量。样本不足时图形右对齐，左侧留白，于是新数据始终从右侧推入。</summary>
    public int Capacity
    {
        get => (int)GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    public Brush UplinkBrush
    {
        get => (Brush)GetValue(UplinkBrushProperty);
        set => SetValue(UplinkBrushProperty, value);
    }

    public Brush DownlinkBrush
    {
        get => (Brush)GetValue(DownlinkBrushProperty);
        set => SetValue(DownlinkBrushProperty, value);
    }

    public Brush BaselineBrush
    {
        get => (Brush)GetValue(BaselineBrushProperty);
        set => SetValue(BaselineBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        double midline = Math.Round(height / 2);
        Pen baselinePen = EnsureBaselinePen();

        IReadOnlyList<TrafficRate> samples = Samples ?? Array.Empty<TrafficRate>();
        if (samples.Count < 2)
        {
            drawingContext.DrawLine(baselinePen, new Point(0, midline + 0.5), new Point(width, midline + 0.5));
            return;
        }

        int capacity = Math.Max(Capacity, samples.Count);
        double step = width / Math.Max(capacity - 1, 1);
        // 右对齐：样本不足时把图推到右边，新数据始终从右侧进入。
        double originX = width - ((samples.Count - 1) * step);

        // 上下行各自归一化，不共用刻度。真实流量里下行常比上行大一到两个数量级，
        // 共用刻度会把上行永远压成贴着中轴的一条直线——那条线没有任何信息量。
        // 代价是两侧纵轴刻度不同，但 Sparkline 读的是趋势，绝对值由两端的数字标签给出。
        double upScale = MinimumScaleBytesPerSecond;
        double downScale = MinimumScaleBytesPerSecond;
        foreach (TrafficRate sample in samples)
        {
            upScale = Math.Max(upScale, sample.UplinkBytesPerSecond);
            downScale = Math.Max(downScale, sample.DownlinkBytesPerSecond);
        }

        double halfHeight = Math.Max(midline - BaselineGap, 1);

        DrawBand(drawingContext, samples, s => s.UplinkBytesPerSecond,
            originX, step, midline - BaselineGap, -halfHeight, upScale * PeakHeadroom,
            Ensure(ref _uplinkBrushes, UplinkBrush));
        DrawBand(drawingContext, samples, s => s.DownlinkBytesPerSecond,
            originX, step, midline + BaselineGap, halfHeight, downScale * PeakHeadroom,
            Ensure(ref _downlinkBrushes, DownlinkBrush));

        // 基线最后画：两片半透明面积都压在中轴上，先画会被盖住。
        drawingContext.DrawLine(baselinePen, new Point(0, midline + 0.5), new Point(width, midline + 0.5));
    }

    /// <param name="baseY">该侧曲线的零点。</param>
    /// <param name="span">从零点到图形边缘的有向距离；上行为负（向上），下行为正。</param>
    private static void DrawBand(
        DrawingContext drawingContext,
        IReadOnlyList<TrafficRate> samples,
        Func<TrafficRate, double> selector,
        double originX,
        double step,
        double baseY,
        double span,
        double scale,
        BandBrushes brushes)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(originX, baseY), isFilled: true, isClosed: true);

            for (int i = 0; i < samples.Count; i++)
            {
                double value = Math.Max(selector(samples[i]), 0);
                double y = baseY + (span * Math.Min(value / scale, 1));
                // 只描实际速率曲线；面积的竖边与基线不应被画成额外的数据线。
                context.LineTo(new Point(originX + (i * step), y), isStroked: i > 0, isSmoothJoin: true);
            }

            // 收回基线闭合成面积。
            context.LineTo(new Point(originX + ((samples.Count - 1) * step), baseY), isStroked: false, isSmoothJoin: false);
            context.LineTo(new Point(originX, baseY), isStroked: false, isSmoothJoin: false);
        }

        geometry.Freeze();

        drawingContext.DrawGeometry(brushes.Fill, brushes.Pen, geometry);
    }

    /// <summary>
    /// 一条带的绘制资源。每秒重绘一次、每次两条带，照 Clone + Freeze 就是每分钟
    /// 240 个短命的笔刷与画笔；而描边色几乎从不变，缓存下来即可。
    ///
    /// 缓存放实例上而不是静态字典：静态字典要跨线程用就得加锁，而每个
    /// <see cref="TrafficSparkline"/> 只被它自己的 UI 线程渲染，实例字段天然没有这个问题。
    /// </summary>
    private readonly struct BandBrushes(Brush source, Brush fill, Pen pen)
    {
        internal Brush Source { get; } = source;
        internal Brush Fill { get; } = fill;
        internal Pen Pen { get; } = pen;

        internal static BandBrushes For(Brush stroke)
        {
            Brush fill = stroke.Clone();
            fill.Opacity = BandFillOpacity;
            fill.Freeze();

            var pen = new Pen(stroke, BandStrokeThickness);
            pen.Freeze();

            return new BandBrushes(stroke, fill, pen);
        }
    }

    private const double BandFillOpacity = 0.22;
    private const double BandStrokeThickness = 1.5;

    private BandBrushes _uplinkBrushes;
    private BandBrushes _downlinkBrushes;
    private Pen? _baselinePen;
    private Brush? _baselinePenSource;

    private static BandBrushes Ensure(ref BandBrushes cache, Brush stroke)
    {
        if (!ReferenceEquals(cache.Source, stroke))
        {
            cache = BandBrushes.For(stroke);
        }

        return cache;
    }

    private Pen EnsureBaselinePen()
    {
        Brush stroke = BaselineBrush;
        if (_baselinePen is null || !ReferenceEquals(_baselinePenSource, stroke))
        {
            var pen = new Pen(stroke, 1) { DashStyle = DashStyles.Dash };
            pen.Freeze();
            _baselinePen = pen;
            _baselinePenSource = stroke;
        }

        return _baselinePen;
    }

    /// <summary>把字节/秒格式化成界面文案。单位到 MB/s 为止——再大的数在这个窗口里没有意义。</summary>
    public static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{Math.Max(bytesPerSecond, 0):0} B/s");
        }

        if (bytesPerSecond < 1024 * 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytesPerSecond / 1024:0.0} KB/s");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{bytesPerSecond / (1024 * 1024):0.00} MB/s");
    }
}
