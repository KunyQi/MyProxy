using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MyProxy.Core;
using MyProxy.Gui.ViewModels;

namespace MyProxy.Gui.Views;

/// <summary>
/// 主界面的代码后置。整个文件只有一件事：把 Windows 端由
/// <c>Binding.TargetUpdated</c> 驱动的「状态淡入」改成 Avalonia 的等价物。
///
/// <para>
/// Windows 端（<c>Views/MainView.xaml.cs</c>）的形状是：状态区挂
/// <c>TargetUpdated="OnStatusTargetUpdated"</c>，回调里
/// <c>BeginAnimation(OpacityProperty, new DoubleAnimation(0 → 1, Motion.Duration.Base){ FillBehavior = Stop })</c>。
/// Avalonia 的绑定没有「目标已更新」这个事件（<c>TargetUpdated</c> 不存在），
/// 所以这里换成**属性变化驱动**：订阅 ViewModel 的
/// <see cref="INotifyPropertyChanged"/>，<c>StatusText</c> /
/// <c>StatusSubText</c> 一变就播一次 <see cref="Animation"/>——0% 处 Opacity 0、100% 处 1，
/// 时长同样取令牌 <c>Motion.Duration.Base</c>，<see cref="FillMode.None"/> 对应 WPF 的
/// <c>FillBehavior.Stop</c>（播完回到基值 1）。
/// </para>
///
/// <para>
/// <b>为什么不用 Opacity 的 Transitions 再「归零→置一」？</b>过渡的起点是变更那一刻属性的**当前值**
/// （Avalonia 的 <c>Transition&lt;T&gt;.Apply</c> 拿 <c>oldValue</c>/<c>newValue</c> 做插值）。
/// 归零与置一若落在同一动画帧里，第二次变更的起点已经是「正在淡出中的 ~1」，
/// 于是过渡退化成 1 → 1，屏幕上什么都不会发生。显式写死两个关键帧没有这个歧义，
/// 也就更贴近 Windows 端那句 <c>DoubleAnimation(0, 1, duration)</c> 的字面含义。
/// </para>
///
/// <para>
/// <b>约束：状态文字是淡入，不是位移。</b>槽高由
/// <c>ReservedStatusLineStyle</c> / <c>ReservedLineStyle</c> 固定，这里只碰
/// <see cref="Visual.Opacity"/>——不碰 Margin、不碰 RenderTransform、不改布局，
/// 于是状态区在整页里一个像素都不会动。
/// </para>
/// </summary>
public partial class MainView : UserControl
{
    /// <summary>淡入时长令牌的 key。与 Windows 端 <c>FindResource("Motion.Duration.Base")</c> 同名。</summary>
    private const string StatusFadeDurationKey = "Motion.Duration.Base";

    /// <summary>触发淡入的两个属性名。与 ViewModel 的属性一一对应。</summary>
    private static readonly string[] StatusProperties = ["StatusText", "StatusSubText"];

    /// <summary>
    /// 能量球的状态类名，与 <c>OrbStyles.axaml</c> 的选择器逐字对应。
    /// 全部加在同一个 Button 上，加/去由 <see cref="SyncOrbClasses"/> 统一负责。
    /// </summary>
    private static readonly string[] OrbClasses =
        ["idle", "connected", "connecting", "disconnecting", "error", "checkbad", "still"];

    private INotifyPropertyChanged? _observed;
    private CancellationTokenSource? _fadeCts;

