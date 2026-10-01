namespace MyProxy.Services;

public interface ILogService
{
    void RegisterSensitiveValue(string value);
    void Info(string scope, string message);
    void Warn(string scope, string message);
    void Error(string scope, string message, Exception? ex = null);
}
