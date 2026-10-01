using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using MyProxy.Core;

// 这是 windows/MyProxy/Controls/TrafficSparkline.cs 的 Avalonia 版，逐条对齐：
//
//   WPF                                              Avalonia 11（本文件）
//   -----------------------------------------------  --------------------------------------------------
//   FrameworkElement + OnRender                      Control + Render(DrawingContext)
//   DependencyProperty.Register                      AvaloniaProperty.Register（StyledProperty）
//   FrameworkPropertyMetadataOptions.AffectsRender   属性变化时 InvalidateVisual()（见 OnPropertyChanged）
//   System.Windows.Media.Brush（抽象类）              Avalonia 的 IBrush（接口）
//   Brushes.SteelBlue / SeaGreen / Gainsboro         令牌 Brush.AccentText / SuccessText / Border（见下方常量）
//   BeginFigure(point, isFilled, isClosed)           BeginFigure(point, isFilled) + EndFigure(isClosed)
//   LineTo(point, isStroked, isSmoothJoin)           LineTo(point, isStroked)；isSmoothJoin 用 LineJoin.Round 近似
//   DashStyles.Dash（{2,2}, offset 1）               DashStyle.Dash（逐值相同：{2,2}, offset 1）
//   Freeze() / Clone()                               没有对应物：画笔按源笔刷引用缓存，面积用 PushOpacity 压透明度
//   SnapsToDevicePixels / GuidelineSet               没有对应物：两边都靠「像素中心在半整数上」对齐（见 Render）
//
// 几何算法（采样→坐标、上下行各自归一化、样本不足时右对齐、绘制顺序、空/单点数据的处理）
// 与 Windows 端逐句相同，所以同一份样本在两端画出同一张图。

namespace MyProxy.Controls;

