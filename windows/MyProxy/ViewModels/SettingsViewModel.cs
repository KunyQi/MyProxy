using System.Diagnostics;
using System.IO;
using System.Windows;
using MyProxy.Core;
using MyProxy.Models;
using MyProxy.Services;

namespace MyProxy.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly IStorageService _storage;
    private readonly IStartupService _startup;
    private readonly IUpdateService _update;
    private readonly IConnectionController _controller;
    private readonly ILogService _log;
    private readonly IThemeService? _theme;

    private bool _autoStart;
    private bool _autoConnect;
    private bool _autoUpdateCheck;
    private UiTheme _uiTheme = UiTheme.Classic;
    private UpdateInfo? _updateInfo;
    private DeviceConfig? _device;
    /// <summary>检查**跑通了**且没有新版本。默认 false：没查过和查失败都不能说「已是最新」。</summary>
    private bool _knownUpToDate;
    private string _errorMessage = "";
    private readonly object _pendingSaveSync = new();
    private Task _pendingSettingsSave = Task.CompletedTask;

    public SettingsViewModel(
        IStorageService storage,
        IStartupService startup,
        IUpdateService update,
        IConnectionController controller,
        ILogService log,
        IThemeService? theme = null)
    {
        _storage = storage;
        _startup = startup;
        _update = update;
        _controller = controller;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _theme = theme;

        RefreshDevice();

        AppSettings settings = _storage.LoadSettings();
        _autoStart = settings.AutoStart;
        _autoConnect = settings.AutoConnect;
        _autoUpdateCheck = settings.AutoUpdateCheck;
        // 以服务当下真正生效的皮肤为准，而不是配置文件：配置里写了一个加载失败的值时，
        // 设置页该高亮的是眼睛看到的那一套。
        _uiTheme = UiThemes.Normalize(_theme?.Current ?? settings.UiTheme);

        CloseCommand = new RelayCommand(() => SettingsClosed?.Invoke());
        RebindCommand = new RelayCommand(_log, RebindAsync, () => true);
        OpenLogCommand = new RelayCommand(OpenLog);
        UpdateCommand = new RelayCommand(OpenUpdateUrl, () => _updateInfo is not null);

        if (_autoUpdateCheck)
        {
            _ = CheckUpdateAsync();
        }
    }

    public event Action? SettingsClosed;

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

    public Visibility ErrorVisibility
        => string.IsNullOrWhiteSpace(ErrorMessage) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// 界面皮肤。分段控件的三个单选按钮各绑一个 bool，
    /// 与主页「规则 / 全局」同一种写法，不为一个枚举引入转换器。
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

            UiTheme previous = _uiTheme;
            _uiTheme = target;

            try
            {
                _theme?.Apply(target);
            }
            catch (Exception ex)
            {
                // 换皮失败就得回到用户真正看见的那一套，否则选中态在说谎：
                // 高亮停在釉陶，屏幕上却是经典。
                _uiTheme = previous;
                _log.Error(nameof(SettingsViewModel), $"apply theme failed: {ex.Message}");
                ErrorMessage = ErrorCodeMessages.Get(ErrorCode.Unknown);
                NotifyThemeSelection();
                return;
            }

            ErrorMessage = "";
            NotifyThemeSelection();
            SaveSettings();
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

    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            if (SetProperty(ref _autoStart, value))
            {
                try
                {
                    _startup.SetAutoStartEnabled(value);
                    SaveSettings();
                    ErrorMessage = "";
                }
                catch
                {
                    _autoStart = !value;
                    OnPropertyChanged(nameof(AutoStart));
                    ErrorMessage = ErrorCodeMessages.Get(ErrorCode.Unknown);
                }
            }
        }
    }

    public bool AutoConnect
    {
        get => _autoConnect;
        set
        {
            if (SetProperty(ref _autoConnect, value))
            {
                SaveSettings();
            }
        }
    }

    public bool AutoUpdateCheck
    {
        get => _autoUpdateCheck;
        set
        {
            if (SetProperty(ref _autoUpdateCheck, value))
            {
                SaveSettings();
                if (value)
                {
                    _ = CheckUpdateAsync();
                }
                else
                {
                    UpdateInfo = null;
                    // 关掉自动检查后就不再知道是不是最新，收回这句话。
                    KnownUpToDate = false;
                }
            }
        }
    }

    public UpdateInfo? UpdateInfo
    {
        get => _updateInfo;
        private set
        {
            if (SetProperty(ref _updateInfo, value))
            {
                OnPropertyChanged(nameof(UpdateText));
                OnPropertyChanged(nameof(UpdateVisibility));
                UpdateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// 重读设备身份。**每次进入设置页都必须调用**：本 ViewModel 在 MainWindow 构造时
    /// 就建好、之后一直复用，只在构造时读一次的话——
    ///   · 首次运行（启动时未绑定）→ 整个会话里设备组永远不出现，绑完也不出现；
    ///   · 重新绑定之后回到本页 → 显示的仍是上一次的设备名与绑定日期。
    /// </summary>
    public void RefreshDevice()
    {
        try
        {
            _device = _storage.LoadDevice();
            if (ErrorMessage == DeviceReadError) ErrorMessage = "";
        }
        catch (SecureStorageReadException ex)
        {
            // 临时锁文件/权限故障不等于解绑。保留已读身份，也不能让主窗口构造失败。
            _log.Error(nameof(SettingsViewModel), "Device identity temporarily unreadable", ex);
            ErrorMessage = DeviceReadError;
        }
        OnPropertyChanged(nameof(DeviceVisibility));
        OnPropertyChanged(nameof(DeviceName));
        OnPropertyChanged(nameof(BoundAtText));
    }

    private const string DeviceReadError = "暂时无法读取绑定信息，请稍后重试";

    /// <summary>没有设备记录时整组隐藏，而不是显示一个编造的名字。</summary>
    public Visibility DeviceVisibility =>
        string.IsNullOrEmpty(_device?.DeviceName) ? Visibility.Collapsed : Visibility.Visible;

    public string DeviceName => _device?.DeviceName ?? "";

    public string BoundAtText => _device is null || _device.BoundAt == default
        ? ""
        : $"{_device.BoundAt.ToLocalTime():yyyy-MM-dd} 绑定";

    /// <summary>
    /// 两种连接模式的名字与说明。常量直出，不带状态——开关在主页，这一页
    /// 只负责解释差别。全部取自 <see cref="ProxyModeText"/>，托盘与主页读的是
    /// 同一份文案。
    /// </summary>
    public static string RuleModeName => ProxyModeText.RuleName;

    public static string RuleModeDescription => ProxyModeText.RuleDescription;

    public static string GlobalModeName => ProxyModeText.GlobalName;

    public static string GlobalModeDescription => ProxyModeText.GlobalDescription;

    /// <summary>
    /// 版本不占一行，它是「更新」这件事的当前状态。
    /// 只有确实查过且没查到新版本时才敢说「已是最新版本」——没查过、关掉了自动检查、
    /// 或者检查抛了异常，都只报版本号，不下结论。
    /// </summary>
    public string VersionText => _knownUpToDate
        ? $"当前 {Version}，已是最新版本"
        : $"当前 {Version}";

    public string UpdateText => UpdateInfo is null
        ? ""
        : UpdateInfo.Mandatory
            ? $"请更新到 {UpdateInfo.Version}"
            : $"发现新版本 {UpdateInfo.Version}";

    public Visibility UpdateVisibility => UpdateInfo is null ? Visibility.Collapsed : Visibility.Visible;

    public RelayCommand CloseCommand { get; }

    public RelayCommand RebindCommand { get; }

    public RelayCommand OpenLogCommand { get; }

    public RelayCommand UpdateCommand { get; }

    public async Task FlushAsync()
    {
        while (true)
        {
            Task pending;
            lock (_pendingSaveSync)
            {
                pending = _pendingSettingsSave;
            }

            await pending;

            lock (_pendingSaveSync)
            {
                if (ReferenceEquals(pending, _pendingSettingsSave))
                {
                    return;
                }
            }
        }
    }

    private async Task CheckUpdateAsync()
    {
        try
        {
            UpdateCheckResult result = await _update.CheckAsync(CancellationToken.None);
            UpdateInfo = _autoUpdateCheck && result.Status == UpdateCheckStatus.UpdateAvailable
                ? result.Update
                : null;
            KnownUpToDate = _autoUpdateCheck && result.Status == UpdateCheckStatus.UpToDate;
        }
        catch
        {
            // 更新检查失败静默忽略——但不能因此说「已是最新版本」。
            KnownUpToDate = false;
        }
    }

    private bool KnownUpToDate
    {
        set
        {
            if (_knownUpToDate != value)
            {
                _knownUpToDate = value;
                OnPropertyChanged(nameof(VersionText));
            }
        }
    }

    private async Task RebindAsync()
    {
        MessageBoxResult result = System.Windows.MessageBox.Show(
            "重新绑定将停止代理并清除当前绑定，是否继续？",
            AppInfo.ProductName,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.OK)
        {
            return;
        }

        ErrorMessage = "";
        try
        {
            await _controller.RebindAsync(CancellationToken.None);
        }
        catch (MyProxyException ex)
        {
            _log.Warn(nameof(SettingsViewModel), $"Rebind failed: {ex.ErrorCode}");
            ErrorMessage = ex.FriendlyMessage;
        }
        catch (Exception ex)
        {
            // 同 BindViewModel：异常在 RelayCommand 之前被吃掉，必须自己记录。
            _log.Error(nameof(SettingsViewModel), "Rebind failed unexpectedly", ex);
            ErrorMessage = ErrorCodeMessages.Get(ErrorCode.Unknown);
        }
    }

    private void OpenLog()
    {
        try
        {
            string logDir = _storage.LogDir;
            Directory.CreateDirectory(logDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{logDir}\"",
                UseShellExecute = true
            });
        }
        catch
        {
            // 打开日志目录失败不打扰用户。
        }
    }

    private void OpenUpdateUrl()
    {
        if (UpdateInfo is null ||
            !Uri.TryCreate(UpdateInfo.DownloadUrl, UriKind.Absolute, out Uri? downloadUri) ||
            downloadUri.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = downloadUri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch
        {
            // 打开下载页失败不打扰用户。
        }
    }

    private void SaveSettings()
    {
        bool autoStart = _autoStart;
        bool autoConnect = _autoConnect;
        bool autoUpdateCheck = _autoUpdateCheck;
        UiTheme uiTheme = _uiTheme;

        lock (_pendingSaveSync)
        {
            _pendingSettingsSave = SaveSettingsAfterAsync(
                _pendingSettingsSave,
                autoStart,
                autoConnect,
                autoUpdateCheck,
                uiTheme);
        }
    }

    private async Task SaveSettingsAfterAsync(
        Task previous,
        bool autoStart,
        bool autoConnect,
        bool autoUpdateCheck,
        UiTheme uiTheme)
    {
        try
        {
            await previous;
            await _storage.UpdateSettingsAsync(settings =>
            {
                settings.AutoStart = autoStart;
                settings.AutoConnect = autoConnect;
                settings.AutoUpdateCheck = autoUpdateCheck;
                settings.UiTheme = uiTheme;
            }, CancellationToken.None);
        }
        catch
        {
            ErrorMessage = ErrorCodeMessages.Get(ErrorCode.Unknown);
        }
    }
}
