using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Beans.Core;

public sealed class BeansApiClient(HttpClient httpClient)
{
    private readonly HttpClient _http = httpClient;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private TokenPair? _tokens;
    private Uri? _server;
    public event Action<TokenPair>? TokensChanged;

    public void Configure(Uri server) => _server = server;
    public void RestoreTokens(TokenPair tokens) => _tokens = tokens;
    public void ClearTokens() => _tokens = null;

    public Task<PendingRegistration> StartRegistrationAsync(string nickname, string email, byte[] authSecret, CryptoProfile profile, byte[] wrappedVaultKey, DeviceInput device, CancellationToken ct = default) =>
        SendAsync<PendingRegistration>(HttpMethod.Post, "v1/auth/register/start", new { nickname, email, authSecret = Convert.ToBase64String(authSecret), cryptoProfile = profile, wrappedVaultKey = Convert.ToBase64String(wrappedVaultKey), device }, false, ct);

    public Task<AuthSession> VerifyEmailAsync(Guid registrationId, string code, CancellationToken ct = default) =>
        SendAsync<AuthSession>(HttpMethod.Post, "v1/auth/verify-email", new { registrationId, code }, false, ct);

    public Task<AuthChallenge> LoginChallengeAsync(string email, CancellationToken ct = default) =>
        SendAsync<AuthChallenge>(HttpMethod.Post, "v1/auth/login/challenge", new { email }, false, ct);

    public Task<AuthSession> LoginAsync(string email, byte[] authSecret, DeviceInput device, CancellationToken ct = default) =>
        SendAsync<AuthSession>(HttpMethod.Post, "v1/auth/login", new { email, authSecret = Convert.ToBase64String(authSecret), device }, false, ct);

    public Task<VaultEnvelope> GetVaultAsync(CancellationToken ct = default) => SendAsync<VaultEnvelope>(HttpMethod.Get, "v1/vault/", null, true, ct);
    public Task PutVaultAsync(VaultEnvelope envelope, CancellationToken ct = default) => SendEmptyAsync(HttpMethod.Put, "v1/vault/", envelope, true, ct);
    public Task<SyncPage> PullSyncAsync(long cursor, CancellationToken ct = default) => SendAsync<SyncPage>(HttpMethod.Get, $"v1/sync/?cursor={cursor}", null, true, ct);
    public Task<IReadOnlyList<SyncEnvelope>> PushSyncAsync(IReadOnlyList<SyncEnvelope> records, CancellationToken ct = default) => SendAsync<IReadOnlyList<SyncEnvelope>>(HttpMethod.Post, "v1/sync/batch", records, true, ct);
    public Task<IReadOnlyList<DeviceRecord>> ListDevicesAsync(CancellationToken ct = default) => SendAsync<IReadOnlyList<DeviceRecord>>(HttpMethod.Get, "v1/devices/", null, true, ct);
    public Task RevokeDeviceAsync(Guid deviceId, CancellationToken ct = default) => SendEmptyAsync(HttpMethod.Delete, $"v1/devices/{deviceId}", null, true, ct);
    public Task LogoutAsync(CancellationToken ct = default) => SendEmptyAsync(HttpMethod.Post, "v1/auth/logout", null, true, ct);
    public async Task<PasswordResetStartResult> StartPasswordResetAsync(string email, CancellationToken ct = default)
    {
        using var response = await SendResponseAsync(HttpMethod.Post, "v1/auth/password/reset/start", new { email }, false, ct);
        var data = await response.Content.ReadAsByteArrayAsync(ct);
        return data.Length == 0
            ? new PasswordResetStartResult(null)
            : JsonSerializer.Deserialize<PasswordResetStartResult>(data, JsonOptions.Default) ?? new PasswordResetStartResult(null);
    }
    public Task CompletePasswordResetAsync(string email, string code, byte[] authSecret, CryptoProfile profile, byte[] wrappedVaultKey, CancellationToken ct = default) =>
        SendEmptyAsync(HttpMethod.Post, "v1/auth/password/reset/complete", new { email, code, authSecret = Convert.ToBase64String(authSecret), cryptoProfile = profile, wrappedVaultKey = Convert.ToBase64String(wrappedVaultKey) }, false, ct);

