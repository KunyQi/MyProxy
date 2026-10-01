using System.Windows.Input;
using MyProxy.Services;

namespace MyProxy.ViewModels;

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?>? _execute;
    private readonly Func<Task>? _executeAsync;
    private readonly Func<object?, bool>? _canExecute;
    private readonly ILogService? _log;
    private bool _isExecuting;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    // 异步命令强制要求 ILogService：命令体的异常只会被下面的 catch 收住，
    // 没有日志就等于按钮恢复可点、界面无提示、故障无痕迹。
    public RelayCommand(ILogService log, Func<Task> executeAsync, Func<object?, bool>? canExecute = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        _canExecute = canExecute;
    }

    public RelayCommand(ILogService log, Func<Task> executeAsync, Func<bool>? canExecute)
        : this(log, executeAsync, canExecute is null ? null : _ => canExecute())
    {
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
        => !_isExecuting && (_canExecute?.Invoke(parameter) ?? true);

    public void Execute(object? parameter)
    {
        if (_executeAsync is not null)
        {
            _ = ExecuteAsync(parameter);
        }
        else
        {
            _execute?.Invoke(parameter);
        }
    }

    public void RaiseCanExecuteChanged()
        => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private async Task ExecuteAsync(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await _executeAsync!();
        }
        catch (Exception ex)
        {
            // 出厂构建里这里曾是空的：Debug.WriteLine 带 [Conditional("DEBUG")]，
            // 而 MyProxy.csproj 不定义 DEBUG，于是非 MyProxyException 的失败无声消失。
            _log!.Error(nameof(RelayCommand), $"Command execution failed: {ex.Message}", ex);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }
}
