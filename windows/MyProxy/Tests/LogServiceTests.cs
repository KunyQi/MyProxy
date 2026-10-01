using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Services;

namespace MyProxy.Tests;

[TestClass]
public sealed class LogServiceTests
{
    private static string NewLogDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "MyProxy.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [TestMethod]
    public void Redact_ReplacesUuidTokenAndPairingCode()
    {
        string logDir = NewLogDir();
        try
        {
            using var log = new LogService(logDir);
            string original = "uuid=123e4567-e89b-12d3-a456-426614174000 token=tok_abc123 code=A7K9-M2QF";
            string redacted = log.Redact(original);

            Assert.IsFalse(redacted.Contains("123e4567-e89b-12d3-a456-426614174000"));
            Assert.IsFalse(redacted.Contains("tok_abc123"));
            Assert.IsFalse(redacted.Contains("A7K9-M2QF"));
        }
        finally
        {
            Directory.Delete(logDir, recursive: true);
        }
    }

    [TestMethod]
    public void Redact_ReplacesRegisteredSensitiveValue()
    {
        string logDir = NewLogDir();
        try
        {
            using var log = new LogService(logDir);
            log.RegisterSensitiveValue("secret-value-123");
            string redacted = log.Redact("prefix secret-value-123 suffix");

            Assert.IsFalse(redacted.Contains("secret-value-123"));
            Assert.IsTrue(redacted.Contains("***"));
        }
        finally
        {
            Directory.Delete(logDir, recursive: true);
        }
    }

    [TestMethod]
    public void Info_WritesLine_AndDisposeFlushes()
    {
        string logDir = NewLogDir();
        try
        {
            var log = new LogService(logDir);
            log.Info("UnitTest", "hello-log");
            log.Dispose();

            string content = File.ReadAllText(Path.Combine(logDir, "app.log"));
            StringAssert.Contains(content, "[INFO]");
            StringAssert.Contains(content, "[UnitTest]");
            StringAssert.Contains(content, "hello-log");
        }
        finally
        {
            Directory.Delete(logDir, recursive: true);
        }
    }

    [TestMethod]
    public void Info_RedactsSensitiveValueBeforeWriting()
    {
        string logDir = NewLogDir();
        try
        {
            var log = new LogService(logDir);
            log.RegisterSensitiveValue("tok_secret_token");
            log.Info("UnitTest", "token=tok_secret_token");
            log.Dispose();

            string content = File.ReadAllText(Path.Combine(logDir, "app.log"));
            Assert.IsFalse(content.Contains("tok_secret_token"));
        }
        finally
        {
            Directory.Delete(logDir, recursive: true);
        }
    }

    [TestMethod]
    public void Logging_AfterDispose_IsIgnored()
    {
        string logDir = NewLogDir();
        try
        {
            var log = new LogService(logDir);
            log.Info("UnitTest", "accepted");
            log.Dispose();

            log.Info("UnitTest", "late-info");
            log.Warn("UnitTest", "late-warning");
            log.Error("UnitTest", "late-error", new Exception("late-exception"));

            string[] lines = File.ReadAllLines(Path.Combine(logDir, "app.log"));
            Assert.AreEqual(1, lines.Length);
            StringAssert.Contains(lines[0], "accepted");
        }
        finally
        {
            Directory.Delete(logDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task Dispose_DuringLogFormatting_DropsLateMessage()
    {
        string logDir = NewLogDir();
        using var formattingStarted = new ManualResetEventSlim();
        using var finishFormatting = new ManualResetEventSlim();
        using var log = new LogService(logDir);
        Task? write = null;
        try
        {
            log.Info("UnitTest", "accepted");
            write = Task.Run(() => log.Error("UnitTest", "late-error",
                new BlockingException(formattingStarted, finishFormatting)));
            Assert.IsTrue(formattingStarted.Wait(TimeSpan.FromSeconds(5)));

            log.Dispose();
            finishFormatting.Set();
            await write.WaitAsync(TimeSpan.FromSeconds(5));

            string[] lines = File.ReadAllLines(Path.Combine(logDir, "app.log"));
            Assert.AreEqual(1, lines.Length);
            StringAssert.Contains(lines[0], "accepted");
        }
        finally
        {
            finishFormatting.Set();
            if (write is not null)
            {
                await write.WaitAsync(TimeSpan.FromSeconds(5));
            }
            log.Dispose();
            Directory.Delete(logDir, recursive: true);
        }
    }

    [TestMethod]
    public void Dispose_ConcurrentCalls_FlushesAcceptedEntries()
    {
        string logDir = NewLogDir();
        try
        {
            var log = new LogService(logDir);
            for (int i = 0; i < 32; i++)
            {
                log.Info("UnitTest", $"accepted-{i}");
            }

            Parallel.For(0, 8, _ => log.Dispose());

            string[] lines = File.ReadAllLines(Path.Combine(logDir, "app.log"));
            Assert.AreEqual(32, lines.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                StringAssert.Contains(lines[i], $"accepted-{i}");
            }
        }
        finally
        {
            Directory.Delete(logDir, recursive: true);
        }
    }

    [TestMethod]
    public void Rotation_MovesOldLogToApp1Log()
    {
        string logDir = NewLogDir();
        try
        {
            string appLogPath = Path.Combine(logDir, "app.log");
            File.WriteAllBytes(appLogPath, new byte[(2 * 1024 * 1024) + 1000]);

            var log = new LogService(logDir);
            log.Info("UnitTest", "after-rotation");
            log.Dispose();

            Assert.IsTrue(File.Exists(Path.Combine(logDir, "app.1.log")));
            Assert.IsTrue(File.Exists(appLogPath));
            string newContent = File.ReadAllText(appLogPath);
            StringAssert.Contains(newContent, "after-rotation");
            FileInfo newInfo = new(appLogPath);
            Assert.IsTrue(newInfo.Length < 2 * 1024 * 1024);
        }
        finally
        {
            Directory.Delete(logDir, recursive: true);
        }
    }

    private sealed class BlockingException(
        ManualResetEventSlim formattingStarted,
        ManualResetEventSlim finishFormatting) : Exception
    {
        public override string ToString()
        {
            formattingStarted.Set();
            if (!finishFormatting.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Log formatting was not released by the test.");
            }
            return "controlled-exception";
        }
    }
}
