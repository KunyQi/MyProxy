using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Services;
using MyProxy.ViewModels;

namespace MyProxy.Tests;

/// <summary>
/// RelayCommand 的 catch-all 曾只有 Debug.WriteLine，而它带 [Conditional("DEBUG")]
/// 且 MyProxy.csproj 不定义 DEBUG —— 出厂构建里该 catch 块编译后为空，
/// 非 MyProxyException 的失败在按下按钮后无日志、无提示、无状态变化。
/// </summary>
[TestClass]
public sealed class RelayCommandTests
{
    [TestMethod]
    public async Task AsyncFailure_IsLogged()
    {
        var log = new RecordingLogService();
        var command = new RelayCommand(
            log,
            () => throw new InvalidOperationException("状态机非法边"));

        command.Execute(null);
        await log.ErrorLogged.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, log.Errors.Count);
        Assert.AreEqual(nameof(RelayCommand), log.Errors[0].Scope);
        StringAssert.Contains(log.Errors[0].Message, "状态机非法边");
        Assert.IsInstanceOfType<InvalidOperationException>(log.Errors[0].Exception);
    }

    [TestMethod]
    public async Task AsyncFailure_StillRestoresExecutability()
    {
        var log = new RecordingLogService();
        var command = new RelayCommand(log, () => throw new InvalidOperationException("boom"));

        command.Execute(null);
        await log.ErrorLogged.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(command.CanExecute(null));
    }

    [TestMethod]
    public void AsyncCommand_RequiresALogger()
    {
        // 缺日志的异步命令等于故障无痕迹，构造期就该拒绝。
        Assert.ThrowsException<ArgumentNullException>(
            () => new RelayCommand(null!, () => Task.CompletedTask));
    }

    private sealed class RecordingLogService : ILogService
    {
        public TaskCompletionSource ErrorLogged { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<(string Scope, string Message, Exception? Exception)> Errors { get; } = new();

        public void RegisterSensitiveValue(string value)
        {
        }

        public void Info(string scope, string message)
        {
        }

        public void Warn(string scope, string message)
        {
        }

        public void Error(string scope, string message, Exception? ex = null)
        {
            Errors.Add((scope, message, ex));
            ErrorLogged.TrySetResult();
        }
    }
}
