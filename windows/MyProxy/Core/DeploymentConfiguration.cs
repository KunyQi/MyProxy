using System.IO;
using System.Text.Json;

namespace MyProxy.Core;

/// <summary>The root deployment.json embedded into each desktop build.</summary>
public static class DeploymentConfiguration
{
    public const string ResourceName = "MyProxy.Deployment.json";
    private static readonly Lazy<string> EmbeddedJson = new(ReadEmbedded);
    public static string ApiBaseUrl => ParseApiBaseUrl(EmbeddedJson.Value);
    public static IReadOnlyList<string> ConnectivityCheckUrls => ParseConnectivityCheckUrls(EmbeddedJson.Value);

    public static string ParseApiBaseUrl(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("api_base_url", out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("deployment.json requires api_base_url.", nameof(json));
        }
        string url = value.GetString()!;
        if (document.RootElement.EnumerateObject().Any(property => property.Name is not ("api_base_url" or "connectivity_check_urls")))
            throw new ArgumentException("deployment.json contains an unknown setting.", nameof(json));
        if (!BindingTarget.TryFromBaseUrl(url, out BindingTarget target) || !target.IsHttps)
        {
            throw new ArgumentException("deployment.json api_base_url must be an HTTPS origin.", nameof(json));
        }
        return url.TrimEnd('/');
    }

    /// <summary>Probe our own server by default; operators may supply up to four HTTPS 204 endpoints.</summary>
    public static IReadOnlyList<string> ParseConnectivityCheckUrls(string json)
    {
        string origin = ParseApiBaseUrl(json);
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("connectivity_check_urls", out JsonElement value))
            return Array.AsReadOnly(new[] { origin + "/connectivity-check" });
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > 4)
            throw new ArgumentException("connectivity_check_urls requires one to four HTTPS URLs.", nameof(json));
        var urls = new List<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            string? url = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (string.IsNullOrEmpty(url) || url.Any(c => char.IsWhiteSpace(c) || c <= '\u001f' || c == '\u007f') ||
                url.Contains('\\') || url.Contains('?') || url.Contains('#') ||
                !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
                !BindingTarget.TryFromBaseUrl(System.Text.RegularExpressions.Regex.Match(url, @"^https://[^/?#]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Value, out BindingTarget target) ||
                !target.IsHttps || uri.UserInfo.Length != 0)
                throw new ArgumentException("connectivity_check_urls accepts HTTPS URLs without credentials, query or fragment.", nameof(json));
            if (!urls.Contains(url, StringComparer.Ordinal)) urls.Add(url);
        }
        return urls.AsReadOnly();
    }

    private static string ReadEmbedded()
    {
        using Stream stream = typeof(DeploymentConfiguration).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The deployment configuration is missing from this build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
