using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using MyProxy.Core;

namespace MyProxy.Services;

/// <summary>
/// Locates the loose Core files used by directory builds, or materializes the
/// embedded copy for single-file builds. Xray is a separate process and must
/// always have a real executable and geo files on disk.
/// </summary>
internal static class CoreAssets
{
    private static readonly string[] RequiredFiles = ["xray.exe", "geoip.dat", "geosite.dat"];
    private const string ResourcePrefix = ".Assets.Core.";

    public static string ResolveDirectory(IStorageService storage)
    {
        ArgumentNullException.ThrowIfNull(storage);

        Assembly assembly = typeof(CoreAssets).Assembly;
        string adjacent = Path.Combine(AppContext.BaseDirectory, "Core");
        // A folder build has no embedded manifest. A single-file build must
        // use its own verified payload even if an old Core folder is nearby.
        if (!HasEmbeddedManifest(assembly) && HasRequiredFiles(adjacent))
        {
            return adjacent;
        }

        try
        {
            Dictionary<string, string> hashes = ReadManifest(assembly);
            string cacheRoot = Path.Combine(storage.DataRoot, "Core");
            string versionDir = Path.Combine(cacheRoot, hashes["xray.exe"]);

            // Two app versions can be started near an upgrade. The digest in
            // the mutex name and directory keeps their extraction independent.
            using var mutex = new Mutex(false, @"Local\MyProxy.CoreAssets." + hashes["xray.exe"]);
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(60));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                throw new IOException("Timed out waiting for Core asset extraction.");
            }

            try
            {
                Directory.CreateDirectory(versionDir);
                foreach (string name in RequiredFiles)
                {
                    EnsureExtracted(assembly, name, hashes[name], versionDir);
                }

                // Keep the upstream license alongside its extracted binary.
                // VERSION.txt is embedded to define the verified payload.
                EnsureExtracted(assembly, "LICENSE", expectedHash: null, versionDir);
            }
            finally
            {
                mutex.ReleaseMutex();
            }

            return versionDir;
        }
        catch (MyProxyException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new MyProxyException(
                ErrorCodeMessages.Get(ErrorCode.XrayMissing), ErrorCode.XrayMissing, ex);
        }
    }

    /// <summary>
    /// Crash recovery may see the Core of a previous app version. Compare its
    /// full path to our known adjacent location or a digest-named cache path;
    /// never kill a process merely because it is named xray.exe.
    /// </summary>
    public static bool IsOwnedXrayPath(IStorageService storage, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string actual = Path.GetFullPath(path);
            string adjacent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Core", "xray.exe"));
            if (!HasEmbeddedManifest(typeof(CoreAssets).Assembly) &&
                string.Equals(actual, adjacent, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string root = Path.GetFullPath(Path.Combine(storage.DataRoot, "Core"));
            string relative = Path.GetRelativePath(root, actual);
            string[] parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return parts.Length == 2 &&
                   parts[0].Length == 64 &&
                   parts[0].All(Uri.IsHexDigit) &&
                   string.Equals(parts[1], "xray.exe", StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(actual) && HasHash(actual, parts[0]);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool HasRequiredFiles(string dir) =>
        RequiredFiles.All(name => File.Exists(Path.Combine(dir, name)));

    private static bool HasEmbeddedManifest(Assembly assembly) =>
        assembly.GetManifestResourceNames().Any(name =>
            name.EndsWith(ResourcePrefix + "VERSION.txt", StringComparison.Ordinal));

    private static Dictionary<string, string> ReadManifest(Assembly assembly)
    {
        using Stream stream = OpenResource(assembly, "VERSION.txt");
        using var reader = new StreamReader(stream);
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            int separator = line.IndexOf('=');
            if (separator > 0)
            {
                entries[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        foreach (string name in RequiredFiles)
        {
            if (!entries.TryGetValue(name, out string? hash) ||
                hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException($"Invalid Core asset digest: {name}");
            }
        }

        return entries;
    }

    private static void EnsureExtracted(Assembly assembly, string name, string? expectedHash, string directory)
    {
        string target = Path.Combine(directory, name);
        if (File.Exists(target) &&
            (expectedHash is null || HasHash(target, expectedHash)))
        {
            return;
        }

        string temporary = Path.Combine(directory, $".{name}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (Stream resource = OpenResource(assembly, name))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                resource.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            if (expectedHash is not null && !HasHash(temporary, expectedHash))
            {
                throw new InvalidDataException($"Embedded Core asset failed SHA-256 verification: {name}");
            }

            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool HasHash(string path, string expected)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 打开一份嵌入的 Core 资源。大文件以 Brotli 形态嵌入（资源名多一个 <c>.br</c>，
    /// 见 MyProxy.csproj），这里透明解压；调用方拿到的永远是原始字节，随后照旧按
    /// VERSION.txt 核对 SHA-256。同一份资源同时存在明文与 .br 两种形态算构建错误。
    /// </summary>
    private static Stream OpenResource(Assembly assembly, string name)
        => OpenResource(assembly.GetManifestResourceNames(), assembly.GetManifestResourceStream, name);

    /// <summary>同上，资源来源可注入（测试不必真的造一个单文件构建）。</summary>
    internal static Stream OpenResource(
        IReadOnlyCollection<string> resourceNames,
        Func<string, Stream?> open,
        string name)
    {
        string suffix = ResourcePrefix + name;
        string compressedSuffix = suffix + ".br";
        string[] matches = resourceNames
            .Where(resource => resource.EndsWith(suffix, StringComparison.Ordinal)
                || resource.EndsWith(compressedSuffix, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new FileNotFoundException($"Expected one embedded Core resource for {name}; found {matches.Length}.");
        }

        Stream stream = open(matches[0])
            ?? throw new FileNotFoundException($"Cannot open embedded Core resource: {name}");
        return matches[0].EndsWith(compressedSuffix, StringComparison.Ordinal)
            ? new BrotliStream(stream, CompressionMode.Decompress)
            : stream;
    }
}
