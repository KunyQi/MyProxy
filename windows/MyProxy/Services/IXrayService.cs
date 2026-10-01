namespace MyProxy.Services;

public sealed class XrayExitEventArgs : EventArgs
{
    public XrayExitEventArgs(int exitCode, bool expected)
    {
        ExitCode = exitCode;
        Expected = expected;
    }

    public int ExitCode { get; }

    public bool Expected { get; }
}

public interface IXrayService
{
    Task StartAsync(string configPath, string workingDir, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task RestartAsync(string configPath, string workingDir, CancellationToken ct);
    bool IsRunning { get; }
    event EventHandler<XrayExitEventArgs>? Exited;
    event EventHandler<string>? OutputReceived;
}
