using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MyProxy.Services;

public sealed class DpapiSecureStorage : ISecureStorage
{
    private const uint CryptprotectUiForbidden = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MyProxy.V1.DPAPI.Entropy.9F2A");

    private readonly string _dataRoot;
    private ILogService? _log;

    public DpapiSecureStorage(string dataRoot, ILogService? log = null)
    {
        _dataRoot = dataRoot;
        _log = log;
        Directory.CreateDirectory(_dataRoot);
    }

    public void AttachLogger(ILogService logService)
    {
        _log = logService;
    }

    public Task SaveAsync(string name, string plainText, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            byte[] clearBytes = Encoding.UTF8.GetBytes(plainText);
            byte[] protectedBytes = Protect(clearBytes);
            WriteAtomic(name, protectedBytes, _dataRoot);
        }, ct);
    }

    public Task<string?> ReadAsync(string name, CancellationToken ct)
    {
        return Task.Run<string?>(() =>
        {
            ct.ThrowIfCancellationRequested();
            string path = GetPath(name);
            if (!File.Exists(path))
            {
                return null;
            }

            byte[] protectedBytes;
            try
            {
                protectedBytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 文件在但打不开：被 AV 扫描占用、File.Move 中途。这是瞬时故障，
                // 返回 null 会被调用方读成「没有绑定」，进而覆盖掉仍然有效的凭据。
                _log?.Warn(nameof(DpapiSecureStorage), $"{name}.dat 暂时无法读取");
                throw new SecureStorageReadException($"{name}.dat 暂时无法读取", ex);
            }

            try
            {
                byte[] clearBytes = Unprotect(protectedBytes);
                return Encoding.UTF8.GetString(clearBytes);
            }
            catch (Exception ex)
            {
                // 内容损坏，或由别的用户/机器加密：重试不会好转，等同于没有可用绑定。
                _log?.Warn(nameof(DpapiSecureStorage), $"{name}.dat 内容无效");
                System.Diagnostics.Debug.WriteLine($"DpapiSecureStorage.ReadAsync failed: {ex.Message}");
                return null;
            }
        }, ct);
    }

    public Task DeleteAsync(string name, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            string path = GetPath(name);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }, ct);
    }

    private string GetPath(string name)
    {
        string safeName = Path.GetFileName(name);
        return Path.Combine(_dataRoot, safeName + ".dat");
    }

    private static void WriteAtomic(string name, byte[] protectedBytes, string dataRoot)
    {
        string targetPath = Path.Combine(dataRoot, name + ".dat");
        string tmpPath = targetPath + ".tmp";
        File.WriteAllBytes(tmpPath, protectedBytes);
        File.Move(tmpPath, targetPath, overwrite: true);
    }

    private static byte[] Protect(byte[] clearBytes)
    {
        DataBlob clearBlob = new()
        {
            cbData = clearBytes.Length,
            pbData = Marshal.AllocHGlobal(clearBytes.Length)
        };

        DataBlob entropyBlob = new()
        {
            cbData = Entropy.Length,
            pbData = Marshal.AllocHGlobal(Entropy.Length)
        };

        try
        {
            Marshal.Copy(clearBytes, 0, clearBlob.pbData, clearBytes.Length);
            Marshal.Copy(Entropy, 0, entropyBlob.pbData, Entropy.Length);

            if (!CryptProtectData(
                    ref clearBlob,
                    null,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptprotectUiForbidden,
                    out DataBlob protectedBlob))
            {
                throw new InvalidOperationException($"CryptProtectData failed: {Marshal.GetLastWin32Error()}");
            }

            try
            {
                byte[] result = new byte[protectedBlob.cbData];
                Marshal.Copy(protectedBlob.pbData, result, 0, protectedBlob.cbData);
                return result;
            }
            finally
            {
                if (protectedBlob.pbData != IntPtr.Zero)
                {
                    LocalFree(protectedBlob.pbData);
                }
            }
        }
        finally
        {
            if (clearBlob.pbData != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(clearBlob.pbData);
            }

            if (entropyBlob.pbData != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(entropyBlob.pbData);
            }
        }
    }

    private static byte[] Unprotect(byte[] protectedBytes)
    {
        DataBlob protectedBlob = new()
        {
            cbData = protectedBytes.Length,
            pbData = Marshal.AllocHGlobal(protectedBytes.Length)
        };

        DataBlob entropyBlob = new()
        {
            cbData = Entropy.Length,
            pbData = Marshal.AllocHGlobal(Entropy.Length)
        };

        try
        {
            Marshal.Copy(protectedBytes, 0, protectedBlob.pbData, protectedBytes.Length);
            Marshal.Copy(Entropy, 0, entropyBlob.pbData, Entropy.Length);

            if (!CryptUnprotectData(
                    ref protectedBlob,
                    null,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptprotectUiForbidden,
                    out DataBlob clearBlob))
            {
                throw new InvalidOperationException($"CryptUnprotectData failed: {Marshal.GetLastWin32Error()}");
            }

            try
            {
                byte[] result = new byte[clearBlob.cbData];
                Marshal.Copy(clearBlob.pbData, result, 0, clearBlob.cbData);
                return result;
            }
            finally
            {
                if (clearBlob.pbData != IntPtr.Zero)
                {
                    LocalFree(clearBlob.pbData);
                }
            }
        }
        finally
        {
            if (protectedBlob.pbData != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(protectedBlob.pbData);
            }

            if (entropyBlob.pbData != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(entropyBlob.pbData);
            }
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }
}
