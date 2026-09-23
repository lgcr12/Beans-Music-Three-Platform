using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Beans.Windows.Rebuild.Services.BeansAccount;
using Beans.Windows.Rebuild.Services.Security;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class BeansAccountServiceTests
{
    [Fact]
    public async Task RegistrationAndVerificationKeepPasswordAndTokensOutOfMetadata()
    {
        var requests = new List<(string Path, string Body)>();
        using var http = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            requests.Add((request.RequestUri!.AbsolutePath, body));
            return request.RequestUri.AbsolutePath switch
            {
                "/api/v1/auth/register/start" => Json(HttpStatusCode.Accepted,
                    """{"registrationId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","expiresAt":"2030-01-01T00:00:00Z","developmentCode":"123456"}"""),
                "/api/v1/auth/verify-email" => Json(HttpStatusCode.OK,
                    """{"account":{"id":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","nickname":"Beans User","email":"user@example.com","createdAt":"2026-01-01T00:00:00Z"},"wrappedVaultKey":"unused","accessToken":"access-secret","accessTokenExpiresAt":"2030-01-01T00:00:00Z","refreshToken":"refresh-secret","refreshTokenExpiresAt":"2031-01-01T00:00:00Z"}"""),
                _ => Json(HttpStatusCode.NotFound, "{}")
            };
        }));
        var credentials = new MemoryCredentialStore();
        var metadata = new MemoryMetadataStore(new BeansAccountMetadata(DeviceId: Guid.Parse("11111111-1111-1111-1111-111111111111")));
        using var service = new BeansAccountService(http, credentials, metadata, new MemorySyncStore(), new FixedCryptography());
        var cancellationToken = TestContext.Current.CancellationToken;

        var started = await service.StartRegistrationAsync(new BeansRegistrationRequest(
            new Uri("https://beans.example/api"), "Beans User", "user@example.com", "plain-password", "Desktop"), cancellationToken);
        var verified = await service.VerifyEmailAsync("123456", cancellationToken);

        Assert.True(started.IsSuccess);
        Assert.Equal("123456", started.DevelopmentVerificationCode);
        Assert.True(verified.IsSuccess);
        Assert.Equal(BeansAccountState.SignedIn, verified.Snapshot.State);
        Assert.All(requests, request => Assert.DoesNotContain("plain-password", request.Body, StringComparison.Ordinal));
        Assert.Equal("access-secret", await credentials.ReadAsync("beans", "access-token", cancellationToken));
        Assert.Equal("refresh-secret", await credentials.ReadAsync("beans", "refresh-token", cancellationToken));
        Assert.NotNull(await credentials.ReadAsync("beans", "vault-key", cancellationToken));
        var serializedMetadata = JsonSerializer.Serialize(metadata.Value);
        Assert.DoesNotContain("access-secret", serializedMetadata, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-secret", serializedMetadata, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-password", serializedMetadata, StringComparison.Ordinal);
        Assert.Equal("https://beans.example/api/", metadata.Value.Server!.ToString());
    }

    [Fact]
    public async Task LoginUsesChallengeAndRejectsMalformedWrappedVaultKeySafely()
    {
        using var http = new HttpClient(new DelegateHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("challenge", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, """{"cryptoProfile":{"algorithm":"argon2id-v1","salt":"AAAAAAAAAAAAAAAAAAAAAA==","memoryKiB":32768,"iterations":2,"parallelism":1}}""")
                : Json(HttpStatusCode.OK, """{"account":{"id":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","nickname":"User","email":"user@example.com","createdAt":"2026-01-01T00:00:00Z"},"wrappedVaultKey":"not-base64","accessToken":"a","accessTokenExpiresAt":"2030-01-01T00:00:00Z","refreshToken":"r","refreshTokenExpiresAt":"2031-01-01T00:00:00Z"}"""))));
        using var service = CreateService(http);

        var result = await service.LoginAsync(new BeansLoginRequest(
            new Uri("https://beans.example/"), "user@example.com", "password", "Desktop"),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(BeansAccountState.Error, result.Snapshot.State);
        Assert.DoesNotContain("base64", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not-base64", result.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncRefreshesOnceAndStoresOnlyEncryptedPayload()
    {
        var refreshCalls = 0;
        var syncAuthorization = new List<string?>();
        string? pushedBody = null;
        using var http = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth/refresh", StringComparison.Ordinal))
            {
                refreshCalls++;
                return Json(HttpStatusCode.OK,
                    """{"accessToken":"fresh-access","accessTokenExpiresAt":"2030-01-01T00:00:00Z","refreshToken":"fresh-refresh","refreshTokenExpiresAt":"2031-01-01T00:00:00Z"}""");
            }
            if (request.Method == HttpMethod.Get && path.EndsWith("/sync/", StringComparison.Ordinal))
            {
                syncAuthorization.Add(request.Headers.Authorization?.Parameter);
                if (request.Headers.Authorization?.Parameter == "old-access")
                    return Json(HttpStatusCode.Unauthorized, "{}");
                return Json(HttpStatusCode.OK, """{"cursor":0,"hasMore":false,"records":[]}""");
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/sync/batch", StringComparison.Ordinal))
            {
                syncAuthorization.Add(request.Headers.Authorization?.Parameter);
                pushedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                var array = JsonNode.Parse(pushedBody)!.AsArray();
                array[0]!["revision"] = 1;
                return Json(HttpStatusCode.OK, array.ToJsonString());
            }
            return Json(HttpStatusCode.NotFound, "{}");
        }));
        var credentials = SignedInCredentials();
        var metadata = SignedInMetadata();
        var syncStore = new MemorySyncStore();
        using var service = new BeansAccountService(http, credentials, metadata, syncStore, new FixedCryptography());
        var cancellationToken = TestContext.Current.CancellationToken;

        await service.EnqueueAsync("favorite", "track-1", new { title = "private-title" }, cancellationToken: cancellationToken);
        var result = await service.SyncAsync(cancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, refreshCalls);
        Assert.Equal("old-access", syncAuthorization[0]);
        Assert.All(syncAuthorization.Skip(1), value => Assert.Equal("fresh-access", value));
        Assert.NotNull(pushedBody);
        Assert.DoesNotContain("private-title", pushedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("private-title", JsonSerializer.Serialize(syncStore.Value), StringComparison.Ordinal);
        Assert.Empty(syncStore.Value.Pending);
        Assert.Single(syncStore.Value.Mirror);
        Assert.Equal(1, syncStore.Value.Cursor);
    }

    [Fact]
    public async Task VaultPayloadIsEncryptedBeforeTransportAndCanBeReadBack()
    {
        string? storedCiphertext = null;
        long storedVersion = 0;
        using var http = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return storedCiphertext is null
                    ? Json(HttpStatusCode.NotFound, "{}")
                    : Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                    {
                        version = storedVersion,
                        ciphertext = storedCiphertext,
                        updatedAt = DateTimeOffset.UtcNow
                    }));
            }

            var node = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            storedVersion = node["version"]!.GetValue<long>();
            storedCiphertext = node["ciphertext"]!.GetValue<string>();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        using var service = new BeansAccountService(http, SignedInCredentials(), SignedInMetadata(), new MemorySyncStore(), new FixedCryptography());
        var cancellationToken = TestContext.Current.CancellationToken;

        await service.SaveVaultAsync(new VaultPayload("private-vault-value"), cancellationToken);
        var loaded = await service.LoadVaultAsync<VaultPayload>(cancellationToken);

        Assert.Equal(1, storedVersion);
        Assert.NotNull(storedCiphertext);
        Assert.DoesNotContain("private-vault-value", storedCiphertext, StringComparison.Ordinal);
        Assert.Equal("private-vault-value", loaded!.Value);
    }

    [Fact]
    public async Task InvalidServerNeverStartsNetworkOrPersistsSecrets()
    {
        var requests = 0;
        using var http = new HttpClient(new DelegateHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(Json(HttpStatusCode.OK, "{}"));
        }));
        var credentials = new MemoryCredentialStore();
        using var service = new BeansAccountService(http, credentials, new MemoryMetadataStore(), new MemorySyncStore(), new FixedCryptography());

        var result = await service.StartRegistrationAsync(new BeansRegistrationRequest(
            new Uri("file:///tmp/beans"), "User Name", "user@example.com", "password1", "Desktop"),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(0, requests);
        Assert.Empty(credentials.Values);
    }

    [Fact]
    public async Task CancellationIsNotConvertedToAFalseSuccessOrSafeError()
    {
        using var http = new HttpClient(new DelegateHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Json(HttpStatusCode.OK, "{}");
        }));
        using var service = CreateService(http);
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StartRegistrationAsync(
            new BeansRegistrationRequest(new Uri("https://beans.example/"), "User Name", "user@example.com", "password1", "Desktop"),
            cancellation.Token));
    }

    [Fact]
    public void AesGcmRejectsModifiedCiphertext()
    {
        var crypto = new BeansAccountCryptography();
        var key = Enumerable.Repeat((byte)7, 32).ToArray();
        var envelope = crypto.Encrypt("secret"u8, key);
        envelope[13] ^= 0x40;

        Assert.ThrowsAny<CryptographicException>(() => crypto.Decrypt(envelope, key));
    }

    [Fact]
    public async Task Argon2idDerivationProducesSeparatedFixedLengthKeys()
    {
        var crypto = new BeansAccountCryptography();
        var profile = new BeansCryptoProfile(
            "argon2id-v1",
            Convert.ToBase64String(Enumerable.Repeat((byte)5, 16).ToArray()),
            32_768,
            2,
            1);

        var keys = await crypto.DeriveAsync("test-password", profile, TestContext.Current.CancellationToken);

        Assert.Equal(32, keys.AuthSecret.Length);
        Assert.Equal(32, keys.VaultWrappingKey.Length);
        Assert.False(keys.AuthSecret.SequenceEqual(keys.VaultWrappingKey));
        CryptographicOperations.ZeroMemory(keys.AuthSecret);
        CryptographicOperations.ZeroMemory(keys.VaultWrappingKey);
    }

    private static BeansAccountService CreateService(HttpClient http) => new(
        http,
        new MemoryCredentialStore(),
        new MemoryMetadataStore(new BeansAccountMetadata(DeviceId: Guid.Parse("11111111-1111-1111-1111-111111111111"))),
        new MemorySyncStore(),
        new FixedCryptography());

    private static MemoryMetadataStore SignedInMetadata() => new(new BeansAccountMetadata(
        new Uri("https://beans.example/"),
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "Desktop",
        AccountId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
        DisplayName: "User",
        Email: "user@example.com"));

    private static MemoryCredentialStore SignedInCredentials(bool expiredAccess = false)
    {
        var values = new Dictionary<(string, string), string>
        {
            [("beans", "access-token")] = "old-access",
            [("beans", "access-expires-at")] = (expiredAccess ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow.AddHours(1)).ToString("O"),
            [("beans", "refresh-token")] = "old-refresh",
            [("beans", "refresh-expires-at")] = DateTimeOffset.UtcNow.AddDays(1).ToString("O"),
            [("beans", "vault-key")] = Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray())
        };
        return new MemoryCredentialStore(values);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed record VaultPayload(string Value);

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request, cancellationToken);
    }

    private sealed class MemoryCredentialStore : ISecureCredentialStore
    {
        public MemoryCredentialStore(Dictionary<(string, string), string>? values = null) => Values = values ?? [];
        public Dictionary<(string Platform, string Name), string> Values { get; }

        public Task SaveAsync(string platformId, string secretName, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Values[(platformId, secretName)] = value;
            return Task.CompletedTask;
        }

        public Task<string?> ReadAsync(string platformId, string secretName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Values.TryGetValue((platformId, secretName), out var value) ? value : null);
        }

        public Task DeletePlatformAsync(string platformId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var key in Values.Keys.Where(key => key.Platform == platformId).ToArray()) Values.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryMetadataStore(BeansAccountMetadata? initial = null) : IBeansAccountMetadataStore
    {
        public BeansAccountMetadata Value { get; private set; } = initial ?? new BeansAccountMetadata(DeviceId: Guid.Parse("11111111-1111-1111-1111-111111111111"));
        public Task<BeansAccountMetadata> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Value);
        }
        public Task WriteAsync(BeansAccountMetadata metadata, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Value = metadata;
            return Task.CompletedTask;
        }
    }

    private sealed class MemorySyncStore : IBeansEncryptedSyncStore
    {
        public BeansLocalSyncState Value { get; private set; } = new(0, [], []);
        public Task<BeansLocalSyncState> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Value);
        }
        public Task WriteAsync(BeansLocalSyncState state, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Value = state;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedCryptography : IBeansAccountCryptography
    {
        private readonly BeansAccountCryptography _aes = new();
        public Task<BeansDerivedKeys> DeriveAsync(string password, BeansCryptoProfile profile, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new BeansDerivedKeys(Enumerable.Repeat((byte)1, 32).ToArray(), Enumerable.Repeat((byte)2, 32).ToArray()));
        }
        public byte[] RandomBytes(int count) => Enumerable.Repeat((byte)3, count).ToArray();
        public byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key) => _aes.Encrypt(plaintext, key);
        public byte[] Decrypt(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> key) => _aes.Decrypt(envelope, key);
    }
}
