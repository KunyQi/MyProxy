using System.Diagnostics;
using MyProxy.Core;
using MyProxy.Models;
using MyProxy.Services;

// 命名空间与 gui/MainWindow.axaml.cs 的 using 一致（MyProxy.Gui.ViewModels），
// 而不是 Windows 端的 MyProxy.ViewModels：这个工程的 RootNamespace 是 MyProxy.Gui。
namespace MyProxy.Gui.ViewModels;

/// <summary>
/// 设置页。视图（<c>Views/SettingsView.axaml</c>）是从 Windows 端逐字搬过来的，
/// 绑定名一个都没改，所以这里的成员名与 Windows 端
/// <c>MyProxy.ViewModels.SettingsViewModel</c> 一一对应。
///
/// <para>
/// <b>与 Windows 端唯一的差别是数据从哪来。</b>Windows 端 GUI 与连接控制器同进程，
/// 设置直接由 <c>IStorageService</c> 读写、皮肤由 <c>IThemeService</c> 施加；
/// Linux 端连接的唯一持有者是守护进程（<c>myproxy run</c>），GUI 只是
/// <c>runtime/control.sock</c> 上的一个客户端。所以：
/// </para>
/// <list type="bullet">
/// <item>读：<c>StatusSnapshot</c>（<c>version</c> / <c>deviceName</c> / <c>boundAt</c> /
/// <c>autoStart</c> / <c>autoConnect</c> / <c>theme</c> / <c>update*</c>）。</item>
/// <item>写：<c>ControlCommands.AutoStart</c> / <c>AutoConnect</c> / <c>Theme</c> /
/// <c>Unbind</c> / <c>UpdateCheck</c>。命令失败时把守护进程回来的
/// <c>message</c> 显示出来，并把开关<b>拨回</b>守护进程报告的现值。</item>
/// </list>
///
/// <para>
/// <b>本类绝不自己动手</b>：不起内核、不改系统代理、不写设备凭据、不直接落盘设置。
/// 那些都是守护进程的事——两个进程各改一半，正是这条控制通道存在的理由。
/// </para>
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    /// <summary>
    /// 更新检查的节流间隔，与守护进程 <c>LinuxDaemon.UpdateLoopAsync</c> 里的值一致。
    /// 用途见 <see cref="UpdateHasBeenChecked"/>。
    /// </summary>
    private static readonly TimeSpan DaemonUpdateInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// 「重新绑定成功了、但守护进程还没报未绑定」这段空窗期的长度。
    /// 超过它就以守护进程的快照为准（见 <see cref="ApplySnapshot"/>）。
    /// </summary>
    private static readonly TimeSpan UnboundPulseDuration = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 写完开关之后，界面还认「用户拨的那一档」多久。它只用来挡住「命令响应里的快照
    /// 是生效之前取的」这一种时序，所以很短：再长就变成对用户撒谎了。
    /// </summary>
    private static readonly TimeSpan PendingToggleFreshness = TimeSpan.FromSeconds(5);

    private readonly ControlClient _client;

    /// <summary>设置页选中的皮肤。仅供分段控件的高亮，真正生效的皮肤在外壳那边（见 <see cref="ThemeApplyRequested"/>）。</summary>
    private UiTheme _uiTheme = UiTheme.Classic;

    /// <summary>最近一次成功取回的守护进程快照。<b>这一页每一项的数据源都是它。</b></summary>
    private StatusSnapshot? _snapshot;

    private bool _autoStart;
    private bool _autoConnect;
    private bool _autoUpdateCheck;

    private UpdateInfo? _updateInfo;
    private string _errorMessage = "";

    /// <summary>皮肤请求正在途中的标记：见 <see cref="ApplySnapshot"/>，它让轮询不要抢在命令回应之前把选中态改掉。</summary>
    private int _themeRequestInFlight;

    /// <summary>
    /// 「用户刚拨了自动启动 / 自动连接」的目标档位，<c>null</c> 表示没有在途的写回。
    /// 写回期间（以及命令回应刚落地的一小段时间内）快照不许覆盖它：命令响应里的快照可能是
    /// 命令生效**之前**取的，跟着改会让开关自己弹回去，而用户看不见任何失败。
    /// 超时之后一律交还给守护进程，界面宁可短暂落后，也不能一直显示一个没写进去的值。
    /// </summary>
    private bool? _pendingAutoStart;
    private bool? _pendingAutoConnect;
    private DateTimeOffset _pendingToggleUntil;

    /// <summary>「重新绑定」刚成功的那一刻，见 <see cref="ApplySnapshot"/>。到期即失效，不会一直骗人。</summary>
    private DateTimeOffset? _unboundLocallyUntil;

    /// <summary>
    /// 待完成的写回链。与 Windows 端的「待落盘设置」不是一回事（见 <see cref="FlushAsync"/>）：
    /// 这里等的是**还在途中的控制命令**，用来保证离场顺序是「命令走完 → 再收尾」。
    /// </summary>
    private readonly object _pendingWriteSync = new();
    private Task _pendingWrite = Task.CompletedTask;

    public SettingsViewModel(ControlClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));

        CloseCommand = new RelayCommand(() => SettingsClosed?.Invoke());
        RebindCommand = new RelayCommand(RebindAsync, () => true);
        OpenLogCommand = new RelayCommand(OpenLog);
        UpdateCommand = new RelayCommand(RequestUpdate, () => _updateInfo is not null);

        RefreshDevice();
    }

    /// <summary>
    /// 用户按了「返回」（或 Esc）。Windows 端由 <c>MainWindow</c> 订阅它切回主页；
    /// <b>「重新绑定」成功走的也是这条</b>——解绑之后留在设置页没有意义，
    /// 那一页显示的设备已经不存在了。
    /// </summary>
    public event Action? SettingsClosed;

    /// <summary>
    /// 需要把皮肤换成 <paramref name="theme"/>。<b>可选</b>：外壳已经在把
    /// <c>MainViewModel.Theme</c>（也就是守护进程快照里的 <c>theme</c>）同步给
    /// <c>AvaloniaThemeService</c>，那一条路才是权威——皮肤换了之后由守护进程说了算，
    /// 轮询一到界面就跟着变。这里的事件只用于两种「命令成功但快照还没回来」的时刻：
    /// 换肤成功后立刻生效，以及换肤失败后立刻退回原来那一套。
    ///
    /// <para>
    /// 订阅者要做的是「幂等地把皮肤换成这一档」，不要在这里改任何 view model 状态，
    /// 也不要在收到命令成功之前抢着换——那会让界面显示一个守护进程并没有接受的皮肤。
    /// </para>
    /// </summary>
    public event Action<UiTheme>? ThemeApplyRequested;

    /// <summary>
    /// 打开更新下载页。与换肤同理：URL 由守护进程给出，打开浏览器是界面层的事，
    /// 而这个 ViewModel 不引入平台 I/O。
    /// </summary>
    public event Action<Uri>? UpdatePageRequested;

    /// <summary>版本号。与 Windows 端一样取 <c>AppInfo.Version</c>，而不是快照里的值。</summary>
    public string Version => AppInfo.Version;

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(ErrorVisibility));
            }
        }
    }

    /// <summary>常驻错误槽是否显示。Avalonia 用布尔 <c>IsVisible</c>，不是 WPF 的 <c>Visibility</c>。</summary>
    public bool ErrorVisibility => !string.IsNullOrWhiteSpace(ErrorMessage);

    /// <summary>
    /// 界面皮肤。分段控件的三个单选按钮各绑一个 bool，
    /// 与主页「智能分流 / 全局代理」同一种写法，不为一个枚举引入转换器。
    /// </summary>
    public UiTheme UiTheme
    {
        get => _uiTheme;
        set
        {
            UiTheme target = UiThemes.Normalize(value);
            if (_uiTheme == target)
            {
                return;
            }

            if (target == CurrentThemeFromDaemon)
            {
                // 用户点回了守护进程本来就有的那一档（换皮肤命令失败之后会走到这里）。
                // 不需要再发一条命令，只需要把高亮挪回去。
                _uiTheme = target;
                NotifyThemeSelection();
                return;
            }

            RequestTheme(target);
        }
    }

    public bool IsThemeClassic
    {
        get => _uiTheme == UiTheme.Classic;
        set { if (value) { UiTheme = UiTheme.Classic; } }
    }

    public bool IsThemePorcelain
    {
        get => _uiTheme == UiTheme.Porcelain;
        set { if (value) { UiTheme = UiTheme.Porcelain; } }
    }

    public bool IsThemeCeramic
    {
        get => _uiTheme == UiTheme.Ceramic;
        set { if (value) { UiTheme = UiTheme.Ceramic; } }
    }

    private void NotifyThemeSelection()
    {
        OnPropertyChanged(nameof(UiTheme));
        OnPropertyChanged(nameof(IsThemeClassic));
        OnPropertyChanged(nameof(IsThemePorcelain));
        OnPropertyChanged(nameof(IsThemeCeramic));
    }

    /// <summary>
    /// 「自动启动 MyProxy」。视图绑的是 <c>IsChecked</c>（双向），所以写回必须是命令，
    /// 并且失败后要把开关拨回守护进程报告的现值——绝不允许留下
    /// 「界面说开了、实际没开」的状态。
    /// </summary>
    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            // 先比较再动（SetProperty 的语义），否则轮询每秒把同一个值推进来都会白发一条命令。
            if (_autoStart == value)
            {
                return;
            }

            _autoStart = value;
            OnPropertyChanged(nameof(AutoStart));
            OnPropertyChanged(nameof(AutoStartText));

            Track(SetToggleAsync(ControlCommands.AutoStart, value, "开机自启", isAutoStart: true));
        }
    }

    /// <summary>「启动后自动连接」。形状与 <see cref="AutoStart"/> 完全一致，只是守护进程那边没有额外副作用。</summary>
    public bool AutoConnect
    {
        get => _autoConnect;
        set
        {
            if (_autoConnect == value)
            {
                return;
            }

            _autoConnect = value;
            OnPropertyChanged(nameof(AutoConnect));
            OnPropertyChanged(nameof(AutoConnectText));

            Track(SetToggleAsync(ControlCommands.AutoConnect, value, "自动连接", isAutoStart: false));
        }
    }

    /// <summary>
    /// 「自动检查更新」。
    ///
    /// <para>
    /// <b>Linux 端与 Windows 端有真实差异，而且这一页如实说了出来。</b>守护进程的设置里
    /// 确实有这个字段（<c>LinuxDaemon.UpdateLoopAsync</c> 读它决定要不要跑定期检查），
    /// 但 <c>StatusSnapshot</c> 既没有回读字段、<c>ControlCommands</c> 里也没有写回命令，
    /// 所以这一页读不到、也写不进它。于是这里<b>先做成本地视图状态</b>：
    /// 打开时顺手发一条真的 <c>update-check</c>（那一条命令确实会跑），同时用
    /// <see cref="ErrorMessage"/> 明说「只在这个界面里生效」。<b>不假装它已生效。</b>
    /// 协议补上之后，这里应该改回与 <see cref="AutoStart"/> 完全相同的形状。
    /// </para>
    /// </summary>
    public bool AutoUpdateCheck
    {
        get => _autoUpdateCheck;
        set
        {
            if (_autoUpdateCheck == value)
            {
                return;
            }

            _autoUpdateCheck = value;
            OnPropertyChanged(nameof(AutoUpdateCheck));

            // 如实报错：说清楚这次改动落在了哪里、没有落在哪里。
            ErrorMessage = "后台服务暂不支持保存「自动检查更新」（缺 autoupdatecheck 命令），这项设置只在当前界面生效。";

            if (value)
            {
                // 打开时立刻查一次：这是本页唯一一条真的能生效的动作。
                Track(RequestUpdateCheckAsync());
            }
        }
    }

    /// <summary>开关状态的可读文本（托盘提示与无障碍用，与主页同源）。</summary>
    public string AutoStartText => _autoStart ? "开" : "关";

    public string AutoConnectText => _autoConnect ? "开" : "关";

    /// <summary>
    /// 重读设备身份。<b>每次进入设置页都必须调用</b>：本 ViewModel 在外壳构造时就建好、
    /// 之后一直复用，只在构造时读一次的话——
    ///   · 首次运行（启动时未绑定）→ 整个会话里设备组永远不出现，绑完也不出现；
    ///   · 重新绑定之后回到本页 → 显示的仍是上一次的设备名与绑定日期。
    ///
    /// <para>
    /// Linux 端的「重读」是<b>再问守护进程要一份快照</b>，而不是读本地文件：设备凭据只有
    /// 守护进程持有（GUI 连它的路径都不该知道）。这里先把已经拿到的那份快照重新铺一遍
    /// （立刻可见），再异步去要新的一份。
    /// </para>
    /// </summary>
    public void RefreshDevice()
    {
        NotifyDevice();
        Track(RefreshStatusAsync());
    }

    private void NotifyDevice()
    {
        OnPropertyChanged(nameof(DeviceVisibility));
        OnPropertyChanged(nameof(DeviceName));
        OnPropertyChanged(nameof(BoundAtText));
    }

    /// <summary>
    /// 设备组是否显示。没有设备记录时整组隐藏，而不是显示一个编造的名字——
    /// 与 Windows 端「<c>_device?.DeviceName</c> 为空即隐藏」同一条规则。
    /// 「读不出来」在守护进程那边也会变成 <c>deviceName</c> 为空（它自己把
    /// <c>SecureStorageReadException</c> 记进日志并如实报成未绑定），所以这里不必再区分。
    /// </summary>
    public bool DeviceVisibility => !string.IsNullOrEmpty(_snapshot?.DeviceName);

    public string DeviceName => _snapshot?.DeviceName ?? "";

    /// <summary>与 Windows 端同一句话、同一个日期格式。</summary>
    public string BoundAtText => _snapshot?.BoundAt is DateTimeOffset boundAt && boundAt != default
        ? $"{boundAt.ToLocalTime():yyyy-MM-dd} 绑定"
        : "";

    /// <summary>
    /// 两种连接模式的名字与说明。常量直出，不带状态——开关在主页，这一页只负责解释差别。
    /// 全部取自 <see cref="ProxyModeText"/>，托盘与主页读的是同一份文案。
    /// </summary>
    public static string RuleModeName => ProxyModeText.RuleName;

    public static string RuleModeDescription => ProxyModeText.RuleDescription;

    public static string GlobalModeName => ProxyModeText.GlobalName;

    public static string GlobalModeDescription => ProxyModeText.GlobalDescription;

    /// <summary>
    /// 版本不占一行，它是「更新」这件事的当前状态。
    ///
    /// <para>
    /// <b>这里与 Windows 端有真实差异，必须说清楚。</b>Windows 端说「已是最新版本」的条件是
    /// 「本进程这次检查真的跑通了且没有新版本」（<c>_knownUpToDate</c>）。Linux 端的检查在
    /// 守护进程里，而 <c>StatusSnapshot</c> 没有「查过且没有新版本」这个字段——
    /// <c>updateMessage</c> 只在<b>发现新版本</b>或<b>出错</b>时才非空，
    /// 于是「查过、没有新版本」与「还没查过」在快照里长得一模一样。
    /// 一句话说不出来的东西不能编：这里退到保守的那一半，只报版本号，
    /// 再带上守护进程确实说过的那句话（如果有）。<b>要恢复 Windows 端的措辞，协议得补字段</b>，
    /// 见文件末尾。
    /// </para>
    /// </summary>
    public string VersionText
    {
        get
        {
            string version = $"当前 {Version}";

            string message = _snapshot?.UpdateMessage ?? "";
            if (!string.IsNullOrEmpty(message))
            {
                return $"{version}，{message}";
            }

            // 查过、没有新版本、而且守护进程还在同一个检查周期之内——只有这时候才敢下结论。
            // 这是推算（见 UpdateHasBeenChecked），协议补上「上次检查结论」之后整段可以删掉。
            return UpdateHasBeenChecked ? $"{version}，已是最新版本" : version;
        }
    }

    public string UpdateText => _updateInfo is null
        ? ""
        : _updateInfo.Mandatory
            ? $"请更新到 {_updateInfo.Version}"
            : $"发现新版本 {_updateInfo.Version}";

    public bool UpdateVisibility => _updateInfo is not null;

    public RelayCommand CloseCommand { get; }

    public RelayCommand RebindCommand { get; }

    public RelayCommand OpenLogCommand { get; }

    public RelayCommand UpdateCommand { get; }

    /// <summary>
    /// 等还在途中的写回命令落地。
    ///
    /// <para>
    /// <b>与 Windows 端语义不同，理由在这里。</b>Windows 端的设置是 GUI 自己持有的一个
    /// JSON 文件，改一下要异步落盘，于是设置页维护一条「待保存」链，离场前必须
    /// <c>FlushAsync</c> 才不会丢最后一次改动。Linux 端<b>没有本地设置文件</b>：
    /// 每一项写回都是一条发给守护进程的控制命令，守护进程当场写进它自己的设置
    /// （<c>LinuxStorageService.UpdateSettingsAsync</c>）。所以这里没有「还没落盘」的东西，
    /// 等的是同一件事的另一半——<b>还没回来的命令</b>：外壳收尾时若不等它们，
    /// 用户刚拨的那个开关可能连命令都没发出去就随进程一起消失了。
    /// </para>
    /// </summary>
    public async Task FlushAsync()
    {
        while (true)
        {
            Task pending;
            lock (_pendingWriteSync)
            {
                pending = _pendingWrite;
            }

            await pending.ConfigureAwait(true);

            lock (_pendingWriteSync)
            {
                if (ReferenceEquals(pending, _pendingWrite))
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// 把一份快照铺到界面上。外壳每次收到 <c>status</c> 回应都该调它——
    /// 设置页显示的是守护进程的现状，而不是本进程记下来的旧值。
    /// </summary>
    public void ApplySnapshot(StatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string previousVersionText = VersionText;

        // 「重新绑定」刚成功、守护进程还没报未绑定：先按未绑定显示，但只认一个短窗口
        // （见 _unboundLocallyUntil）。窗口一过就以守护进程为准——界面宁可短暂落后，
        // 也不能一直显示一个不存在的设备。
        _snapshot = IsUnboundPulseActive() ? WithoutDevice(snapshot) : snapshot;

        // 设备身份：与 Windows 端「每次进设置页重读一次」等价，快照每次刷新都要跟着动。
        NotifyDevice();

        // 开关：用户刚拨过的那一档在短窗口内不服从快照（见 _pendingAutoStart），
        // 其余一律以守护进程为准——这一页显示的是它的现状，不是本进程记下来的旧值。
        ApplyAutoStart(ResolveToggle(_pendingAutoStart, _snapshot.AutoStart));
        ApplyAutoConnect(ResolveToggle(_pendingAutoConnect, _snapshot.AutoConnect));

        // 皮肤：只跟守护进程走。Windows/Android 的规矩是「眼睛看到的那一套才该高亮」，
        // 而 Linux 上真正生效的皮肤也由守护进程持有（外壳把 MainViewModel.Theme 同步给
        // AvaloniaThemeService），所以高亮跟它，两边不可能说两套话。
        // 例外是命令还在途中时——那时候守护进程回报的还是旧值。
        if (Volatile.Read(ref _themeRequestInFlight) == 0)
        {
            ApplyThemeSelection(_snapshot.Theme);
        }

        // 更新：整组重算。快照是权威，本进程不缓存上一次的结论。
        UpdateInfoFromSnapshot(_snapshot, previousVersionText);
    }

    /// <summary>
    /// 外壳把守护进程当前真正生效的皮肤告诉这一页。启动时用得上：快照还没回来之前，
    /// 界面已经按某套皮肤画出来了，分段控件的高亮必须与之一致。
    /// </summary>
    public void ApplyThemeSelection(UiTheme theme)
    {
        UiTheme normalized = UiThemes.Normalize(theme);
        if (_uiTheme == normalized)
        {
            return;
        }

        _uiTheme = normalized;
        NotifyThemeSelection();
    }

    // ---- 写回 ---------------------------------------------------------------------

    /// <summary>
    /// 开关类命令的统一处理。与 Windows 端 <c>AutoStart</c> 的 setter 是同一个形状：
    /// 乐观置位（setter 里已经做了）→ 发命令 → 成功以快照对齐，失败就拨回现值并报错。
    ///
    /// <para>
    /// 失败时用<b>守护进程报的值</b>回滚，而不是把开关翻成 <c>!value</c>：用户可能在命令
    /// 还在途中时又拨了一次，翻反会翻错方向。
    /// </para>
    /// </summary>
    private async Task SetToggleAsync(string command, bool value, string label, bool isAutoStart)
    {
        // 用户拨到的那一档。从发命令到命令回应落地这段时间里，「界面显示它」才是真相
        // ——命令响应里的快照有可能是生效**之前**取的（见 ApplySnapshot 的 ResolveToggle）。
        if (isAutoStart)
        {
            _pendingAutoStart = value;
        }
        else
        {
            _pendingAutoConnect = value;
        }

        try
        {
            ControlResponse response = await SendAsync(command, value ? "on" : "off").ConfigureAwait(true);

            if (response.Ok)
            {
                ErrorMessage = "";
                if (response.Status is not null)
                {
                    ApplySnapshot(response.Status);
                }

                return;
            }

            // 失败：把开关拨回守护进程报告的现值。有响应快照就用它，没有就用最后一次
            // 已知的快照（那也是守护进程报过的）。
            ErrorMessage = FailureText(response, $"{label}设置失败");

            StatusSnapshot? rollback = response.Status ?? _snapshot;
            if (rollback is not null)
            {
                string previousVersionText = VersionText;
                ClearPendingToggles();
                _snapshot = rollback;
                ApplyAutoStart(rollback.AutoStart);
                ApplyAutoConnect(rollback.AutoConnect);
                NotifyDevice();
                UpdateInfoFromSnapshot(rollback, previousVersionText);
            }
        }
        finally
        {
            // 窗口从「命令回应落地」开始算，而不是从发命令开始：写回是串行的，
            // 前面可能还排着一条几十秒的长命令，窗口开早了就会在命令还在途时先失效。
            _pendingToggleUntil = DateTimeOffset.UtcNow + PendingToggleFreshness;

            // 守护进程报的已经是用户拨的那一档 → 目标达成，立刻交还给轮询。
            bool caughtUp = isAutoStart
                ? _snapshot?.AutoStart == _pendingAutoStart
                : _snapshot?.AutoConnect == _pendingAutoConnect;

            if (caughtUp)
            {
                if (isAutoStart)
                {
                    _pendingAutoStart = null;
                }
                else
                {
                    _pendingAutoConnect = null;
                }
            }
        }
    }

    private void ClearPendingToggles()
    {
        _pendingAutoStart = null;
        _pendingAutoConnect = null;
    }

    /// <summary>
    /// 该显示哪一档：写回窗口内以用户拨的那一档为准，窗口外以守护进程为准。
    /// </summary>
    private bool ResolveToggle(bool? pending, bool fromDaemon)
        => pending is bool target && DateTimeOffset.UtcNow < _pendingToggleUntil ? target : fromDaemon;

    /// <summary>
    /// 换皮肤。参数用 <c>UiTheme.ToString().ToLowerInvariant()</c>：守护进程
    /// <c>LinuxDaemon.TryParseTheme</c> 认的就是 <c>classic</c> / <c>porcelain</c> / <c>ceramic</c>。
    /// </summary>
    private void RequestTheme(UiTheme target)
    {
        // 命令还在途中时再点一次：_uiTheme 已经是新值，后面那条命令会把守护进程也推过去。
        // 皮肤是「最后一句说了算」的选项，不需要队列。
        Interlocked.Exchange(ref _themeRequestInFlight, 1);
        Track(RequestThemeAsync(target));
    }

    private async Task RequestThemeAsync(UiTheme target)
    {
        string argument = target.ToString().ToLowerInvariant();
        UiTheme previous = _uiTheme;

        _uiTheme = target;
        NotifyThemeSelection();

        ControlResponse response;
        try
        {
            response = await SendAsync(ControlCommands.Theme, argument).ConfigureAwait(true);
        }
        finally
        {
            Interlocked.Exchange(ref _themeRequestInFlight, 0);
        }

        if (response.Ok)
        {
            ErrorMessage = "";

            // 先让外壳把皮肤换掉，再铺快照。「换肤」只发生在这里和失败回滚那一处：
            // 快照回来时 Theme 已经是新值，ApplyThemeSelection 是幂等的，不会重复触发。
            ThemeApplyRequested?.Invoke(target);

            if (response.Status is not null)
            {
                ApplySnapshot(response.Status);
            }

            return;
        }

        // 换肤失败就得回到用户真正看见的那一套，否则选中态在说谎：
        // 高亮停在釉陶，屏幕上却是经典。这里「真正看见的那一套」也由守护进程说了算。
        UiTheme reported = UiThemes.Normalize(response.Status?.Theme ?? previous);
        _uiTheme = reported;
        NotifyThemeSelection();
        ThemeApplyRequested?.Invoke(reported);
        ErrorMessage = FailureText(response, "界面外观设置失败");
    }

    /// <summary>
    /// 重新绑定：解绑这台设备，然后通知外层切回绑定页。
    /// 命令是 <c>unbind</c>——守护进程收到它就跑 <c>IConnectionController.RebindAsync</c>，
    /// 也就是「先按 fail-open 顺序停连接、再清设备凭据」，与 Windows 端同一个入口。
    /// </summary>
    private async Task RebindAsync()
    {
        ErrorMessage = "";

        ControlResponse response = await SendAsync(ControlCommands.Unbind).ConfigureAwait(true);

        if (!response.Ok)
        {
            ErrorMessage = FailureText(response, "重新绑定失败");
            return;
        }

        // 成功：这一页显示的设备已经不存在了。先就地把它抹掉（不必等下一次轮询），
        // 再让外层切回绑定页——那是 Windows 端「解绑之后要重新输入配对码」的同一句话。
        _unboundLocallyUntil = DateTimeOffset.UtcNow + UnboundPulseDuration;
        if (response.Status is not null)
        {
            ApplySnapshot(response.Status);
        }

        SettingsClosed?.Invoke();
    }

    /// <summary>
    /// 打开日志目录。Windows 端用资源管理器打开 <c>_storage.LogDir</c>；
    /// Linux 端用 <c>xdg-open</c> 打开同一个位置——<c>LinuxPaths.LogDir</c> 就是
    /// <c>$XDG_DATA_HOME/myproxy/logs/</c>（默认 <c>~/.local/share/myproxy/logs/</c>），
    /// 也正是守护进程 <c>LogService</c> 写日志的地方。所以这里仍然只是「打开一个别人定好的路径」，
    /// 不是 GUI 自己发明路径：目录约定归 core 的 <c>LinuxPaths</c>，
    /// 而真正往里写日志的是守护进程。
    /// </summary>
    private void OpenLog()
    {
        try
        {
            string logDir = LinuxPaths.LogDir;
            Directory.CreateDirectory(logDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                ArgumentList = { logDir },
                UseShellExecute = false
            });
        }
        catch
        {
            // 打开日志目录失败不打扰用户（与 Windows 端一致）。
        }
    }

    /// <summary>
    /// 「发现新版本」这一行被点击。Windows 端在这里 <c>Process.Start</c> 打开下载页；
    /// Linux 端把 URL 交给外壳——理由与换肤相同，而且更新包的下载与安装全在守护进程里
    /// （<c>LinuxUpdateInstaller</c>），GUI 不搬任何文件、更不自己装。
    /// </summary>
    private void RequestUpdate()
    {
        if (_updateInfo is null ||
            !Uri.TryCreate(_updateInfo.DownloadUrl, UriKind.Absolute, out Uri? downloadUri) ||
            downloadUri.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        UpdatePageRequested?.Invoke(downloadUri);
    }

    private async Task RequestUpdateCheckAsync()
    {
        ControlResponse response = await SendAsync(ControlCommands.UpdateCheck).ConfigureAwait(true);

        if (response.Ok)
        {
            if (response.Status is not null)
            {
                ApplySnapshot(response.Status);
            }

            return;
        }

        ErrorMessage = FailureText(response, "更新检查失败");
    }

    /// <summary>
    /// 拉一份状态。只用于 <see cref="RefreshDevice"/>：每一次进入设置页都该问一次，
    /// 否则重新绑定之后回来看到的还是上一台设备。
    /// </summary>
    private async Task RefreshStatusAsync()
    {
        ControlResponse response = await SendAsync(ControlCommands.Status).ConfigureAwait(true);
        if (response.Ok && response.Status is not null)
        {
            ApplySnapshot(response.Status);
        }
        // 拉不到就不动界面：把设备组抹掉会让用户以为绑定丢了，而真相只是守护进程没在跑。
    }

    // ---- 快照 → 属性 -----------------------------------------------------------------

    private void ApplyAutoStart(bool value)
    {
        if (_autoStart == value)
        {
            return;
        }

        _autoStart = value;
        OnPropertyChanged(nameof(AutoStart));
        OnPropertyChanged(nameof(AutoStartText));
    }

    private void ApplyAutoConnect(bool value)
    {
        if (_autoConnect == value)
        {
            return;
        }

        _autoConnect = value;
        OnPropertyChanged(nameof(AutoConnect));
        OnPropertyChanged(nameof(AutoConnectText));
    }

    /// <summary>
    /// 把快照里的「有没有新版本」整组铺开。
    ///
    /// <para>
    /// 每次都重算而不是只算一次：守护进程是权威，上一次的结论不能留在这里当缓存
    /// ——否则更新被撤销之后那一行会一直挂在界面上。
    /// </para>
    /// </summary>
    private void UpdateInfoFromSnapshot(StatusSnapshot snapshot, string previousVersionText)
    {
        UpdateInfo? next = snapshot.UpdateAvailable
            ? new UpdateInfo
            {
                Version = snapshot.UpdateVersion,
                Mandatory = snapshot.UpdateMandatory,
                ReleaseId = snapshot.UpdateReleaseId
            }
            : null;

        // 注意：下载地址不在快照里（协议只给版本与结论，不给 URL），所以这里构造的
        // UpdateInfo.DownloadUrl 是空的，UpdateCommand 点下去会安静地什么都不做。
        // 「点更新那一行去下载页」需要协议补 updateUrl 字段，见文件末尾。
        bool changed = next is null
            ? _updateInfo is not null
            : _updateInfo is null
              || _updateInfo.Version != next.Version
              || _updateInfo.Mandatory != next.Mandatory;

        _updateInfo = next;

        if (changed)
        {
            OnPropertyChanged(nameof(UpdateText));
            OnPropertyChanged(nameof(UpdateVisibility));
        }

        // 版本行只在文字真的变了时才通知：轮询每秒一次，无脑通知会让这一行每秒重排一次。
        if (VersionText != previousVersionText)
        {
            OnPropertyChanged(nameof(VersionText));
        }
    }

    /// <summary>
    /// 命令失败时要显示的那句话：守护进程给的 <c>message</c> 优先，没有才退回错误码文案。
    /// 与 Windows 端「先 <c>FriendlyMessage</c>、再 <c>ErrorCodeMessages</c>」同一个优先级。
    /// </summary>
    private static string FailureText(ControlResponse response, string prefix)
        => response.Message.Length > 0
            ? response.Message
            : $"{prefix}：{ErrorCodeMessages.Get(response.ErrorCode)}";

    /// <summary>守护进程当前生效的皮肤（快照里的值）。快照还没来时按出厂皮肤算。</summary>
    private UiTheme CurrentThemeFromDaemon => UiThemes.Normalize(_snapshot?.Theme ?? UiTheme.Classic);

    private bool IsUnboundPulseActive()
        => _unboundLocallyUntil is DateTimeOffset until && DateTimeOffset.UtcNow < until;

    /// <summary>
    /// 解绑之后立刻把设备身份抹掉：守护进程的下一份快照要等一次往返才回来，
    /// 而「已经解绑了却还显示着设备名」是最容易被看见的那种谎报。
    /// </summary>
    private static StatusSnapshot WithoutDevice(StatusSnapshot snapshot) => new()
    {
        Version = snapshot.Version,
        Platform = snapshot.Platform,
        DaemonPid = snapshot.DaemonPid,
        StartedAt = snapshot.StartedAt,
        Bound = false,
        DeviceId = "",
        DeviceName = "",
        BoundAt = null,
        ConfigVersion = snapshot.ConfigVersion,
        State = snapshot.State,
        Mode = snapshot.Mode,
        LatencyMs = snapshot.LatencyMs,
        LastErrorCode = snapshot.LastErrorCode,
        LastErrorMessage = snapshot.LastErrorMessage,
        LastSwitchErrorMessage = snapshot.LastSwitchErrorMessage,
        ServerIdentityUnverified = snapshot.ServerIdentityUnverified,
        IsChecking = snapshot.IsChecking,
        HasCheck = snapshot.HasCheck,
        CheckReachable = snapshot.CheckReachable,
        CheckLatencyMs = snapshot.CheckLatencyMs,
        CheckBestLatencyMs = snapshot.CheckBestLatencyMs,
        CheckSampleCount = snapshot.CheckSampleCount,
        CheckEgress = snapshot.CheckEgress,
        CheckError = snapshot.CheckError,
        UplinkBytesPerSecond = snapshot.UplinkBytesPerSecond,
        DownlinkBytesPerSecond = snapshot.DownlinkBytesPerSecond,
        AutoStart = snapshot.AutoStart,
        AutoConnect = snapshot.AutoConnect,
        Theme = snapshot.Theme,
        UpdateAvailable = snapshot.UpdateAvailable,
        UpdateVersion = snapshot.UpdateVersion,
        UpdateMandatory = snapshot.UpdateMandatory,
        UpdateReleaseId = snapshot.UpdateReleaseId,
        UpdateMessage = snapshot.UpdateMessage
    };

    private Task<ControlResponse> SendAsync(string command, string argument = "")
        // CancellationToken.None：离场不该把已经发出去的命令掐掉（FlushAsync 等的就是它）。
        // 超时由 ControlClient 自己管（连接 3 秒、交换 60 秒）。
        => _client.SendAsync(command, argument, CancellationToken.None);

    /// <summary>
    /// 把一条写回挂到链上。<see cref="FlushAsync"/> 靠它知道「还有没有命令在途中」。
    ///
    /// <para>
    /// 串行化不是洁癖：两条命令并发时，<b>后回来的那条响应里的快照可能比先回来的旧</b>，
    /// 界面会闪回一个过时的状态。串成一条链之后，后发的命令一定落在后面的快照上。
    /// </para>
    /// </summary>
    private void Track(Task work)
    {
        lock (_pendingWriteSync)
        {
            _pendingWrite = ContinueAsync(_pendingWrite, work);
        }
    }

    private static async Task ContinueAsync(Task previous, Task work)
    {
        try
        {
            await previous.ConfigureAwait(true);
        }
        catch
        {
            // 上一条写回自己的异常已经在它内部处理过了；链不能因此断掉。
        }

        try
        {
            await work.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 兜底：控制通道的异常在 ControlClient 里已经变成失败响应，走到这里的是
            // 「我们自己的代码出了问题」。一条命令失败绝不能把界面拖崩。
            Debug.WriteLine($"设置写回失败：{ex}");
        }
    }

    /// <summary>
    /// 守护进程的定期更新检查是否**至少跑过一轮**。用途只有一个：判断
    /// <c>updateMessage</c> 为空到底是「查过、没有新版本」还是「还没查过」。
    ///
    /// <para>
    /// <b>这是一个不得已的推算，不是权威判断。</b>守护进程的检查间隔是
    /// <see cref="DaemonUpdateInterval"/>（必须与 <c>UpdateLoopAsync</c> 里的值一致），
    /// 所以「这次开机已经超过一个间隔」意味着那个循环至少空转过一轮。
    /// 反过来，开机不到一个间隔时它一定还没查过——那时候说「已是最新版本」就是编的。
    /// </para>
    ///
    /// <para>
    /// 协议补上「上次检查时间 / 结论」之后，这个属性应当整个删掉。
    /// </para>
    /// </summary>
    private bool UpdateHasBeenChecked
    {
        get
        {
            if (_snapshot is null)
            {
                return false;
            }

            return DateTimeOffset.UtcNow - _snapshot.StartedAt >= DaemonUpdateInterval;
        }
    }

    // ---- 协议缺口（写给下一个改控制协议的人） --------------------------------------
    //
    // 这一页有四项现在做不到与 Windows 端逐字一致，全部是协议缺口，不是可以糊过去的细节：
    //
    // 1) `autoUpdateCheck` 没有回读字段、也没有写回命令。
    //    守护进程的设置里**有**这个字段（LinuxDaemon.UpdateLoopAsync 读它决定要不要跑
    //    定期检查），只是 StatusSnapshot 没把它带出来、ControlCommands 里也没有对应命令。
    //    现在这一页把它做成本地视图状态，并如实显示「只在当前界面生效」。
    //    要补齐：StatusSnapshot 加 `autoUpdateCheck`；ControlCommands 加
    //    `autoupdatecheck`（参数 on/off，守护进程侧复用 SetToggleAsync 那一套）。
    //
    // 2) 「已是最新版本」这句话基本说不出来。
    //    Windows 端靠「本进程这次检查跑通了且没有新版本」；Linux 端的检查在守护进程里，
    //    而「查过且没有」与「还没查」在快照里长得一样（updateMessage 两者都是空串）。
    //    现在靠 UpdateHasBeenChecked 做保守推算，只在「开机已超过一个检查间隔」时才敢说。
    //    要补齐：StatusSnapshot 加 `updateCheckedAt`（DateTimeOffset?）或
    //    `updateChecked`（bool）——最省事的做法是让守护进程在「查过且没有」时把
    //    updateMessage 写成一句结论。另：守护进程的循环**开机后要等整整一个间隔才跑第一轮**
    //    （UpdateLoopAsync 是先 Task.Delay 再查），所以刚开机的客户端本来就没有结论。
    //
    // 3) 「发现新版本」那一行点不开。
    //    快照只给 updateVersion / updateMandatory / updateReleaseId，没有下载地址，
    //    所以 UpdateInfo.DownloadUrl 是空的，UpdateCommand 点下去什么都不做。
    //    要补齐：StatusSnapshot 加 `updateUrl`（仅 https），外壳订阅 UpdatePageRequested
    //    之后用 xdg-open 打开。
    //
    // 4) `theme` 命令只认 classic / porcelain。
    //    但设置页的分段控件有三档（经典 / 瓷白 / 釉陶），而 Linux 端的皮肤字典只有
    //    Themes/DesignTokens.axaml 与 Porcelain.axaml 两份（gui/Themes 下没有 Ceramic.axaml）。
    //    也就是说用户点「釉陶」一定失败——这一页会如实报错并把高亮拨回守护进程报的那一套，
    //    不会留下一个假的选中态。要补齐：Ceramic 皮肤字典 + LinuxDaemon.TryParseTheme 认 ceramic。
}
