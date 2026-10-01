namespace MyProxy;

/// <summary>Embedded update signing keys. Empty tables reject every update; administrators provision keys before enabling updates.</summary>
public static class LinuxArtifactSigningKeys
{
    /// <summary>
    /// 身份指纹（64 hex，manifest 里的 subjectSha256）→ 32 字节公钥。
    /// **不要放占位值或示例值**：一个假公钥不会让任何东西通过验签，只会让
    /// 「我们已经配好了」看起来是真的。
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> Trusted { get; } = Build();

    /// <summary>
    /// 与 <see cref="ReleaseSigningKeys.Parse"/> 同一套格式（<c>指纹:64hex[, ...]</c>）：
    /// 一个解析器比两个不容易走偏，格式错了就在启动时炸而不是等到有人要发版。
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> Parse(string? spec)
        => ReleaseSigningKeys.Parse(spec);

    private static IReadOnlyDictionary<string, byte[]> Build() => Parse("");
}
