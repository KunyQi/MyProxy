using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyProxy.Core;
using MyProxy.Models;

namespace MyProxy.Services;

public sealed class ConfigService : IConfigService
{
    private const int ConfigSyncTimeoutSeconds = 5;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IStorageService _storage;
    private readonly ILogService _log;
    private readonly IApiEndpoint _endpoint;

    public ConfigService(IStorageService storage, ILogService log, IApiEndpoint endpoint)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    // 客户端从端点取：它按调用方缓存 HttpClient，连接池因此能复用。
    private string ApiBaseUrl => _endpoint.BaseUrl;

    private HttpClient Http
        => _endpoint.Client(nameof(ConfigService), TimeSpan.FromSeconds(ConfigSyncTimeoutSeconds));

    public async Task<ConfigCacheEntry> GetConfigAsync(CancellationToken ct)
    {
        DeviceConfig? device = _storage.LoadDevice();
        if (device is null)
        {
            throw new MyProxyException(ErrorCodeMessages.Get(ErrorCode.NotBound), ErrorCode.NotBound);
        }

        _log.RegisterSensitiveValue(device.DeviceToken);

        ConfigCacheEntry? cache = _storage.LoadConfigCache();

        try
        {
            (long configVersion, ServerProfile profile) = await GetConfigOnlineAsync(device.DeviceToken, ct)
                .ConfigureAwait(false);

            _log.RegisterSensitiveValue(profile.Uuid);

            if (configVersion > (cache?.ConfigVersion ?? 0))
            {
                return CreateOnlineCandidate(configVersion, profile);
            }

            if (configVersion == (cache?.ConfigVersion ?? 0))
            {
                if (!IsUsableVerifiedCache(cache))
                {
                    // 首份缓存尚未验证，或缓存缺失：使用服务端候选并在启动成功后提升。
                    return CreateOnlineCandidate(configVersion, profile);
                }

                return cache!;
            }

            // 服务端版本小于缓存版本：异常数据，沿用缓存。
            return await FallbackToVerifiedCacheAsync(cache).ConfigureAwait(false);
        }
        catch (MyProxyException ex) when (ex.ErrorCode is ErrorCode.TokenInvalid or ErrorCode.ServerNotConfigured)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (MyProxyException ex) when (ex.ErrorCode == ErrorCode.ServerUntrusted)
        {
            // 服务器证书校验失败。有验证过的缓存就照旧用它连（数据面不依赖这条 TLS），
            // 没有的话如实说出原因：换成 NoValidConfig 的「暂无可用配置，请重新绑定」会把
            // 用户引去要新的配对码，而绑定走的是同一条 TLS，同样会失败。
            _log.Error(nameof(ConfigService), "Config sync rejected the server certificate chain, validity or hostname");
            if (!IsUsableVerifiedCache(cache))
            {
                throw;
            }

            return cache!;
        }
        catch (Exception ex)
        {
            _log.Warn(nameof(ConfigService), $"Config sync failed: {ex.Message}");
            return await FallbackToVerifiedCacheAsync(cache).ConfigureAwait(false);
        }
    }

    public async Task PromoteCurrentConfigAsync(ServerProfile profile, long configVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (configVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(configVersion));
        }

        XrayConfigGenerator.ValidateProfile(profile);

        var entry = new ConfigCacheEntry
        {
            ConfigVersion = configVersion,
            FetchedAt = DateTimeOffset.UtcNow,
            Verified = true,
            Profile = profile
        };

