using System.Text;

namespace MyProxy.Core;

public static class PairingCodeNormalizer
{
    private const int CompactCodeLength = 8;

    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        StringBuilder cleaned = new(raw.Length);
        foreach (char c in raw.Trim())
        {
            if (c == ' ' || c == '-')
            {
                continue;
            }

            cleaned.Append(char.ToUpperInvariant(c));
        }

        if (cleaned.Length == 0)
        {
            return "";
        }

        return string.Join("-", ChunkByFour(cleaned.ToString()));
    }

    public static bool TryNormalize(string? raw, out string normalized)
    {
        normalized = Normalize(raw);
        if (normalized.Length != CompactCodeLength + 1 || normalized[4] != '-')
        {
            return false;
        }

        for (int i = 0; i < normalized.Length; i++)
        {
            if (i == 4)
            {
                continue;
            }

            char c = normalized[i];
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<string> ChunkByFour(string value)
    {
        for (int i = 0; i < value.Length; i += 4)
        {
            int length = Math.Min(4, value.Length - i);
            yield return value.Substring(i, length);
        }
    }
}
