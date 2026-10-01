namespace MyProxy;

/// <summary>Embedded update signing keys. Empty tables reject every update; administrators provision keys before enabling updates.</summary>
public static class ReleaseSigningKeys
{
    /// <summary>
    /// keyId → 32 字节公钥。**不要在这里放占位值或示例值**：一个假公钥不会让
    /// 任何东西通过验签，但会让「我们已经配好了」这件事看起来是真的。
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> Trusted { get; } = Build();

    /// <summary>
    /// 从 <c>keyId:64hex[,keyId:64hex]</c> 构造公钥表。任何一项格式不对就整体抛出，
    /// 避免一个拼写错误静默地把某把密钥从信任表里去掉。
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> Parse(string? spec)
    {
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string item in (spec ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string text = item.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            int separator = text.IndexOf(':');
            if (separator <= 0 || separator == text.Length - 1)
            {
                throw new ArgumentException("Release signing key entries must be keyId:hex.", nameof(spec));
            }

            string keyId = text[..separator].Trim();
            string hex = text[(separator + 1)..].Trim().ToLowerInvariant();
            if (keyId.Length == 0 || keyId.Length > 64 || hex.Length != 64)
            {
                throw new ArgumentException("Release signing keys must be 32 bytes of hex.", nameof(spec));
            }

            byte[] raw;
            try
            {
                raw = Convert.FromHexString(hex);
            }
            catch (FormatException ex)
            {
                throw new ArgumentException("Release signing keys must be hexadecimal.", nameof(spec), ex);
            }

            if (!keys.TryAdd(keyId, raw))
            {
                throw new ArgumentException($"Release signing key {keyId} is declared twice.", nameof(spec));
            }
        }

        return keys;
    }

    private static IReadOnlyDictionary<string, byte[]> Build() => Parse("");
}