        await _storage.SaveConfigCacheAsync(entry, ct).ConfigureAwait(false);
        _log.Info(nameof(ConfigService), $"Config promoted. ConfigVersion={configVersion}");
    }

    public async Task<HeartbeatResult> HeartbeatAsync(CancellationToken ct)
    {
        DeviceConfig? device = _storage.LoadDevice();
        if (device is null)
        {
            throw new MyProxyException(ErrorCodeMessages.Get(ErrorCode.NotBound), ErrorCode.NotBound);
        }

        _log.RegisterSensitiveValue(device.DeviceToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/api/device/heartbeat");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            device.DeviceToken);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await SendConfigRequestAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new MyProxyException(ErrorCodeMessages.Get(ErrorCode.TokenInvalid), ErrorCode.TokenInvalid);
        }

        if (!response.IsSuccessStatusCode)
        {
            ErrorCode errorCode = await MapErrorAsync(response, ct).ConfigureAwait(false);
            throw new MyProxyException(ErrorCodeMessages.Get(errorCode), errorCode);
        }

        string responseJson = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        HeartbeatResult? heartbeat;
        try
        {
            heartbeat = JsonSerializer.Deserialize<HeartbeatResult>(responseJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new MyProxyException(ErrorCodeMessages.Get(ErrorCode.ApiUnreachable), ErrorCode.ApiUnreachable, ex);
        }

        if (heartbeat is null || !heartbeat.Ok || heartbeat.ConfigVersion <= 0)
        {
            throw new MyProxyException(ErrorCodeMessages.Get(ErrorCode.ApiUnreachable), ErrorCode.ApiUnreachable);
        }

        return heartbeat;
    }

    private async Task<(long ConfigVersion, ServerProfile Profile)> GetConfigOnlineAsync(
        string deviceToken,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}/api/device/config");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", deviceToken);

        using HttpResponseMessage response = await SendConfigRequestAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new MyProxyException(ErrorCodeMessages.Get(ErrorCode.TokenInvalid), ErrorCode.TokenInvalid);
        }

        if (!response.IsSuccessStatusCode)
        {
            ErrorCode errorCode = await MapErrorAsync(response, ct).ConfigureAwait(false);
            throw new MyProxyException(ErrorCodeMessages.Get(errorCode), errorCode);
        }

        string responseJson = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            throw new MyProxyException(ErrorCodeMessages.Get(ErrorCode.ApiUnreachable), ErrorCode.ApiUnreachable);
        }

        ConfigResponse? configResponse;
        try
        {
            configResponse = JsonSerializer.Deserialize<ConfigResponse>(responseJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new MyProxyException(ErrorCodeMessages.Get(ErrorCode.ApiUnreachable), ErrorCode.ApiUnreachable, ex);
        }

        if (configResponse is null ||
            configResponse.ConfigVersion <= 0 ||
            configResponse.Config is null)
        {
            throw new MyProxyException(ErrorCodeMessages.Get(ErrorCode.ApiUnreachable), ErrorCode.ApiUnreachable);
        }

        try
        {
            XrayConfigGenerator.ValidateProfile(configResponse.Config);
        }
        catch (ArgumentException ex)
        {
            throw new MyProxyException(ErrorCodeMessages.Get(ErrorCode.ApiUnreachable), ErrorCode.ApiUnreachable, ex);
        }

        return (configResponse.ConfigVersion, configResponse.Config);
    }

    private static ConfigCacheEntry CreateOnlineCandidate(long configVersion, ServerProfile profile)
    {
        return new ConfigCacheEntry
        {
            ConfigVersion = configVersion,
            FetchedAt = DateTimeOffset.UtcNow,
            Verified = false, // 内存候选尚未提升为 LastKnownGood。
            Profile = profile
        };
    }

    private static Task<ConfigCacheEntry> FallbackToVerifiedCacheAsync(ConfigCacheEntry? cache)
    {
        if (!IsUsableVerifiedCache(cache))
        {
            throw new MyProxyException(ErrorCodeMessages.Get(ErrorCode.NoValidConfig), ErrorCode.NoValidConfig);
        }

        return Task.FromResult(cache!);
    }

    private static bool IsUsableVerifiedCache(ConfigCacheEntry? cache)
    {
        if (cache is null || !cache.Verified || cache.ConfigVersion <= 0)
        {
            return false;
        }

        try
        {
            XrayConfigGenerator.ValidateProfile(cache.Profile);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }


    private async Task<HttpResponseMessage> SendConfigRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await Http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex) when (TlsFailure.IsServerCertificateRejected(ex))
        {
            // 服务器证书校验失败。配置同步照旧回退到已验证缓存（数据面不依赖这条 TLS），
            // 但日志与心跳里要能看出是身份问题，而不是一条看起来重试就会好的「连不上」。
            throw new MyProxyException(ErrorCodeMessages.Get(ErrorCode.ServerUntrusted), ErrorCode.ServerUntrusted, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new MyProxyException(ErrorCodeMessages.Get(ErrorCode.ApiUnreachable), ErrorCode.ApiUnreachable, ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new MyProxyException(ErrorCodeMessages.Get(ErrorCode.ApiUnreachable), ErrorCode.ApiUnreachable, ex);
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
                "TokenInvalid" => ErrorCode.TokenInvalid,
                "DeviceNotFound" => ErrorCode.TokenInvalid,
                "ServerError" => ErrorCode.ApiUnreachable,
                "RateLimited" => ErrorCode.ApiUnreachable,
                _ => ErrorCode.Unknown
            };
        }

        return response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests => ErrorCode.ApiUnreachable,
            _ when (int)response.StatusCode >= 500 => ErrorCode.ApiUnreachable,
            _ => ErrorCode.Unknown
        };
    }

    private sealed class ConfigResponse
    {
        [JsonPropertyName("configVersion")]
        public long ConfigVersion { get; set; }

        [JsonPropertyName("config")]
        public ServerProfile? Config { get; set; }
    }
}
