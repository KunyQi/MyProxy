using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MyProxy.Services;

public sealed class LogService : ILogService, IDisposable
{
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private const string AppLogFileName = "app.log";

    private static readonly Regex UuidRegex = new(
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TokenRegex = new(
        @"\btok_[A-Za-z0-9_\-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PairingCodeRegex = new(
        @"\b[A-Z0-9]{4}-[A-Z0-9]{4}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _logDir;
    private readonly BlockingCollection<string> _queue = new();
    private readonly object _queueSync = new();
    private readonly Task _writerTask;
    private readonly object _sensitiveLock = new();
    private readonly List<string> _sensitiveValues = new();
    private int _disposed;

    public LogService(string logDir)
    {
        _logDir = logDir;
        Directory.CreateDirectory(_logDir);
        _writerTask = Task.Run(WriterLoop);
    }

    public void RegisterSensitiveValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        lock (_sensitiveLock)
        {
            if (!_sensitiveValues.Contains(value))
            {
                _sensitiveValues.Add(value);
            }
        }
    }

    public void Info(string scope, string message)
        => Enqueue("INFO", scope, message, null);

    public void Warn(string scope, string message)
        => Enqueue("WARN", scope, message, null);

    public void Error(string scope, string message, Exception? ex = null)
        => Enqueue("ERROR", scope, message, ex);

    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        string result = text;

        lock (_sensitiveLock)
        {
            foreach (string value in _sensitiveValues)
            {
                result = result.Replace(value, "***", StringComparison.Ordinal);
            }
        }

        result = UuidRegex.Replace(result, "***");
        result = TokenRegex.Replace(result, "tok_***");
        result = PairingCodeRegex.Replace(result, "***");
        return result;
    }

    public void Dispose()
    {
        lock (_queueSync)
        {
            if (_disposed != 0)
            {
                return;
            }

            _disposed = 1;
            _queue.CompleteAdding();
        }

        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 忽略 Dispose 阶段的写入异常。
        }
    }

    private void Enqueue(string level, string scope, string message, Exception? ex)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        string fullMessage = ex is null ? message : $"{message}{Environment.NewLine}{ex}";
        string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] [{scope}] {Redact(fullMessage)}";
        lock (_queueSync)
        {
            if (_disposed == 0)
            {
                _queue.Add(line);
            }
        }
    }

    private void WriterLoop()
    {
        try
        {
            foreach (string line in _queue.GetConsumingEnumerable())
            {
                try
                {
                    RotateIfNeeded();
                    string path = Path.Combine(_logDir, AppLogFileName);
                    File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                }
                catch
                {
                    // 日志写盘失败不能影响业务。
                }
            }
        }
        finally
        {
            // Dispose 只限时等待；队列由消费者排空后释放，避免慢磁盘上的关闭竞态。
            _queue.Dispose();
        }
    }

    private void RotateIfNeeded()
    {
        string path = Path.Combine(_logDir, AppLogFileName);
        if (!File.Exists(path))
        {
            return;
        }

        FileInfo info = new(path);
        if (info.Length < MaxLogBytes)
        {
            return;
        }

        string rotatedPath = Path.Combine(_logDir, "app.1.log");
        File.Move(path, rotatedPath, overwrite: true);
    }
}