    public MainView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // 模式开关那两格的模板要在托盘模板展开之后才补得上（原因见 ModeSwitchFixup）。
        // 挂在 AttachedToVisualTree 上：那时 ContentControl 的模板已经 ApplyTemplate 过一轮。
        AttachedToVisualTree += (_, _) => ModeSwitchFixup.Apply(ModeSwitch);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_observed is not null)
        {
            _observed.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _observed = DataContext as INotifyPropertyChanged;
        if (_observed is not null)
        {
            _observed.PropertyChanged += OnViewModelPropertyChanged;
        }

        SyncOrbClasses();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 属性名为空（null 或 ""）按「全都变了」处理——那是 INotifyPropertyChanged 的约定。
        string? name = e.PropertyName is { Length: > 0 } propertyName ? propertyName : null;

        // 状态类要跟着 State / CheckSeverity / CanAnimate 走，而这三支都由 ViewModel 报变化。
        // 这里不挑属性名：SyncOrbClasses 内部用 Classes.Set 做值比对，没变时不动样式表，
        // 每秒一次的刷新不会让呼吸动画重启。
        SyncOrbClasses();

        if (name is not null && Array.IndexOf(StatusProperties, name) < 0)
        {
            return;
        }

        _ = PlayStatusFadeAsync();
    }

    /// <summary>
    /// 把「连接状态 + 自检结论 + 动效开关」翻译成能量球上的类名。
    ///
    /// <para>
    /// Windows 端这套映射写在 <c>OrbButtonStyle</c> 的 <c>ControlTemplate.Triggers</c> 里
    /// （<c>DataTrigger Binding="{Binding State}"</c> 等）；Avalonia 的控件模板没有
    /// 数据触发器，只有类/伪类选择器，所以改成「View 负责加/去类名」——这也是
    /// <c>OrbStyles.axaml</c> 里那份状态契约要求的方向。
    /// </para>
    ///
    /// <para>
    /// 两条映射规则要照着 Windows 端抄，不能自己发挥：
    /// <list type="bullet">
    ///   <item><description><c>checkbad</c> **必须与 connected 同时挂**：它表达的是
    ///     「连着，但自检说流量没走隧道」，单独挂没有意义（那边是 MultiDataTrigger 的两个条件）。</description></item>
    ///   <item><description><c>still</c> 是 <c>!CanAnimate</c>（不可见、最小化、或在退出），
    ///     它只停动画，**不改变任何状态色**。</description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// 用 <see cref="Classes.Set(string, bool)"/> 而不是先清后加：Set 自己会比对当前值，
    /// 值没变时不动样式表——状态每秒都被刷新，先清后加会让呼吸动画每秒重启一次。
    /// </para>
    /// </summary>
    private void SyncOrbClasses()
    {
        if (OrbButton is null)
        {
            return;
        }

        MainViewModel? model = DataContext as MainViewModel;
        if (model is null)
        {
            return;
        }

        Classes classes = OrbButton.Classes;
        foreach (string name in OrbClasses)
        {
            classes.Set(name, false);
        }

        // 未绑定也走 idle：Windows 端 Unbound 没有单独的状态色，球就是静止的灰白。
        // 「守护进程不在」在 Linux 上是 Connecting 的**最近似**（都不是已连接、都在等），
        // 但它是持续态而不是过渡态，所以不加动画类——`still` 会把它按住。
        string stateClass = model.State switch
        {
            AppState.Connected => "connected",
            AppState.Connecting => "connecting",
            AppState.Disconnecting => "disconnecting",
            AppState.Error => "error",
            _ => "idle",
        };
        classes.Set(stateClass, true);
        classes.Set("checkbad", stateClass == "connected" && model.CheckSeverity == CheckSeverity.Bad);
        classes.Set("still", !model.CanAnimate || model.IsDaemonUnreachable);
    }

    /// <summary>
    /// 播一次淡入。对应 Windows 端回调的两步：先 <c>BeginAnimation(OpacityProperty, null)</c>
    /// 清掉上一次动画（这里取消上一次的 <see cref="CancellationTokenSource"/>），再决定要不要播。
    /// </summary>
    private async Task PlayStatusFadeAsync()
    {
        _fadeCts?.Cancel();
        _fadeCts?.Dispose();
        _fadeCts = null;

        if (!IsVisible || !ShouldAnimate || StatusFadeDuration is not { Ticks: > 0 } duration)
        {
            // 不播时也要把 Opacity 留在基值上：Windows 端清掉动画之后同样是基值 1。
            StatusBand.Opacity = 1;
            return;
        }

        var fade = new Animation
        {
            Duration = duration,
            FillMode = FillMode.None,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.OpacityProperty, 0d) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, 1d) } },
            },
        };

        var cts = new CancellationTokenSource();
        _fadeCts = cts;
        try
        {
            await fade.RunAsync(StatusBand, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 上一次还没播完就被新的状态更新顶掉了：正常路径，不是错误。
        }
        catch (Exception ex)
        {
            // 动画播不出来绝不能影响连接状态本身的显示：记一条，界面照旧。
            Trace.WriteLine($"状态淡入失败：{ex}");
        }
        finally
        {
            if (ReferenceEquals(_fadeCts, cts))
            {
                _fadeCts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// 是否允许动效。判据与 Windows 端逐字相同：<c>DataContext is not MainViewModel { CanAnimate: true }</c>
    /// 就不播（<c>CanAnimate = IsLive &amp;&amp; AnimationsEnabled</c>，后者在 Linux 上由
    /// <c>MainWindow</c> 读环境变量决定——Avalonia 11.3.2 没有 WPF 那种系统动效开关）。
    /// </summary>
    private bool ShouldAnimate => DataContext is MainViewModel { CanAnimate: true };

    /// <summary>
    /// 淡入时长。<c>Motion.Duration.Base</c> 由皮肤字典提供（三套皮肤各自一份）。
    /// 取不到就不播，而不是退回一个写死的毫秒数：编一个默认值只会让「缺令牌」这种集成错误
    /// 在界面上看不出来。（MainWindow 的切页淡入对同一个 key 用了 240ms 兜底；
    /// 那里是切页，宁可有动效也不要生硬跳变，取舍不同。）
    /// </summary>
    private TimeSpan? StatusFadeDuration
        => this.TryFindResource(StatusFadeDurationKey, out object? value) && value is TimeSpan duration
            ? duration
            : null;
}

/// <summary>
/// 模式开关那两格（智能分流 / 全局代理）的模板补丁。
///
/// <para>
/// <b>为什么需要它。</b>两格是 <c>RadioButton</c>（派生自 <c>ContentControl</c>），
/// 而托盘是 <c>ContentControl</c> + <c>Theme="{DynamicResource ModeSwitchStyle}"</c>。
/// 这个位置上 Avalonia 11.3 不会把 <c>ModeSwitchItemStyle</c> 的模板交给它们。
/// 试过并实测失败的四种写法（每一种都在 Debian/WSLg 里跑起来量过视觉树）：
/// </para>
/// <list type="number">
///   <item><description>格子上写 <c>Theme="{StaticResource ModeSwitchItemStyle}"</c>——
///     运行期 <c>radio.Theme</c> 报的确实是本主题、<c>TargetType</c> 也对，但模板仍来自父级。</description></item>
///   <item><description>实例上内联一个同内容的 <c>ControlTheme</c>：同样被顶掉。</description></item>
///   <item><description>内联 <c>&lt;ControlTheme BasedOn="{StaticResource ModeSwitchItemStyle}"&gt;</c>：
///     <c>Setters.Count</c> 变 0，模板还是父级的。</description></item>
///   <item><description>改写成应用级 <c>&lt;Style Selector="RadioButton"&gt;</c>：编译期即失败，
///     <c>AVLN3000: Unable to find suitable setter or adder for property Content of type
///     Avalonia.Base:Avalonia.Controls.ResourceDictionary</c>。</description></item>
/// </list>
/// <para>
/// 错的视觉树长这样：两格各自套了一整层 352×44 的轨道与滑块，第二格被推到 x=88，
/// 被窗口右缘切掉——截图上看起来就是「智能分流 / 全局代理 各被裁掉一半」。
/// </para>
///
/// <para>
/// <b>补法。</b>托盘模板展开后，给这两格显式赋 <c>Theme</c> 与 <c>Template</c>，再
/// <c>ApplyTemplate()</c> 一次。<c>Template</c> 一旦是直接赋的值，主题系统便没有插手的
/// 余地——不依赖解析顺序，也不依赖父级是什么。<c>Theme</c> 同时给上，是为了让
/// <c>:checked</c> / <c>:pressed</c> / <c>:disabled</c> 那几条子样式继续生效。
/// </para>
///
/// <para>
/// <b>只认「直接挂在托盘模板里的那两格」</b>：视觉父链上除自己以外必须全是模板骨架
/// （Grid / Border / ContentPresenter）且终点是本控件。这样即便将来格样式自己又被谁套了
/// 一层，把孙子辈也渲染出来，也不会被误认成格子。
/// </para>
///
/// <para>
/// 分段控件（设置页的外观三档）不需要这套补丁：那三格是<b>视图直接创建</b>的控件，
/// 显式给 <c>Theme</c> 就能拿到模板。
/// </para>
/// </summary>
internal static class ModeSwitchFixup
{
    /// <summary>格样式 key。三套皮肤共用同一份（取值差异全在令牌里）。</summary>
    private const string ItemStyleKey = "ModeSwitchItemStyle";

    private static readonly HashSet<ContentControl> Applied = [];

    /// <summary>
    /// 幂等：同一个托盘只接一次 <c>TemplateApplied</c>。
    /// </summary>
    public static void Apply(ContentControl? modeSwitch)
    {
        if (modeSwitch is null || !Applied.Add(modeSwitch))
        {
            return;
        }

        // 挂在 TemplateApplied 上而不是只做一次：托盘自己的模板被重建时（换肤、重建页面、
        // 主题变更）两格会跟着被重建，那一刻必须再补一次——只做一次的话，第一次重建就把
        // 两格打回「继承父级模板」的状态。
        modeSwitch.TemplateApplied += (_, _) => Fix(modeSwitch);
        Fix(modeSwitch);
    }

    private static void Fix(ContentControl modeSwitch)
    {
        if (!modeSwitch.TryFindResource(ItemStyleKey, out object? resource) || resource is not ControlTheme itemTheme)
        {
            Trace.WriteLine($"模式开关：找不到 {ItemStyleKey}，两格保持继承的模板。");
            return;
        }

        var template = itemTheme.Setters
            .OfType<Setter>()
            .FirstOrDefault(setter => setter.Property == TemplatedControl.TemplateProperty)?
            .Value as IControlTemplate;

        if (template is null)
        {
            Trace.WriteLine($"模式开关：{ItemStyleKey} 没有 Template setter，两格保持继承的模板。");
            return;
        }

        foreach (Visual visual in modeSwitch.GetVisualDescendants())
        {
            if (visual is not RadioButton radio || !IsTemplateSegment(modeSwitch, radio))
            {
                continue;
            }

            // 已经是这一套就直接返回：给 Template 赋同一个引用会再触发一次
            // TemplateApplied，不挡住就是死循环。
            if (ReferenceEquals(radio.Template, template))
            {
                continue;
            }

            radio.Theme = itemTheme;
            radio.Template = template;
        }
    }

    /// <summary>
    /// 只认「直接挂在托盘模板里的那两格」：视觉父链上除自己以外必须全是模板骨架
    /// （Grid / Border / ContentPresenter）且终点是本控件。这样即便将来格样式自己又被谁
    /// 套了一层、孙子辈也渲染出来，也不会被误认成格子。
    /// </summary>
    private static bool IsTemplateSegment(ContentControl modeSwitch, RadioButton radio)
    {
        Visual? current = radio.GetVisualParent();

        while (current is not null)
        {
            if (ReferenceEquals(current, modeSwitch))
            {
                return true;
            }

            if (current is not (Grid or Border or ContentPresenter))
            {
                return false;
            }

            current = current.GetVisualParent();
        }

        return false;
    }
}
