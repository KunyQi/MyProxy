using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Services;

namespace MyProxy.Tests;

/// <summary>
/// Update Plane 的第三道校验：包里的可执行文件必须真的被它声明的那张证书签过。
///
/// <para>
/// 这条要钉住的是一个容易悄悄退化的性质。<c>X509Certificate.CreateFromSignedFile</c>
/// 只把安全目录里的证书读出来，<b>不算摘要</b>——把一份合法签名的证书 blob 抄到
/// 任意字节上，它照样返回那张真实证书。所以「有签名 + 签名者对得上」这两条断言
/// 可以同时通过一个被改过的二进制。下面那条篡改用例就是在证明这件事不再成立。
/// </para>
/// </summary>
[TestClass]
public sealed class AuthenticodeVerifierTests
{
    [TestMethod]
    public void IsDigestTrusted_AcceptsOnlyChainFailures()
    {
        // 摘要算过了，只是链不被本机信任——本项目的信任根是 manifest 里那个
        // 指纹，不是本机证书存储，所以这些放行。
        Assert.IsTrue(AuthenticodeVerifier.IsDigestTrusted(0), "S_OK");
        Assert.IsTrue(AuthenticodeVerifier.IsDigestTrusted(unchecked((int)0x800B0109u)), "CERT_E_UNTRUSTEDROOT");
        Assert.IsTrue(AuthenticodeVerifier.IsDigestTrusted(unchecked((int)0x800B010Au)), "CERT_E_CHAINING");
        Assert.IsTrue(AuthenticodeVerifier.IsDigestTrusted(unchecked((int)0x800B0101u)), "CERT_E_EXPIRED");

        // 摘要不符、根本没签名、以及含义含糊的那一个，都必须拒。
        Assert.IsFalse(AuthenticodeVerifier.IsDigestTrusted(unchecked((int)0x80096010u)), "TRUST_E_BAD_DIGEST");
        Assert.IsFalse(AuthenticodeVerifier.IsDigestTrusted(unchecked((int)0x800B0100u)), "TRUST_E_NOSIGNATURE");
        Assert.IsFalse(AuthenticodeVerifier.IsDigestTrusted(unchecked((int)0x800B0004u)), "TRUST_E_SUBJECT_NOT_TRUSTED");

        // 白名单而不是黑名单：认不出来的状态码不放行。
        Assert.IsFalse(AuthenticodeVerifier.IsDigestTrusted(unchecked((int)0x80004005u)), "E_FAIL");
        Assert.IsFalse(AuthenticodeVerifier.IsDigestTrusted(1), "unknown positive status");
    }

    [TestMethod]
    public void Verify_RejectsAnUnsignedFile()
    {
        string unsigned = Path.Combine(Path.GetTempPath(), $"myproxy-unsigned-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(unsigned, new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00 });
        try
        {
            AuthenticodeResult result = AuthenticodeVerifier.Verify(unsigned);
            Assert.IsFalse(result.Ok, result.Detail);
            Assert.AreEqual("", result.SignerSha256);
        }
        finally
        {
            File.Delete(unsigned);
        }
    }

    [TestMethod]
    public void Verify_AcceptsASignedFileAndRejectsATamperedCopy()
    {
        // 测试输出目录里有一堆内嵌 Authenticode 签名的 Microsoft DLL（测试宿主
        // 自己的依赖），它们随 NuGet 还原而来，本机与 CI 上都在。不硬编码某一个
        // 文件名：签谁不重要，重要的是「同一份字节验得过、改一个字节就验不过」。
        string? signed = FindSignedFile(AppContext.BaseDirectory);
        if (signed is null)
        {
            Assert.Inconclusive("测试输出目录里没有内嵌签名的文件可用；跳过。");
            return;
        }

        AuthenticodeResult original = AuthenticodeVerifier.Verify(signed);
        Assert.IsTrue(original.Ok, $"{signed}: {original.Detail}");
        Assert.AreEqual(64, original.SignerSha256.Length);
        Assert.AreEqual(original.SignerSha256, original.SignerSha256.ToLowerInvariant());

        string tampered = Path.Combine(Path.GetTempPath(), $"myproxy-tampered-{Guid.NewGuid():N}.dll");
        File.Copy(signed, tampered, overwrite: true);
        try
        {
            // 改映像中段的一个字节。证书 blob 原样留着，所以 CreateFromSignedFile
            // 仍然会给出同一张签名者证书——只有真正算摘要的那一步能发现区别。
            byte[] bytes = File.ReadAllBytes(tampered);
            int offset = bytes.Length / 2;
            bytes[offset] = (byte)(bytes[offset] ^ 0xFF);
            File.WriteAllBytes(tampered, bytes);

            AuthenticodeResult afterTamper = AuthenticodeVerifier.Verify(tampered);
            Assert.IsFalse(
                afterTamper.Ok,
                "改过字节的副本必须被拒绝——这正是只读证书不验摘要时会漏掉的那种包。");
        }
        finally
        {
            File.Delete(tampered);
        }
    }

    private static string? FindSignedFile(string directory)
    {
        foreach (string path in Directory.EnumerateFiles(directory, "*.dll"))
        {
            if (AuthenticodeVerifier.Verify(path).Ok)
            {
                return path;
            }
        }

        return null;
    }
}
