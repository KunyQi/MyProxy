using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using MyProxy.Core;
using MyProxy.Models;

namespace MyProxy.Services;

public sealed class UpdateService : IUpdateService
{
    private const int UpdateCheckTimeoutSeconds = 5;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 本构建认的是哪个平台：公开的 <c>latest.json</c> 路径与 manifest 里的
    /// <c>platform</c>/产物签名类型都由它决定。
    ///
    /// <para>
    /// 取自 <see cref="AppInfo.Platform"/>（每个平台各有一份 <c>AppInfo</c>），
    /// 所以 Windows 端拼出来仍然是 <c>/client/windows/latest.json</c>、
    /// 仍然只认 <c>authenticode</c>，与本类上游行为逐字节一致；Linux 端则自动
    /// 走 <c>/client/linux/latest.json</c> 与 <c>ed25519</c>。
    /// </para>
    /// </summary>
    private static readonly ClientReleasePlatform TargetPlatform =
        ClientReleasePlatform.ForPlatformName(AppInfo.Platform);

    private readonly IApiEndpoint _endpoint;
    private readonly IStorageService? _storage;
    private readonly ILogService? _log;
    private readonly IReadOnlyDictionary<string, byte[]> _signingKeys;

    public UpdateService(
        IApiEndpoint endpoint,
        IStorageService? storage = null,
        ILogService? log = null,
        IReadOnlyDictionary<string, byte[]>? signingKeys = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _storage = storage;
        _log = log;
        _signingKeys = signingKeys ?? ReleaseSigningKeys.Trusted;
    }

    private string ApiBaseUrl => _endpoint.BaseUrl;

    private HttpClient Http
        => _endpoint.Client(nameof(UpdateService), TimeSpan.FromSeconds(UpdateCheckTimeoutSeconds));

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage response = await Http
                .GetAsync($"{ApiBaseUrl}/client/{TargetPlatform.Name}/latest.json", ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new(UpdateCheckStatus.Failed);
            }

            string responseJson = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(responseJson))
            {
                return new(UpdateCheckStatus.Failed);
            }

            UpdateInfo? updateInfo;
            try
            {
                updateInfo = JsonSerializer.Deserialize<UpdateInfo>(responseJson, JsonOptions);
            }
            catch (JsonException)
            {
                return new(UpdateCheckStatus.Failed);
            }

            if (updateInfo is null)
            {
                return new(UpdateCheckStatus.Failed);
            }

            // 带签名 manifest 时以 manifest 为准：latest.json 的四个明文字段是给
            // 老客户端读的便利副本，权威永远是签名覆盖的那段字节。
            if (updateInfo.HasSignedManifest)
            {
                ManifestVerification verification = ReleaseManifestVerifier.Verify(
                    updateInfo.Manifest,
                    updateInfo.Signature,
                    updateInfo.SigningKeyId,
                    _signingKeys,
                    TargetPlatform);

                if (!verification.Ok)
                {
                    _log?.Warn(nameof(UpdateService), $"latest.json manifest rejected: {verification.Rejection}");
                    return new(UpdateCheckStatus.Failed);
                }

                UpdatePlanResult plan = UpdatePlanner.Decide(verification, AppInfo.Version, updateInfo.ReleaseId);
                return plan.Decision switch
                {
                    UpdateDecision.Install => new(
                        UpdateCheckStatus.UpdateAvailable,
                        FromVerifiedManifest(updateInfo, verification.Manifest!)),
                    UpdateDecision.UpToDate => new(UpdateCheckStatus.UpToDate),
                    // 需要中间版本时不谎称「已是最新」，也不提示一个装不上的版本。
                    _ => new(UpdateCheckStatus.Failed)
                };
            }

            // Only a signed manifest may offer an update; unsigned metadata can indicate no update.
            if (!System.Version.TryParse(updateInfo.Version, out Version? remoteVersion) ||
                !System.Version.TryParse(AppInfo.Version, out Version? localVersion))
            {
                return new(UpdateCheckStatus.Failed);
            }

            if (remoteVersion > localVersion)
            {
                _log?.Warn(nameof(UpdateService), "latest.json names a newer version without a signed manifest; not offered");
                return new(UpdateCheckStatus.Failed);
            }

            return new(UpdateCheckStatus.UpToDate);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 后台静默失败，不打扰用户。
            return new(UpdateCheckStatus.Failed);
        }
    }

    /// <summary>
    /// 提示给用户的版本号、下载地址与强制标记一律取自验过签的 manifest。latest.json 的
    /// 四个明文字段只是给老客户端读的副本，签名覆盖不到它们。
    /// </summary>
    private static UpdateInfo FromVerifiedManifest(UpdateInfo response, ReleaseManifest manifest) => new()
    {
        Version = manifest.Version,
        DownloadUrl = manifest.ArtifactUrl,
        Sha256 = manifest.ArtifactSha256,
        Mandatory = manifest.Mandatory,
        ReleaseId = response.ReleaseId,
        Manifest = response.Manifest,
        Signature = response.Signature,
        SigningKeyId = response.SigningKeyId
    };

    public async Task<AssignedUpdate?> FetchAssignedAsync(CancellationToken ct)
    {
        string? token = _storage?.LoadDevice()?.DeviceToken;
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}/api/device/update");
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized || !response.IsSuccessStatusCode)
            {
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            DeviceUpdateResponse? payload = JsonSerializer.Deserialize<DeviceUpdateResponse>(json, JsonOptions);
            if (payload is null)
            {
                return null;
            }

            FeatureFlags flags = FeatureFlags.FromJson(payload.FeatureFlags);
            if (payload.Update is null)
            {
                return new AssignedUpdate(new UpdatePlanResult(UpdateDecision.UpToDate), flags);
            }

            ManifestVerification verification = ReleaseManifestVerifier.Verify(
                payload.Update.Manifest,
                payload.Update.Signature,
                payload.Update.SigningKeyId,
                _signingKeys,
                TargetPlatform);

            UpdatePlanResult plan = UpdatePlanner.Decide(verification, AppInfo.Version, payload.Update.ReleaseId);
            if (plan.Decision == UpdateDecision.Rejected)
            {
                _log?.Warn(
                    nameof(UpdateService),
                    $"Assigned release {payload.Update.ReleaseId} rejected: {plan.Rejection}");
            }

            return new AssignedUpdate(plan, flags, payload.Update.ReleaseId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public async Task ReportInstallAsync(string releaseId, string status, string detail, CancellationToken ct)
    {
        string? token = _storage?.LoadDevice()?.DeviceToken;
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(releaseId) || string.IsNullOrEmpty(status))
        {
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/api/device/update/report");
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            string body = JsonSerializer.Serialize(new
            {
                releaseId,
                status,
                detail = detail.Length > 256 ? detail[..256] : detail
            });
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _log?.Warn(nameof(UpdateService), $"Install report rejected: {(int)response.StatusCode}");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 上报是尽力而为：上报不上去不该让一次成功的安装看起来像失败。
            _log?.Warn(nameof(UpdateService), $"Install report failed: {ex.GetType().Name}");
        }
    }
}
