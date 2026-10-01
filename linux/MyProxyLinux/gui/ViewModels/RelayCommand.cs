using System.Diagnostics;
using System.Windows.Input;
using MyProxy.Services;

namespace MyProxy.Gui.ViewModels;

/// <summary>
/// 命令：逐字搬自 <c>windows/MyProxy/ViewModels/RelayCommand.cs</c>，只换掉
/// <c>System.Windows.Input.ICommand</c> 的来源——Avalonia 用的就是 .NET 里那个
/// <see cref="ICommand"/>（在 System.ObjectModel 里，不是 WPF 的），所以这一份与
/// Windows 端**语义完全相同**。
///
/// <para>
/// 三个行为照搬，都是踩过坑才有的：
/// </para>
///
/// <list type="number">
/// <item><b>执行中不可重入</b>：<c>_isExecuting</c> 让 <see cref="CanExecute"/> 在执行
/// 期间返回 false，并且一前一后各发一次 <see cref="CanExecuteChanged"/>。按钮因此自己
/// 变灰再变亮，不必让每个 ViewModel 各自维护「忙」标志。</item>
/// <item><b>异步命令的异常必须落地</b>：<c>_ = ExecuteAsync(...)</c> 是 fire-and-forget，
/// 命令体抛出的异常只会被这里的 catch 收住。Windows 端强制要求注入 <c>ILogService</c>
/// 就是为了这条——没有日志就等于「按钮恢复可点、界面无提示、故障无痕迹」。
/// 本进程（托盘 GUI）没有日志服务：守护进程才是写日志的那个进程，GUI 只拿
/// <see cref="Trace"/> 兜底，而**命令体自己**负责把失败原因变成界面上的文案
/// （例如主页把 <c>mode</c> 的拒绝原样显示到 <c>SwitchErrorMessage</c>）。</item>
/// <item><b>执行前再核一遍 <see cref="CanExecute"/></b>：命令可能被按钮之外的东西调起
/// （IsDefault 的回车、代码直接调用），那时代码路径不经过按钮的禁用态。</item>
/// </list>
/// </summary>
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

    /// <summary>
    /// 没有日志服务的异步命令。托盘 GUI 是守护进程的客户端，本进程不写日志文件
    /// （日志归守护进程），所以三个 ViewModel 都走这一条；失败原因由命令体自己
    /// 变成界面文案，走到 catch 里的只剩「本类自己的代码出了问题」。
    /// </summary>
    public RelayCommand(Func<Task> executeAsync, Func<bool>? canExecute = null)
    {
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        _canExecute = canExecute is null ? null : _ => canExecute();
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
            await _executeAsync!().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Windows 端这里曾经是空的：Debug.WriteLine 带 [Conditional("DEBUG")]，
            // 而出厂构建不定义 DEBUG，于是非 MyProxyException 的失败无声消失。
            // Trace 在两类构建里都写，至少不会让一次失败彻底没人知道。
            if (_log is not null)
            {
                _log.Error(nameof(RelayCommand), $"Command execution failed: {ex.Message}", ex);
            }
            else
            {
                Trace.WriteLine($"命令执行失败：{ex}");
            }
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }
}
