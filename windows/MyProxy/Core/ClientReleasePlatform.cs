namespace MyProxy.Core;

/// <summary>
/// 「这个客户端认为自己是什么平台」的完整描述。
///
/// <para>
/// 验签本身与平台无关（Ed25519 + 原始字节），但 manifest 里有两个字段是平台相关的：
/// <c>platform</c> 与 <c>artifact.signature.type</c>。原来它们被写死在
/// <see cref="ReleaseManifestVerifier"/> 里（只认 <c>windows</c> + <c>authenticode</c>），
/// 于是一份合法的 Linux release 到了检票口就被判成 <c>WrongPlatform</c>。
/// </para>
///
/// <para>
/// 平台签名在两端对应的是不同的东西，含义相同：<b>「这些字节是被这个平台的发布身份
/// 签过的」</b>。Windows 用 Authenticode 的签名者证书指纹；Linux 的 tarball 没有
/// 操作系统级的签名可查，所以用一把<b>发布产物专用的 Ed25519 公钥</b>对产物做分离
/// 签名——用的就是本项目已经在三端各钉了 RFC 8032 向量的那套实现，不引入新的密码学
/// 依赖。<see cref="LinuxSignatureTypes"/> 里的取值是那个格式的名字。
/// </para>
///
/// <para>
/// 两者判定的都是「<c>subjectSha256</c> 是不是一个我们内置的发布身份」。指纹本身
/// 不证明字节完整（那由 manifest 里被签名覆盖的 SHA-256 负责），它证明的是
/// <b>谁发的</b>。
/// </para>
/// </summary>
public sealed record ClientReleasePlatform(string Name, IReadOnlySet<string> SignatureTypes)
{
    public const string WindowsPlatform = "windows";
    public const string LinuxPlatform = "linux";
    public const string AndroidPlatform = "android";

    /// <summary>Authenticode 签名者证书的 SHA-256 指纹。</summary>
    public const string AuthenticodeSignatureType = "authenticode";

    /// <summary>对产物本身的分离 Ed25519 签名（公钥指纹进 subjectSha256）。</summary>
    public const string Ed25519SignatureType = "ed25519";

    public static ClientReleasePlatform Windows { get; } = new(
        WindowsPlatform,
        new HashSet<string>(StringComparer.Ordinal) { AuthenticodeSignatureType });

    public static ClientReleasePlatform Linux { get; } = new(
        LinuxPlatform,
        new HashSet<string>(StringComparer.Ordinal) { Ed25519SignatureType });

    public static ClientReleasePlatform Android { get; } = new(
        AndroidPlatform,
        new HashSet<string>(StringComparer.Ordinal) { "apksigner" });

    /// <summary>
    /// 按 <c>AppInfo.Platform</c> 取本构建的平台。
    ///
    /// <para>
    /// <b>认不出来就 fail closed</b>：返回一个不接受任何产物签名类型的平台，
    /// 于是任何 manifest 都会在签名类型那一步被拒。<see cref="AppInfo.Platform"/>
    /// 是各端自己写死的常量，出现第三种取值只可能是有人改了它却没在下面登记——
    /// 那种情况下「什么更新都不装」是唯一安全的答复。
    /// </para>
    /// </summary>
    public static ClientReleasePlatform ForPlatformName(string name) => name switch
    {
        WindowsPlatform => Windows,
        LinuxPlatform => Linux,
        AndroidPlatform => Android,
        _ => new ClientReleasePlatform(name ?? "", new HashSet<string>(StringComparer.Ordinal))
    };

    public bool Accepts(string signatureType) => SignatureTypes.Contains(signatureType);
}
