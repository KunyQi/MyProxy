using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Services;

namespace MyProxy.Tests;

/// <summary>
/// 单文件构建里 xray.exe 与 geosite.dat 以 Brotli 形态嵌入（见 MyProxy.csproj）。
/// 解压必须对调用方透明：拿到的是原始字节，随后照旧按 VERSION.txt 核对 SHA-256。
/// </summary>
[TestClass]
public sealed class CoreAssetsResourceTests
{
    private static readonly byte[] Payload =
        Enumerable.Range(0, 64 * 1024).Select(index => (byte)(index * 31 % 251)).ToArray();

    [TestMethod]
    public void CompressedResource_IsDecompressedTransparently()
    {
        byte[] compressed = Compress(Payload);
        string[] names = { "MyProxy.Assets.Core.xray.exe.br", "MyProxy.Assets.Core.LICENSE" };

        using Stream stream = CoreAssets.OpenResource(names, _ => new MemoryStream(compressed), "xray.exe");

        CollectionAssert.AreEqual(Payload, ReadAll(stream));
    }

    [TestMethod]
    public void PlainResource_IsReturnedAsIs()
    {
        string[] names = { "MyProxy.Assets.Core.geoip.dat" };

        using Stream stream = CoreAssets.OpenResource(names, _ => new MemoryStream(Payload), "geoip.dat");

        CollectionAssert.AreEqual(Payload, ReadAll(stream));
    }

    [TestMethod]
    public void BothFormsPresent_IsABuildErrorNotAGuess()
    {
        // 同一份资源同时有明文与 .br：说明构建把它嵌了两次，挑哪个都是在赌。
        string[] names = { "MyProxy.Assets.Core.xray.exe", "MyProxy.Assets.Core.xray.exe.br" };

        Assert.ThrowsException<FileNotFoundException>(
            () => CoreAssets.OpenResource(names, _ => new MemoryStream(Payload), "xray.exe"));
    }

    [TestMethod]
    public void OtherFilesWithASharedSuffix_AreNotConfusedWithTheRequestedOne()
    {
        // "geoip.dat" 不能匹配到 "…Core.xgeoip.dat" 之类的名字：后缀前必须是 ".Assets.Core."。
        string[] names = { "MyProxy.Assets.Core.xgeoip.dat", "MyProxy.Assets.Core.geoip.dat" };

        using Stream stream = CoreAssets.OpenResource(
            names,
            name => name.EndsWith(".Core.geoip.dat", System.StringComparison.Ordinal) ? new MemoryStream(Payload) : null,
            "geoip.dat");

        CollectionAssert.AreEqual(Payload, ReadAll(stream));
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var encoder = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            encoder.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
