using System.Text.RegularExpressions;

namespace MyProxy.Core;

/// <summary>A fixed API origin. HTTP is reserved for local development.</summary>
public sealed record BindingTarget
{
    public const int DefaultPort = 443;
    public const string HttpsScheme = "https";
    public required string Host { get; init; }
    public int Port { get; init; } = DefaultPort;
    public string Scheme { get; init; } = HttpsScheme;
    public string BaseUrl => $"{Scheme}://{Host}:{Port}";
    public bool IsHttps => Scheme == HttpsScheme;
    public bool IsConfigured => !Host.Equals("invalid", StringComparison.OrdinalIgnoreCase) && !Host.Trim('[', ']').EndsWith(".invalid", StringComparison.OrdinalIgnoreCase);

    public static bool TryFromBaseUrl(string? baseUrl, out BindingTarget target)
    {
        target = null!;
        if (string.IsNullOrEmpty(baseUrl) || baseUrl.Any(c => char.IsWhiteSpace(c) || c <= '\u001f' || c == '\u007f') || baseUrl.Contains('\\')) return false;
        Match origin = Regex.Match(baseUrl, @"^https?://(?<authority>[^/?#]+)(/?)$", RegexOptions.IgnoreCase);
        if (!origin.Success || origin.Groups["authority"].Value.EndsWith(':') ||
            !Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) ||
            string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.Port is <= 0 or > 65535)
        {
            return false;
        }
        string authority = origin.Groups["authority"].Value;
        string rawHost = authority.StartsWith('[')
            ? authority[..(authority.IndexOf(']') + 1)]
            : authority.Split(':')[0];
        if (!IsCanonicalHost(rawHost, uri)) return false;
        target = new BindingTarget { Host = uri.Host, Port = uri.Port, Scheme = uri.Scheme };
        return true;
    }

    private static bool IsCanonicalHost(string host, Uri uri)
    {
        if (host.StartsWith('['))
        {
            return !host.Contains('%') && Regex.IsMatch(host, @"^\[[0-9a-fA-F:.]+\]$") && uri.HostNameType == UriHostNameType.IPv6;
        }
        if (host.Length is 0 or > 253 || host.EndsWith('.')) return false;
        string[] labels = host.Split('.');
        if (labels.All(label => label.All(char.IsAsciiDigit)))
        {
            return labels.Length == 4 && labels.All(label =>
                int.TryParse(label, out int value) && value is >= 0 and <= 255 && label == value.ToString());
        }
        if (labels.All(label => Regex.IsMatch(label, @"^(0x[0-9a-f]+|[0-9]+)$", RegexOptions.IgnoreCase))) return false;
        if (uri.HostNameType == UriHostNameType.IPv4 && host != uri.Host) return false;
        return labels.All(label => Regex.IsMatch(label, @"^[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$"));
    }

    public static BindingTarget FromBaseUrl(string baseUrl)
        => TryFromBaseUrl(baseUrl, out BindingTarget target)
            ? target
            : throw new ArgumentException("API URL must be an HTTP(S) origin without credentials, path, query or fragment.", nameof(baseUrl));
}
