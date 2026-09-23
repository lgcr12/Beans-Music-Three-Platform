using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Beans.Windows.Rebuild.Services.Security;

namespace Beans.Windows.Rebuild.Services.BeansAccount;

internal sealed class BeansApiException(string safeMessage, HttpStatusCode? statusCode = null, Exception? innerException = null)
    : Exception(safeMessage, innerException)
{
    public string SafeMessage { get; } = safeMessage;
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

internal static class BeansCredentialNames
{
    public const string Platform = "beans";
    public const string AccessToken = "access-token";
    public const string AccessExpiresAt = "access-expires-at";
    public const string RefreshToken = "refresh-token";
    public const string RefreshExpiresAt = "refresh-expires-at";
    public const string VaultKey = "vault-key";
    public const string PendingVaultKey = "pending-vault-key";
}

internal sealed class BeansAccountApiClient(HttpClient http, ISecureCredentialStore credentials)
{
    private sealed record AccessTokenLease(string Token, bool Refreshed);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public async Task<T> SendPublicAsync<T>(
        Uri server,
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(server, method, path, body, null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<T> SendAuthorizedAsync<T>(
        Uri server,
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedResponseAsync(server, method, path, body, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendAuthorizedEmptyAsync(
        Uri server,
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedResponseAsync(server, method, path, body, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(HttpStatusCode StatusCode, T? Value)> TrySendAuthorizedAsync<T>(
        Uri server,
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        var lease = await GetUsableAccessTokenAsync(server, cancellationToken).ConfigureAwait(false);
        using var response = await SendCoreAsync(server, method, path, body, lease.Token, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return (response.StatusCode, default);
        if (response.StatusCode == HttpStatusCode.Unauthorized && !lease.Refreshed)
        {
            var token = await RefreshAsync(server, lease.Token, cancellationToken).ConfigureAwait(false);
            using var retry = await SendCoreAsync(server, method, path, body, token, cancellationToken).ConfigureAwait(false);
            if (retry.StatusCode == HttpStatusCode.NotFound) return (retry.StatusCode, default);
            await EnsureSuccessAsync(retry, cancellationToken).ConfigureAwait(false);
            return (retry.StatusCode, await ReadAsync<T>(retry, cancellationToken).ConfigureAwait(false));
        }
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false));
    }

    public async Task SaveTokensAsync(BeansTokenPair tokens, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tokens.AccessToken) || string.IsNullOrWhiteSpace(tokens.RefreshToken))
            throw new BeansApiException("Beans 服务返回的登录凭证无效");

        await credentials.SaveAsync(BeansCredentialNames.Platform, BeansCredentialNames.AccessToken, tokens.AccessToken, cancellationToken).ConfigureAwait(false);
        await credentials.SaveAsync(BeansCredentialNames.Platform, BeansCredentialNames.AccessExpiresAt, tokens.AccessTokenExpiresAt.ToString("O"), cancellationToken).ConfigureAwait(false);
        await credentials.SaveAsync(BeansCredentialNames.Platform, BeansCredentialNames.RefreshToken, tokens.RefreshToken, cancellationToken).ConfigureAwait(false);
        await credentials.SaveAsync(BeansCredentialNames.Platform, BeansCredentialNames.RefreshExpiresAt, tokens.RefreshTokenExpiresAt.ToString("O"), cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAuthorizedResponseAsync(
        Uri server,
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        var lease = await GetUsableAccessTokenAsync(server, cancellationToken).ConfigureAwait(false);
        var response = await SendCoreAsync(server, method, path, body, lease.Token, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized && !lease.Refreshed)
        {
            response.Dispose();
            var token = await RefreshAsync(server, lease.Token, cancellationToken).ConfigureAwait(false);
            response = await SendCoreAsync(server, method, path, body, token, cancellationToken).ConfigureAwait(false);
        }
        try
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async Task<AccessTokenLease> GetUsableAccessTokenAsync(Uri server, CancellationToken cancellationToken)
    {
        var accessToken = await credentials.ReadAsync(BeansCredentialNames.Platform, BeansCredentialNames.AccessToken, cancellationToken).ConfigureAwait(false);
        var expiresRaw = await credentials.ReadAsync(BeansCredentialNames.Platform, BeansCredentialNames.AccessExpiresAt, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken)) throw new BeansApiException("请先登录 Beans 账号", HttpStatusCode.Unauthorized);

        if (!DateTimeOffset.TryParse(expiresRaw, out var expiresAt) || expiresAt <= DateTimeOffset.UtcNow.AddSeconds(30))
            return new AccessTokenLease(await RefreshAsync(server, accessToken, cancellationToken).ConfigureAwait(false), true);
        return new AccessTokenLease(accessToken, false);
    }

    private async Task<string> RefreshAsync(Uri server, string staleAccessToken, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await credentials.ReadAsync(BeansCredentialNames.Platform, BeansCredentialNames.AccessToken, cancellationToken).ConfigureAwait(false);
            var expiresRaw = await credentials.ReadAsync(BeansCredentialNames.Platform, BeansCredentialNames.AccessExpiresAt, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(current)
                && current != staleAccessToken
                && DateTimeOffset.TryParse(expiresRaw, out var currentExpiry)
                && currentExpiry > DateTimeOffset.UtcNow.AddSeconds(30))
            {
                return current;
            }

            var refresh = await credentials.ReadAsync(BeansCredentialNames.Platform, BeansCredentialNames.RefreshToken, cancellationToken).ConfigureAwait(false);
            var refreshExpiryRaw = await credentials.ReadAsync(BeansCredentialNames.Platform, BeansCredentialNames.RefreshExpiresAt, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(refresh)
                || (DateTimeOffset.TryParse(refreshExpiryRaw, out var refreshExpiry) && refreshExpiry <= DateTimeOffset.UtcNow))
            {
                throw new BeansApiException("Beans 登录已过期，请重新登录", HttpStatusCode.Unauthorized);
            }

            var pair = await SendPublicAsync<BeansTokenPair>(
                server,
                HttpMethod.Post,
                "v1/auth/refresh",
                new { refreshToken = refresh },
                cancellationToken).ConfigureAwait(false);
            await SaveTokensAsync(pair, cancellationToken).ConfigureAwait(false);
            return pair.AccessToken;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        Uri server,
        HttpMethod method,
        string path,
        object? body,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(server, path));
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        if (!string.IsNullOrWhiteSpace(bearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BeansApiException("Beans 服务请求超时，请稍后重试");
        }
        catch (HttpRequestException exception)
        {
            throw new BeansApiException("无法连接 Beans 服务，请检查服务地址和网络", null, exception);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new BeansApiException("Beans 服务返回为空");
        }
        catch (JsonException exception)
        {
            throw new BeansApiException("Beans 服务返回了无法识别的数据", response.StatusCode, exception);
        }
    }

    private static Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (response.IsSuccessStatusCode) return Task.CompletedTask;
        var message = response.StatusCode switch
        {
            HttpStatusCode.BadRequest => "提交的信息无效，请检查后重试",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Beans 登录已过期或凭证无效",
            HttpStatusCode.NotFound => "Beans 服务暂时不支持此功能",
            HttpStatusCode.Conflict => "账号或同步数据发生冲突，请刷新后重试",
            HttpStatusCode.Gone => "验证码已过期，请重新获取",
            HttpStatusCode.TooManyRequests => "操作过于频繁，请稍后重试",
            HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout => "Beans 服务暂时不可用，请稍后重试",
            _ => "Beans 服务请求失败，请稍后重试"
        };
        throw new BeansApiException(message, response.StatusCode);
    }
}