    public Task<QrSession> CreateQrSessionAsync(DeviceInput device, byte[] publicKey, byte[] exchangeSecret, CancellationToken ct = default) =>
        SendAsync<QrSession>(HttpMethod.Post, "v1/qr-sessions/", new { device, ephemeralPublicKey = Convert.ToBase64String(publicKey), exchangeSecret = Convert.ToBase64String(exchangeSecret) }, false, ct);
    public Task<QrSession> GetQrSessionAsync(Guid id, CancellationToken ct = default) => SendAsync<QrSession>(HttpMethod.Get, $"v1/qr-sessions/{id}", null, false, ct);
    public Task ApproveQrSessionAsync(Guid id, string verificationCode, byte[] encryptedVaultKey, CancellationToken ct = default) =>
        SendEmptyAsync(HttpMethod.Post, $"v1/qr-sessions/{id}/approve", new { verificationCode, encryptedVaultKey = Convert.ToBase64String(encryptedVaultKey) }, true, ct);
    public Task<QrExchangeResult> ExchangeQrSessionAsync(QrLoginContext context, DeviceInput device, CancellationToken ct = default) =>
        SendAsync<QrExchangeResult>(HttpMethod.Post, $"v1/qr-sessions/{context.Session.Id}/exchange", new { device, ephemeralPublicKey = Convert.ToBase64String(AccountCrypto.PublicKeyFromPrivate(context.PrivateKey)), exchangeSecret = Convert.ToBase64String(context.ExchangeSecret) }, false, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, bool authorized, CancellationToken ct, bool retried = false)
    {
        using var request = CreateRequest(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions.Default);
        if (authorized && _tokens is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens.AccessToken);
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized && authorized && !retried && await RefreshAsync(ct))
            return await SendAsync<T>(method, path, body, true, ct, true);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions.Default, ct) ?? throw new InvalidDataException("服务器返回为空");
    }

    private async Task SendEmptyAsync(HttpMethod method, string path, object? body, bool authorized, CancellationToken ct, bool retried = false)
    {
        using var request = CreateRequest(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions.Default);
        if (authorized && _tokens is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens.AccessToken);
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized && authorized && !retried && await RefreshAsync(ct))
        {
            await SendEmptyAsync(method, path, body, true, ct, true);
            return;
        }
        await EnsureSuccessAsync(response, ct);
    }

    private async Task<HttpResponseMessage> SendResponseAsync(HttpMethod method, string path, object? body, bool authorized, CancellationToken ct, bool retried = false)
    {
        using var request = CreateRequest(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions.Default);
        if (authorized && _tokens is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens.AccessToken);
        var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized && authorized && !retried && await RefreshAsync(ct))
        {
            response.Dispose();
            return await SendResponseAsync(method, path, body, true, ct, true);
        }
        try { await EnsureSuccessAsync(response, ct); return response; }
        catch { response.Dispose(); throw; }
    }

    private async Task<bool> RefreshAsync(CancellationToken ct)
    {
        if (_tokens is null) return false;
        await _refreshLock.WaitAsync(ct);
        try
        {
            var refresh = _tokens.RefreshToken;
            _tokens = await SendAsync<TokenPair>(HttpMethod.Post, "v1/auth/refresh", new { refreshToken = refresh }, false, ct);
            TokensChanged?.Invoke(_tokens);
            return true;
        }
        finally { _refreshLock.Release(); }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path) =>
        new(method, new Uri(_server ?? throw new InvalidOperationException("尚未配置 Beans 服务地址"), path));

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var problem = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"Beans 服务请求失败 ({(int)response.StatusCode})：{problem}", null, response.StatusCode);
    }
}

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}
