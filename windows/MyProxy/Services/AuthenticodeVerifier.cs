using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MyProxy.Services;

/// <summary>一次 Authenticode 校验的结论：签名是否覆盖这份字节，以及是谁签的。</summary>
public sealed record AuthenticodeResult(bool Ok, string SignerSha256 = "", string Detail = "");

/// <summary>
/// 校验一个 PE 文件的 Authenticode 签名，并取出签名者证书的 SHA-256。
///
/// <para>
/// <b>为什么不能只用 <c>X509Certificate.CreateFromSignedFile</c>：</b>它只是把
/// 文件安全目录里的证书读出来，<b>完全不验证签名是否覆盖文件内容</b>。把一份
/// 合法签名的证书 blob 原样附到任意可执行文件上，它照样返回那张真实的签名者
/// 证书——于是「有签名」与「签名者对得上」两条断言会同时通过一个被改过的
/// 二进制。<see cref="WinVerifyTrust"/> 才是真正算摘要的那一步。
/// </para>
///
/// <para>
/// <b>为什么链不可信也算通过：</b>信任根在这里不是本机证书存储，而是 manifest
/// 里那个 <c>subjectSha256</c>，而它本身被 Ed25519 签名覆盖。依赖本机存储反而
/// 更弱——那里可以被装进一张攻击者的根证书。所以本类只接受一小撮明确表示
/// 「摘要没问题、只是链不被本机信任」的状态码；<b>其余一律拒绝</b>，包括
/// 没见过的状态码：白名单而不是黑名单，认不出来就不放行。
/// </para>
///
/// <para>
/// 调用方必须把这里的 <see cref="AuthenticodeResult.SignerSha256"/> 与 manifest
/// 声明的指纹逐字节比对。只验「签名有效」而不比对签名者，等于接受任何一张
/// 能签名的证书。
/// </para>
/// </summary>
public static class AuthenticodeVerifier
{
    /// <summary>
    /// 摘要算对了、只是证书链不被本机信任的那几个状态码。
    ///
    /// <para>
    /// <c>WinVerifyTrust</c> 先算映像摘要再走链校验，所以能走到这些链相关的
    /// 结果，就说明摘要那一关已经过了。摘要不符（<c>TRUST_E_BAD_DIGEST</c>）、
    /// 根本没有签名（<c>TRUST_E_NOSIGNATURE</c>）、以及含义含糊的
    /// <c>TRUST_E_SUBJECT_NOT_TRUSTED</c> 都<b>不在</b>这里。
    /// </para>
    /// </summary>
    private static readonly HashSet<uint> ChainOnlyFailures = new()
    {
        0x800B0101, // CERT_E_EXPIRED
        0x800B0109, // CERT_E_UNTRUSTEDROOT
        0x800B010A, // CERT_E_CHAINING
        0x800B010D, // CERT_E_UNTRUSTEDTESTROOT
        0x800B010E, // CERT_E_REVOCATION_FAILURE
    };

    /// <summary>
    /// 这个 <c>WinVerifyTrust</c> 返回值是否意味着「签名确实覆盖了这份字节」。
    ///
    /// <para>纯函数，与 Win32 无关，因此可以直接测。</para>
    /// </summary>
    public static bool IsDigestTrusted(int status)
        => status == 0 || ChainOnlyFailures.Contains(unchecked((uint)status));

    /// <summary>
    /// 验签并读出签名者指纹。任何一步不成立都返回 <c>Ok = false</c>，
    /// 绝不返回一个「验了一半」的结果。
    /// </summary>
    public static AuthenticodeResult Verify(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        int status;
        try
        {
            status = VerifyEmbeddedSignature(filePath);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // 拿不到 wintrust 就没法判断签名是否有效。fail closed：Update Plane
            // 的整条前提就是没验过的字节不安装。
            return new AuthenticodeResult(false, Detail: "wintrust unavailable");
        }

        if (!IsDigestTrusted(status))
        {
            return new AuthenticodeResult(false, Detail: $"WinVerifyTrust 0x{status:X8}");
        }

        // 摘要过了才有必要问「是谁签的」。反过来（先读证书再验摘要）就会给出
        // 一个看起来可信、实际没被校验过的指纹。
        string? signer = ReadSignerSha256(filePath);
        return signer is null
            ? new AuthenticodeResult(false, Detail: "signer certificate unreadable")
            : new AuthenticodeResult(true, signer);
    }

    private static string? ReadSignerSha256(string filePath)
    {
        try
        {
            using var certificate = new X509Certificate2(
                X509Certificate.CreateFromSignedFile(filePath));
            return Convert.ToHexString(
                certificate.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant();
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static int VerifyEmbeddedSignature(string filePath)
    {
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = filePath,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero
        };

        IntPtr fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, fDeleteOld: false);

            var trustData = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                pPolicyCallbackData = IntPtr.Zero,
                pSIPClientData = IntPtr.Zero,
                // 绝不弹窗：这条路径跑在后台更新里，没有人在旁边点确定。
                dwUIChoice = WTD_UI_NONE,
                // 不查吊销：那是一次网络请求，而这里唯一要回答的是「摘要对不对」。
                // 签名者身份由 manifest 的指纹判定，不由吊销状态判定。
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = fileInfoPtr,
                // IGNORE：不保留状态句柄，也就不需要第二次调用去关它。
                dwStateAction = WTD_STATEACTION_IGNORE,
                hWVTStateData = IntPtr.Zero,
                pwszURLReference = IntPtr.Zero,
                dwProvFlags = WTD_SAFER_FLAG,
                dwUIContext = 0,
                pSignatureSettings = IntPtr.Zero
            };

            Guid action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            return WinVerifyTrust(IntPtr.Zero, ref action, ref trustData);
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPtr);
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_IGNORE = 0;
    private const uint WTD_SAFER_FLAG = 0x100;

    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WinTrustData pWVTData);
}
