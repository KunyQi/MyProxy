using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyProxy;
using MyProxy.Core;
using MyProxy.Models;

namespace MyProxy.Services;

public sealed class BindingService : IBindingService
{
    private const int ClaimTimeoutSeconds = 15;
    private const int MaxDeviceNameLength = 128;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IStorageService _storage;
    private readonly ILogService _log;
    private readonly IApiEndpoint _endpoint;

    public BindingService(IStorageService storage, ILogService log, IApiEndpoint endpoint)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    public async Task<BindResult> BindAsync(string pairingCode, CancellationToken ct)
    {
        if (!PairingCodeNormalizer.TryNormalize(pairingCode, out string normalized))
        {
            throw new MyProxyException(GetFriendlyMessage(ErrorCode.PairingInvalid), ErrorCode.PairingInvalid);
        }

        _log.RegisterSensitiveValue(normalized);
        string clientInstanceId = await _storage
            .GetOrCreateClientInstanceIdAsync(ct)
            .ConfigureAwait(false);

        ClaimResponse? claim;
        try
        {
            claim = await PostClaimAsync(normalized, clientInstanceId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw new MyProxyException(GetFriendlyMessage(ErrorCode.ApiUnreachable), ErrorCode.ApiUnreachable, ex);
        }
        catch (HttpRequestException ex) when (TlsFailure.IsServerCertificateRejected(ex))
        {
            // 不能并进 ApiUnreachable：地址是编译期常量，用户手上没有可核对的东西，重试
            // 也永远不会好。部署管理员需要检查服务器证书链、有效期与主机名。
            throw new MyProxyException(GetFriendlyMessage(ErrorCode.ServerUntrusted), ErrorCode.ServerUntrusted, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new MyProxyException(GetFriendlyMessage(ErrorCode.ApiUnreachable), ErrorCode.ApiUnreachable, ex);
        }

        if (claim is null ||
            string.IsNullOrWhiteSpace(claim.DeviceId) ||
            string.IsNullOrWhiteSpace(claim.DeviceToken) ||
            claim.ConfigVersion <= 0 ||
            claim.Config is null)
        {
            throw new MyProxyException(GetFriendlyMessage(ErrorCode.Unknown), ErrorCode.Unknown);
        }

        try
        {
            XrayConfigGenerator.ValidateProfile(claim.Config);
        }
        catch (ArgumentException ex)
        {
            throw new MyProxyException(GetFriendlyMessage(ErrorCode.Unknown), ErrorCode.Unknown, ex);
        }

        _log.RegisterSensitiveValue(claim.DeviceToken);
        _log.RegisterSensitiveValue(claim.Config.Uuid);

        var device = new DeviceConfig
        {
            DeviceId = claim.DeviceId,
            DeviceToken = claim.DeviceToken,
            DeviceName = TruncateDeviceName(Environment.MachineName),
            Platform = AppInfo.Platform,
            ClientVersion = AppInfo.Version,
            BoundAt = DateTimeOffset.UtcNow
        };

        var cacheEntry = new ConfigCacheEntry
        {
            ConfigVersion = claim.ConfigVersion,
            FetchedAt = DateTimeOffset.UtcNow,
            Verified = false,
            Profile = claim.Config
        };

        await _storage.SaveDeviceAsync(device, ct).ConfigureAwait(false);
        await _storage.SaveConfigCacheAsync(cacheEntry, ct).ConfigureAwait(false);

        _log.Info(nameof(BindingService), $"Claim succeeded. ConfigVersion={claim.ConfigVersion}");

        return new BindResult
        {
            Device = device,
            Profile = claim.Config,
            ConfigVersion = claim.ConfigVersion
        };
    }

    private async Task<ClaimResponse?> PostClaimAsync(
        string normalizedPairingCode,
        string clientInstanceId,
        CancellationToken ct)
    {
        var request = new ClaimRequest
        {
            PairingCode = normalizedPairingCode,
            DeviceName = TruncateDeviceName(Environment.MachineName),
            Platform = AppInfo.Platform,
            ClientVersion = AppInfo.Version,
            ClientInstanceId = clientInstanceId
        };

        string requestJson = JsonSerializer.Serialize(request, JsonOptions);
        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        // 目标是固定的，所以 claim 和其余调用共用同一个端点与连接池。
        HttpClient http = _endpoint.Client("binding", TimeSpan.FromSeconds(ClaimTimeoutSeconds));
        using HttpResponseMessage response = await http
            .PostAsync($"{_endpoint.BaseUrl}/api/device/claim", content, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            ErrorCode errorCode = await MapErrorAsync(response, ct).ConfigureAwait(false);
            _log.Warn(nameof(BindingService), $"Claim failed. ErrorCode={errorCode}");
            throw new MyProxyException(GetFriendlyMessage(errorCode), errorCode);
        }

        string responseJson = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            throw new MyProxyException(GetFriendlyMessage(ErrorCode.Unknown), ErrorCode.Unknown);
        }

        try
        {
            return JsonSerializer.Deserialize<ClaimResponse>(responseJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new MyProxyException(GetFriendlyMessage(ErrorCode.Unknown), ErrorCode.Unknown, ex);
        }
    }

    private static async Task<ErrorCode> MapErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? serverCode = null;
        try
        {
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(body))
            {
                using JsonDocument document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("error", out JsonElement error) &&
                    error.TryGetProperty("code", out JsonElement code) &&
                    code.ValueKind == JsonValueKind.String)
                {
                    serverCode = code.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // 非 JSON 错误体：按 HTTP 状态映射。
        }

        if (!string.IsNullOrWhiteSpace(serverCode))
        {
            return serverCode switch
            {
                "PairingInvalid" => ErrorCode.PairingInvalid,
                "PairingExpired" => ErrorCode.PairingExpired,
                "TokenInvalid" => ErrorCode.TokenInvalid,
                "DeviceNotFound" => ErrorCode.TokenInvalid,
                "ServerError" => ErrorCode.ApiUnreachable,
                "RateLimited" => ErrorCode.ApiUnreachable,
                "BadRequest" => ErrorCode.PairingInvalid,
                _ => ErrorCode.Unknown
            };
        }

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => ErrorCode.TokenInvalid,
            HttpStatusCode.TooManyRequests => ErrorCode.ApiUnreachable,
            _ when (int)response.StatusCode >= 500 => ErrorCode.ApiUnreachable,
            _ => ErrorCode.Unknown
        };
    }

    private static string GetFriendlyMessage(ErrorCode errorCode)
    {
        return ErrorCodeMessages.Get(errorCode);
    }

    private static string TruncateDeviceName(string deviceName)
    {
        return deviceName.Length <= MaxDeviceNameLength
            ? deviceName
            : deviceName[..MaxDeviceNameLength];
    }

    private sealed class ClaimRequest
    {
        [JsonPropertyName("pairingCode")]
        public string PairingCode { get; set; } = "";

        [JsonPropertyName("deviceName")]
        public string DeviceName { get; set; } = "";

        [JsonPropertyName("platform")]
        public string Platform { get; set; } = "";

        [JsonPropertyName("clientVersion")]
        public string ClientVersion { get; set; } = "";

        [JsonPropertyName("clientInstanceId")]
        public string ClientInstanceId { get; set; } = "";
    }

    private sealed class ClaimResponse
    {
        [JsonPropertyName("deviceId")]
        public string DeviceId { get; set; } = "";

        [JsonPropertyName("deviceToken")]
        public string DeviceToken { get; set; } = "";

        [JsonPropertyName("configVersion")]
        public long ConfigVersion { get; set; }

        [JsonPropertyName("config")]
        public ServerProfile? Config { get; set; }
    }
}
