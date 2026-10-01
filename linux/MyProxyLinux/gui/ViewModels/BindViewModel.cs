using MyProxy.Core;
using MyProxy.Services;

// 命名空间与 gui/MainWindow.axaml.cs 的 using 一致（MyProxy.Gui.ViewModels）。
namespace MyProxy.Gui.ViewModels;

/// <summary>
/// 绑定页。只有一个输入：配对码。
///
/// <para>
/// 服务器入口由根目录 deployment.json 在构建时确定。HTTPS 使用系统信任库，
/// 校验证书链、有效期和主机名；用户只输入配对码。
/// </para>
///
/// <para>
/// <b>与 Windows 端唯一的差别是绑定由谁执行。</b>Windows 端这里注入
/// <c>IBindingService</c> + <c>IConnectionController</c>，本进程自己发请求、自己落凭据；
/// Linux 端设备凭据只有守护进程持有（GUI 连它的路径都不该知道），所以这里只发一条
/// <c>bind</c> 控制命令，参数是用户输入的那串码——规范化由守护进程里的
/// <c>BindingService.BindAsync</c> 做，用的就是两端链接的同一份
/// <see cref="PairingCodeNormalizer"/>。这一页仍然自己先校一遍，
/// 为的是把错误就地落在输入框上，而不是白跑一趟往返。
/// </para>
/// </summary>
public sealed class BindViewModel : ViewModelBase
{
    private readonly ControlClient _client;

    private string _pairingCode = "";
    private string _codeErrorMessage = "";
    private string _formErrorMessage = "";
    private bool _isBinding;

    public BindViewModel(ControlClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        BindCommand = new RelayCommand(BindAsync, () => CanBind);
    }

    /// <summary>绑定成功。外层（MainWindow）据此切到主界面——与 Windows 端同一个语义。</summary>
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
    /// 控制面不可达、限流、未知失败都属于后者。
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

    /// <summary>不归属输入框的失败：连不上后台服务、限流、未知，以及外部传进来的原因。</summary>
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

    /// <summary>输入框错误是否显示。Avalonia 用布尔 <c>IsVisible</c>，不是 WPF 的 <c>Visibility</c>。</summary>
    public bool CodeErrorVisibility => HasCodeError;

    public bool FormErrorVisibility => HasFormError;

    /// <summary>
    /// 绑定进行中。视图拿它禁用输入框（<see cref="IsInputEnabled"/>）并换按钮文案
    /// （<see cref="BindButtonText"/>）——与 Windows 端同一组派生属性。
    /// </summary>
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

    /// <summary>
    /// 回到刚进这一页的状态。外层在两个时机调用：用户「重新绑定」之后切回本页，
    /// 以及守护进程报未绑定的时候。参数 <paramref name="errorMessage"/> 是外部原因
    /// （设备失效之类），按 Windows 端的规矩它<b>不</b>归属输入框，所以只写进表单错误槽。
    /// </summary>
    public void Reset(string? errorMessage = null)
    {
        PairingCode = "";
        // 外部传进来的原因（设备失效之类）不归属输入框。
        ClearErrors();
        FormErrorMessage = errorMessage ?? "";
        IsBinding = false;
    }

    /// <summary>
    /// 按钮可点：没在绑定中，且配对码已经是一串合法的 8 位码。
    /// 用的就是共享的 <see cref="PairingCodeNormalizer"/>——与守护进程在校验、
    /// 与 Windows 端在判断，读的是同一份规则。
    /// </summary>
    private bool CanBind
        => !IsBinding && PairingCodeNormalizer.TryNormalize(PairingCode, out _);

    /// <summary>
    /// 错误码归属哪里。输入框只有配对码一个，所以这里回答的是「这条错误该不该
    /// 把配对码框标红」——落到 <c>default</c> 的都是与用户输入无关的失败。
    /// 与 Windows 端逐字一致（含「令牌失效也算配对码的问题」这一条）。
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
        // 命令可能在按钮被禁用之后仍然被调起（IsDefault 的回车、代码调用），
        // 所以前提条件在这里再核一遍，而不是只依赖 CanBind。
        if (!PairingCodeNormalizer.TryNormalize(PairingCode, out _))
        {
            SetError(ErrorCode.PairingInvalid, ErrorCodeMessages.Get(ErrorCode.PairingInvalid));
            return;
        }

        IsBinding = true;
        try
        {
            // 参数用**用户输入的原串**，不用规范化后的结果：规范化（去空格补短横线）
            // 是共享的 PairingCodeNormalizer 的职责，守护进程里的 BindingService.BindAsync
            // 第一件事就是调它；在这里先规范化一遍再发过去等于把同一条规则实现两次。
            // 上面那次 TryNormalize 只用来判断「这串码看起来对不对」，不改变发出去的内容。
            //
            // 超时交给 ControlClient 自己管（连接 3 秒、交换 60 秒）：这里传
            // CancellationToken.None，与主页 ViewModel 的写法一致。
            ControlResponse response = await _client
                .SendAsync(ControlCommands.Bind, PairingCode, CancellationToken.None)
                .ConfigureAwait(true);

            if (!response.Ok)
            {
                // 错误码到文案的映射照 Windows 端：守护进程给的 message 优先，
                // 没有才退回共享的 ErrorCodeMessages。
                SetError(
                    response.ErrorCode,
                    response.Message.Length > 0
                        ? response.Message
                        : ErrorCodeMessages.Get(response.ErrorCode));
                return;
            }

            ClearErrors();
            // 绑定成功后清空输入框：那串码是一次性凭据，留在界面上没有意义
            // （与主页 ViewModel 同一条规矩）。
            PairingCode = "";
            BindCompleted?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // 控制通道的异常在 ControlClient 里都已经变成失败响应，能走到这里的只有
            // 「进程正在收尾、等待被取消」。它不归属输入框。
            SetError(ErrorCode.ApiUnreachable, ErrorCodeMessages.Get(ErrorCode.ApiUnreachable));
        }
        catch (Exception ex)
        {
            // 兜底：控制通道不该抛到这里，但界面不能因为一次绑定崩掉。
            System.Diagnostics.Trace.WriteLine($"绑定失败：{ex}");
            SetError(ErrorCode.Unknown, ErrorCodeMessages.Get(ErrorCode.Unknown));
        }
        finally
        {
            IsBinding = false;
        }
    }
}
