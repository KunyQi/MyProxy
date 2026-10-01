using System.Windows;
using MyProxy.Core;
using MyProxy.Services;

namespace MyProxy.ViewModels;

/// <summary>
/// 绑定页。只有一个输入：配对码。
///
/// <para>
/// 服务器入口由根目录 deployment.json 在构建时确定。HTTPS 使用系统信任库，
/// 校验证书链、有效期和主机名；用户只输入配对码。
/// </para>
/// </summary>
public sealed class BindViewModel : ViewModelBase
{
    private string _pairingCode = "";
    private string _codeErrorMessage = "";
    private string _formErrorMessage = "";
    private bool _isBinding;

    public BindViewModel(
        IBindingService bindingService,
        IConnectionController connectionController,
        ILogService log)
    {
        _bindingService = bindingService ?? throw new ArgumentNullException(nameof(bindingService));
        _connectionController = connectionController ?? throw new ArgumentNullException(nameof(connectionController));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        BindCommand = new RelayCommand(_log, BindAsync, _ => CanBind);
    }

    public event Action? BindCompleted;

    public string PairingCode
    {
        get => _pairingCode;
        set
        {
            if (SetProperty(ref _pairingCode, value))
            {
                ClearErrors();
                BindCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// 配对码输入框的就地错误。
    ///
    /// <para>
    /// 与 <see cref="FormErrorMessage"/> 分开，是为了让红框只落在真正出错的地方。
    /// 输入框只剩一个了，但「这条错误怪不到输入框头上」依然是个需要表达的状态：
    /// 服务端不可用、限流、未知失败都属于后者。
    /// </para>
    /// </summary>
    public string CodeErrorMessage
    {
        get => _codeErrorMessage;
        private set
        {
            if (SetProperty(ref _codeErrorMessage, value))
            {
                OnPropertyChanged(nameof(CodeErrorVisibility));
                OnPropertyChanged(nameof(HasCodeError));
            }
        }
    }

    /// <summary>不归属输入框的失败：限流、服务端不可用、未知，以及外部传进来的原因。</summary>
    public string FormErrorMessage
    {
        get => _formErrorMessage;
        private set
        {
            if (SetProperty(ref _formErrorMessage, value))
            {
                OnPropertyChanged(nameof(FormErrorVisibility));
                OnPropertyChanged(nameof(HasFormError));
            }
        }
    }

    public bool HasCodeError => !string.IsNullOrWhiteSpace(CodeErrorMessage);

    public bool HasFormError => !string.IsNullOrWhiteSpace(FormErrorMessage);

    public Visibility CodeErrorVisibility
        => HasCodeError ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FormErrorVisibility
        => HasFormError ? Visibility.Visible : Visibility.Collapsed;

    public bool IsBinding
    {
        get => _isBinding;
        private set
        {
            if (SetProperty(ref _isBinding, value))
            {
                BindCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(IsInputEnabled));
                OnPropertyChanged(nameof(BindButtonText));
            }
        }
    }

    public bool IsInputEnabled => !IsBinding;

    public string BindButtonText => IsBinding ? "正在绑定…" : "绑定";

    public RelayCommand BindCommand { get; }

    public void Reset(string? errorMessage = null)
    {
        PairingCode = "";
        // 外部传进来的原因（设备失效之类）不归属输入框。
        ClearErrors();
        FormErrorMessage = errorMessage ?? "";
        IsBinding = false;
    }

    private readonly IBindingService _bindingService;
    private readonly IConnectionController _connectionController;
    private readonly ILogService _log;

    private bool CanBind
        => !IsBinding && PairingCodeNormalizer.TryNormalize(PairingCode, out _);

    /// <summary>
    /// 错误码归属哪里。输入框只有配对码一个，所以这里回答的是「这条错误该不该
    /// 把配对码框标红」——落到 <c>default</c> 的都是与用户输入无关的失败。
    /// </summary>
    private static bool BlamesPairingCode(ErrorCode code) => code switch
    {
        ErrorCode.PairingInvalid or ErrorCode.PairingExpired or ErrorCode.TokenInvalid => true,
        _ => false,
    };

    private void SetError(ErrorCode code, string message)
    {
        ClearErrors();
        if (BlamesPairingCode(code))
        {
            CodeErrorMessage = message;
        }
        else
        {
            FormErrorMessage = message;
        }
    }

    private void ClearErrors()
    {
        CodeErrorMessage = "";
        FormErrorMessage = "";
    }

    private async Task BindAsync()
    {
        if (!PairingCodeNormalizer.TryNormalize(PairingCode, out _))
        {
            SetError(ErrorCode.PairingInvalid, "配对码无效，请检查后重试");
            return;
        }

        IsBinding = true;
        try
        {
            await _bindingService.BindAsync(PairingCode, CancellationToken.None).ConfigureAwait(true);
            _connectionController.MarkBound();
            ClearErrors();
            BindCompleted?.Invoke();
        }
        catch (MyProxyException ex)
        {
            _log.Warn(nameof(BindViewModel), $"Bind failed: {ex.ErrorCode}");
            SetError(ex.ErrorCode, ex.FriendlyMessage);
        }
        catch (Exception ex)
        {
            // 这里在 RelayCommand 之前就把异常吃掉了，所以必须自己记录。
            _log.Error(nameof(BindViewModel), "Bind failed unexpectedly", ex);
            SetError(ErrorCode.Unknown, ErrorCodeMessages.Get(ErrorCode.Unknown));
        }
        finally
        {
            IsBinding = false;
        }
    }
}