/// <summary>
/// 上下行速率的对称面积图：中轴以上是上行，以下是下行，两侧共用同一条基线。
///
/// 自绘而非组合现成控件，是因为这里要的每一条都不是 <c>Polyline</c> 能给的：
/// 共享基线的镜像布局、闲置时不放大噪声的归一化、以及样本不足时的右对齐。
/// </summary>
public sealed class TrafficSparkline : Control
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

    /// <summary>面积填充透明度。</summary>
    private const double BandFillOpacity = 0.22;

    /// <summary>曲线描边宽度。基线的宽度不在这里，走 <c>Size.Hairline</c> 令牌。</summary>
    private const double BandStrokeThickness = 1.5;

    // 令牌只能这样取。XAML 里的 {DynamicResource} 在 C# 里没有对应物：控件的三个笔刷属性
    // 一旦没人设，就去逻辑树上的资源字典里按 key 现取（App.axaml 合并了
    // Themes/DesignTokens.Tokens.axaml，换肤换的就是它），取不到才用下面与令牌同值的常量。
    // 常量必须与令牌一致——它是「控件先于资源字典存在」时的兜底，不是第二个真值来源。
    private const string UplinkBrushToken = "Brush.AccentText";
    private const string DownlinkBrushToken = "Brush.SuccessText";
    private const string BaselineBrushToken = "Brush.Border";
    private const string HairlineToken = "Size.Hairline";

    /// <summary>回退值 = <c>Color.AccentText</c> #4B7A8A（默认皮肤；三套皮肤各有各的值，走令牌时自然跟着换）。</summary>
    private static readonly IBrush DefaultUplinkBrush = new ImmutableSolidColorBrush(Color.Parse("#4B7A8A"));

    /// <summary>回退值 = <c>Color.SuccessText</c> #3F7159。</summary>
    private static readonly IBrush DefaultDownlinkBrush = new ImmutableSolidColorBrush(Color.Parse("#3F7159"));

    /// <summary>回退值 = <c>Color.Border</c> #CFD6D2。</summary>
    private static readonly IBrush DefaultBaselineBrush = new ImmutableSolidColorBrush(Color.Parse("#CFD6D2"));

    /// <summary>回退值 = <c>Size.Hairline</c> 1（三套皮肤都是 1）。</summary>
    private const double DefaultHairlineThickness = 1;

    public static readonly StyledProperty<IReadOnlyList<TrafficRate>?> SamplesProperty =
        AvaloniaProperty.Register<TrafficSparkline, IReadOnlyList<TrafficRate>?>(nameof(Samples));

    public static readonly StyledProperty<int> CapacityProperty =
        AvaloniaProperty.Register<TrafficSparkline, int>(nameof(Capacity), TrafficRateMonitor.DefaultCapacity);

    public static readonly StyledProperty<IBrush?> UplinkBrushProperty =
        AvaloniaProperty.Register<TrafficSparkline, IBrush?>(nameof(UplinkBrush));

    public static readonly StyledProperty<IBrush?> DownlinkBrushProperty =
        AvaloniaProperty.Register<TrafficSparkline, IBrush?>(nameof(DownlinkBrush));

    public static readonly StyledProperty<IBrush?> BaselineBrushProperty =
        AvaloniaProperty.Register<TrafficSparkline, IBrush?>(nameof(BaselineBrush));

    public IReadOnlyList<TrafficRate>? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    /// <summary>横轴容量。样本不足时图形右对齐，左侧留白，于是新数据始终从右侧推入。</summary>
    public int Capacity
    {
        get => GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    /// <summary>
    /// 上行曲线的颜色。视图把它设成 <c>{DynamicResource Brush.AccentText}</c>；
    /// 没人设过时按「资源树里的同名令牌 → 与令牌同值的常量」解析，所以换肤后取到的是新皮肤的颜色。
    /// （Windows 端的默认值是 <c>Brushes.SteelBlue</c>——视图总会覆盖它，这里改成令牌是为了
    /// 「没设」时也跟皮肤一致；顺带把 WPF 在属性被显式设成 null 时会抛
    /// <c>NullReferenceException</c> 的那条路径变成了回退到令牌。）
    /// </summary>
    public IBrush UplinkBrush
    {
        get => ResolveBrush(GetValue(UplinkBrushProperty), UplinkBrushToken, DefaultUplinkBrush);
        set => SetValue(UplinkBrushProperty, value);
    }

    /// <summary>下行曲线的颜色，默认对应令牌 <c>Brush.SuccessText</c>（WPF 端是 <c>Brushes.SeaGreen</c>）。</summary>
    public IBrush DownlinkBrush
    {
        get => ResolveBrush(GetValue(DownlinkBrushProperty), DownlinkBrushToken, DefaultDownlinkBrush);
        set => SetValue(DownlinkBrushProperty, value);
    }

    /// <summary>中轴基线的颜色，默认对应令牌 <c>Brush.Border</c>（WPF 端是 <c>Brushes.Gainsboro</c>）。</summary>
    public IBrush BaselineBrush
    {
        get => ResolveBrush(GetValue(BaselineBrushProperty), BaselineBrushToken, DefaultBaselineBrush);
        set => SetValue(BaselineBrushProperty, value);
    }

    /// <summary>基线粗细，令牌 <c>Size.Hairline</c>。WPF 端写死 1；改成读令牌是因为 dash 长度
    /// 以笔宽为单位，将来令牌动了两边一起动。</summary>
    private double HairlineThickness
    {
        get
        {
            if (this.TryFindResource(HairlineToken, out object? found)
                && found is double thickness
                && thickness > 0)
            {
                return thickness;
            }

            return DefaultHairlineThickness;
        }
    }

    public TrafficSparkline()
    {
        // 换肤换的是资源字典（AvaloniaThemeService 只替换 MergedDictionaries[0]）。
        // 属性有人设时，DynamicResource 会自己改属性、走下面那条 InvalidateVisual；
        // 但没人设时笔刷是在 Render 里现取的（见 ResolveBrush），资源换了得有人喊一声，
        // 否则要等下一次样本变化才重画——断开时那一刻可能永远不来。
        // 这是 Avalonia 独有的问题：WPF 端全是 StaticResource，换肤后本来就整页重建。
        ResourcesChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>
    /// WPF 的 <c>FrameworkPropertyMetadataOptions.AffectsRender</c>：这五个属性一变就重画。
    /// 笔刷按引用比较（见 <see cref="Ensure"/>），所以换肤换了笔刷实例时，下一次 Render 会自动重建画笔。
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SamplesProperty
            || change.Property == CapacityProperty
            || change.Property == UplinkBrushProperty
            || change.Property == DownlinkBrushProperty
            || change.Property == BaselineBrushProperty)
        {
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        // WPF 的 ActualWidth/ActualHeight → Avalonia 的 Bounds（都是布局后的 DIP 尺寸，
        // 绘制原点固定在控件左上角）。没有固有尺寸：高度由视图给 Size.TrafficChart。
        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        // 「+ 0.5」在两边含义相同：WPF 与 Avalonia/Skia 都把设备像素的边界放在整数坐标、
        // 中心放在半整数坐标，1px 的水平线落在半整数上才只覆盖一行像素。midline 取整之后
        // 加 0.5，于是这条虚线在两个平台上都是清清楚楚的一行，而不是糊成两行。
        // WPF 的 SnapsToDevicePixels / GuidelineSet 在 Avalonia 里没有对应 API，这里也不依赖它们。
        double midline = Math.Round(height / 2);
        Pen baselinePen = EnsureBaselinePen();

        IReadOnlyList<TrafficRate> samples = Samples ?? Array.Empty<TrafficRate>();
        if (samples.Count < 2)
        {
            // 空数据与单点数据都只画基线：一个点画不出趋势，画成一根竖柱反而会被读成
            // 「刚刚有一瞬间的流量」。断开时控制器已 Reset，图上就只剩这条基线。
            context.DrawLine(baselinePen, new Point(0, midline + 0.5), new Point(width, midline + 0.5));
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

        DrawBand(context, samples, static s => s.UplinkBytesPerSecond,
            originX, step, midline - BaselineGap, -halfHeight, upScale * PeakHeadroom,
            Ensure(ref _uplinkBrushes, UplinkBrush));
        DrawBand(context, samples, static s => s.DownlinkBytesPerSecond,
            originX, step, midline + BaselineGap, halfHeight, downScale * PeakHeadroom,
            Ensure(ref _downlinkBrushes, DownlinkBrush));

        // 基线最后画：两片半透明面积都压在中轴上，先画会被盖住。
        context.DrawLine(baselinePen, new Point(0, midline + 0.5), new Point(width, midline + 0.5));
    }

    /// <param name="baseY">该侧曲线的零点。</param>
    /// <param name="span">从零点到图形边缘的有向距离；上行为负（向上），下行为正。</param>
    private static void DrawBand(
        DrawingContext context,
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
        using (StreamGeometryContext path = geometry.Open())
        {
            path.BeginFigure(new Point(originX, baseY), isFilled: true);

            for (int i = 0; i < samples.Count; i++)
            {
                double value = Math.Max(selector(samples[i]), 0);
                double y = baseY + (span * Math.Min(value / scale, 1));
                // 只描实际速率曲线；面积的竖边与基线不应被画成额外的数据线。
                // WPF 的 LineTo(point, isStroked: i > 0, isSmoothJoin: true) → 这里的同名重载；
                // Skia 后端把它拆成两条 SKPath（填充一条、描边一条），false 的那一段只进填充。
                path.LineTo(new Point(originX + (i * step), y), isStroked: i > 0);
            }

            // 收回基线闭合成面积。WPF 的 BeginFigure(..., isClosed: true) 在这里表达成 EndFigure(true)。
            path.LineTo(new Point(originX + ((samples.Count - 1) * step), baseY), isStroked: false);
            path.LineTo(new Point(originX, baseY), isStroked: false);
            path.EndFigure(isClosed: true);
        }

        // WPF 是 DrawGeometry(fill, pen, geometry)，其中 fill 是 stroke 的 Clone 且 Opacity = 0.22。
        // Avalonia 没有 Freezable，也没有「复制任意笔刷再改透明度」的通用做法，于是改用
        // PushOpacity 把这一次填充压到 0.22：Skia 后端在非 saveLayer 路径下把 _currentOpacity
        // 直接乘进颜色的 alpha（DrawingContextImpl.CreatePaint），与 WPF 的 brush.Opacity 是同一条
        // 算式（(byte)(A * opacity)），对纯色笔刷逐位相同；描边在 push 之外，不受影响。
        // 顺带一个好处：渐变/Halo 这类皮肤笔刷也照样被压暗，不需要按笔刷类型分支。
        using (context.PushOpacity(BandFillOpacity))
        {
            context.DrawGeometry(brushes.Source, null, geometry);
        }

        context.DrawGeometry(null, brushes.Pen, geometry);
    }

    /// <summary>
    /// 一条带的绘制资源。每秒重绘一次、每次两条带，照 Clone + Freeze 就是每分钟
    /// 240 个短命的笔刷与画笔；而描边色几乎从不变，缓存下来即可。
    ///
    /// 缓存放实例上而不是静态字典：静态字典要跨线程用就得加锁，而每个
    /// <see cref="TrafficSparkline"/> 只被它自己的 UI 线程渲染，实例字段天然没有这个问题。
    ///
    /// 与 Windows 端的差异：Avalonia 的 <see cref="Pen"/> 不是 Freezable（没有 Freeze/Clone），
    /// 缓存的就是 Pen 实例本身；判定失效的规则一样——源笔刷的引用变了就重建。
    /// 面积填充不需要额外的笔刷对象（见 <see cref="DrawBand"/> 的 PushOpacity）。
    /// </summary>
    private readonly record struct BandBrushes(IBrush Source, Pen Pen)
    {
        internal static BandBrushes For(IBrush stroke)
        {
            var pen = new Pen(stroke, BandStrokeThickness)
            {
                // WPF 的 Pen 默认就是 Flat / Miter / MiterLimit 10，Avalonia 的默认值相同；
                // 只有 LineJoin 必须显式改：WPF 端每个数据点都标了 isSmoothJoin: true
                // （平滑连接，尖角处不拉尖刺），Avalonia 没有逐点开关，只能整条笔选一种。
                // Round 是三者里最接近的：Miter 会在尖峰处拉出最长 10×笔宽（MiterLimit）的尖刺，
                // 而 Bevel 比 WPF 的平滑连接削得更狠。
                LineCap = PenLineCap.Flat,
                LineJoin = PenLineJoin.Round,
            };

            return new BandBrushes(stroke, pen);
        }
    }

    private BandBrushes _uplinkBrushes;
    private BandBrushes _downlinkBrushes;
    private Pen? _baselinePen;
    private IBrush? _baselinePenSource;
    private double _baselinePenThickness;

    private static BandBrushes Ensure(ref BandBrushes cache, IBrush stroke)
    {
        if (!ReferenceEquals(cache.Source, stroke))
        {
            cache = BandBrushes.For(stroke);
        }

        return cache;
    }

    private Pen EnsureBaselinePen()
    {
        IBrush stroke = BaselineBrush;
        double thickness = HairlineThickness;
        if (_baselinePen is not null
            && ReferenceEquals(_baselinePenSource, stroke)
            && _baselinePenThickness == thickness)
        {
            return _baselinePen;
        }

        var pen = new Pen(stroke, thickness)
        {
            // DashStyle.Dash 与 WPF 的 DashStyles.Dash 逐值相同：dashes {2,2}、offset 1
            // （WPF 用的是 offset 1 而不是 0，别顺手改成 {2,2} 加 offset 0）。
            // 两边的 dash 长度与相位都以笔宽为单位（Avalonia 在
            // DrawingContextHelper.TryCreateDashEffect 里乘以 pen.Thickness），
            // Hairline = 1 时就是 2px 实 / 2px 空。
            DashStyle = DashStyle.Dash,
            LineCap = PenLineCap.Flat,
        };

        _baselinePen = pen;
        _baselinePenSource = stroke;
        _baselinePenThickness = thickness;
        return pen;
    }

    /// <summary>
    /// 属性值 →（没人设时）资源树里的同名令牌 →（资源字典还没到位时）与令牌同值的常量。
    /// 每次 Render 现取一遍：换肤只换资源字典，缓存住旧笔刷就会让控件停在上一个皮肤的颜色上。
    /// </summary>
    private IBrush ResolveBrush(IBrush? value, string tokenKey, IBrush fallback)
    {
        if (value is not null)
        {
            return value;
        }

        return this.TryFindResource(tokenKey, out object? found) && found is IBrush brush
            ? brush
            : fallback;
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
