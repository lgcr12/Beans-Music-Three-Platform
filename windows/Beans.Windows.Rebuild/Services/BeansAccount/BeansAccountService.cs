using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;
using Beans.Windows.Rebuild.Services.Security;

namespace Beans.Windows.Rebuild.Services.BeansAccount;

public sealed class BeansAccountService : IBeansAccountService, IBeansSyncService, IBeansVaultService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly HashSet<string> SupportedEntityTypes = new(StringComparer.Ordinal)
    {
        "playlist", "playlistItem", "favorite", "history", "playback", "theme", "preference", "platformMirror"
    };

    private readonly ISecureCredentialStore _credentials;
    private readonly IBeansAccountMetadataStore _metadata;
    private readonly IBeansEncryptedSyncStore _syncStore;
    private readonly IBeansAccountCryptography _cryptography;
    private readonly BeansAccountApiClient _api;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BeansAccountService(
        HttpClient httpClient,
        ISecureCredentialStore credentials,
        IBeansAccountMetadataStore? metadataStore = null,
        IBeansEncryptedSyncStore? syncStore = null,
        IBeansAccountCryptography? cryptography = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _metadata = metadataStore ?? new JsonBeansAccountMetadataStore();
        _syncStore = syncStore ?? new JsonBeansEncryptedSyncStore();
        _cryptography = cryptography ?? new BeansAccountCryptography();
        _api = new BeansAccountApiClient(httpClient, credentials);
    }

    public async Task<BeansAccountSnapshot> GetStateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var metadata = await EnsureDeviceAsync(await _metadata.ReadAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            return await BuildSnapshotAsync(metadata, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BeansAccountActionResult> StartRegistrationAsync(
        BeansRegistrationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var validation = ValidateRegistration(request);
            if (validation is not null) return await FailureAsync(validation, cancellationToken).ConfigureAwait(false);

            var server = NormalizeServer(request.Server);
            var metadata = await EnsureDeviceAsync(await _metadata.ReadAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            var deviceId = metadata.DeviceId == Guid.Empty ? Guid.NewGuid() : metadata.DeviceId;
            var profile = BeansAccountCryptography.CreateRegistrationProfile();
            var keys = await _cryptography.DeriveAsync(request.Password, profile, cancellationToken).ConfigureAwait(false);
            var vaultKey = _cryptography.RandomBytes(32);
            byte[]? wrappedVaultKey = null;
            try
            {
                wrappedVaultKey = _cryptography.Encrypt(vaultKey, keys.VaultWrappingKey);
                var pending = await _api.SendPublicAsync<BeansPendingRegistration>(
                    server,
                    HttpMethod.Post,
                    "v1/auth/register/start",
                    new
                    {
                        nickname = request.Nickname.Trim(),
                        email = request.Email.Trim(),
                        authSecret = Convert.ToBase64String(keys.AuthSecret),
                        cryptoProfile = profile,
                        wrappedVaultKey = Convert.ToBase64String(wrappedVaultKey),
                        device = new BeansDeviceInput(deviceId, request.DeviceName.Trim(), "windows")
                    },
                    cancellationToken).ConfigureAwait(false);

                await _credentials.SaveAsync(
                    BeansCredentialNames.Platform,
                    BeansCredentialNames.PendingVaultKey,
                    Convert.ToBase64String(vaultKey),
                    cancellationToken).ConfigureAwait(false);
                metadata = metadata with
                {
                    Server = server,
                    DeviceId = deviceId,
                    DeviceName = request.DeviceName.Trim(),
                    PendingRegistrationId = pending.RegistrationId,
                    PendingRegistrationExpiresAt = pending.ExpiresAt,
                    AccountId = null,
                    DisplayName = request.Nickname.Trim(),
                    Email = request.Email.Trim(),
                    LastSyncedAt = null,
                    SyncCursor = 0
                };
                await _metadata.WriteAsync(metadata, cancellationToken).ConfigureAwait(false);
                var snapshot = new BeansAccountSnapshot(
                    BeansAccountState.PendingVerification,
                    null,
                    metadata.DisplayName,
                    metadata.Email,
                    null,
                    "验证码已发送，请在有效期内完成验证");
                return new BeansAccountActionResult(true, snapshot, snapshot.SafeMessage, pending.DevelopmentCode);
            }
            catch (BeansApiException exception)
            {
                return await FailureAsync(exception.SafeMessage, cancellationToken).ConfigureAwait(false);
            }
            catch (CryptographicException)
            {
                return await FailureAsync("无法建立安全账号会话，请重试", cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(keys.AuthSecret);
                CryptographicOperations.ZeroMemory(keys.VaultWrappingKey);
                CryptographicOperations.ZeroMemory(vaultKey);
                if (wrappedVaultKey is not null) CryptographicOperations.ZeroMemory(wrappedVaultKey);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BeansAccountActionResult> VerifyEmailAsync(
        string verificationCode,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (verificationCode is null || verificationCode.Length != 6 || verificationCode.Any(ch => ch is < '0' or > '9'))
                return await FailureAsync("请输入 6 位数字验证码", cancellationToken).ConfigureAwait(false);

            var metadata = await _metadata.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (metadata.Server is null || metadata.PendingRegistrationId is null)
                return await FailureAsync("没有等待验证的 Beans 注册", cancellationToken).ConfigureAwait(false);
            var pendingVaultRaw = await _credentials.ReadAsync(
                BeansCredentialNames.Platform,
                BeansCredentialNames.PendingVaultKey,
                cancellationToken).ConfigureAwait(false);
            if (!TryDecodeKey(pendingVaultRaw, out var pendingVaultKey))
                return await FailureAsync("注册密钥已丢失，请重新开始注册", cancellationToken).ConfigureAwait(false);

            try
            {
                var session = await _api.SendPublicAsync<BeansAuthSession>(
                    metadata.Server,
                    HttpMethod.Post,
                    "v1/auth/verify-email",
                    new { registrationId = metadata.PendingRegistrationId.Value, code = verificationCode },
                    cancellationToken).ConfigureAwait(false);
                return await CompleteSignInAsync(metadata, session, pendingVaultKey, cancellationToken).ConfigureAwait(false);
            }
            catch (BeansApiException exception)
            {
                return await FailureAsync(exception.SafeMessage, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pendingVaultKey);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BeansAccountActionResult> LoginAsync(
        BeansLoginRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var validation = ValidateLogin(request);
            if (validation is not null) return await FailureAsync(validation, cancellationToken).ConfigureAwait(false);
            var server = NormalizeServer(request.Server);
            var metadata = await EnsureDeviceAsync(await _metadata.ReadAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            var deviceId = metadata.DeviceId == Guid.Empty ? Guid.NewGuid() : metadata.DeviceId;
            BeansDerivedKeys? keys = null;
            byte[]? vaultKey = null;
            byte[]? wrappedVaultKey = null;
            try
            {
                var challenge = await _api.SendPublicAsync<BeansAuthChallenge>(
                    server,
                    HttpMethod.Post,
                    "v1/auth/login/challenge",
                    new { email = request.Email.Trim() },
                    cancellationToken).ConfigureAwait(false);
                keys = await _cryptography.DeriveAsync(request.Password, challenge.CryptoProfile, cancellationToken).ConfigureAwait(false);
                var session = await _api.SendPublicAsync<BeansAuthSession>(
                    server,
                    HttpMethod.Post,
                    "v1/auth/login",
                    new
                    {
                        email = request.Email.Trim(),
                        authSecret = Convert.ToBase64String(keys.AuthSecret),
                        device = new BeansDeviceInput(deviceId, request.DeviceName.Trim(), "windows")
                    },
                    cancellationToken).ConfigureAwait(false);
                wrappedVaultKey = Convert.FromBase64String(session.WrappedVaultKey);
                vaultKey = _cryptography.Decrypt(wrappedVaultKey, keys.VaultWrappingKey);
                if (vaultKey.Length != 32) throw new CryptographicException("保险库密钥长度无效");
                metadata = metadata with { Server = server, DeviceId = deviceId, DeviceName = request.DeviceName.Trim() };
                return await CompleteSignInAsync(metadata, session, vaultKey, cancellationToken).ConfigureAwait(false);
            }
            catch (BeansApiException exception)
            {
                return await FailureAsync(exception.SafeMessage, cancellationToken).ConfigureAwait(false);
            }
            catch (FormatException)
            {
                return await FailureAsync("Beans 服务返回的保险库密钥无效", cancellationToken).ConfigureAwait(false);
            }
            catch (CryptographicException)
            {
                return await FailureAsync("邮箱或密码错误，无法解锁账号数据", cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (keys is not null)
                {
                    CryptographicOperations.ZeroMemory(keys.AuthSecret);
                    CryptographicOperations.ZeroMemory(keys.VaultWrappingKey);
                }
                if (vaultKey is not null) CryptographicOperations.ZeroMemory(vaultKey);
                if (wrappedVaultKey is not null) CryptographicOperations.ZeroMemory(wrappedVaultKey);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BeansAccountActionResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var metadata = await _metadata.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (metadata.Server is null || metadata.AccountId is null)
                return await FailureAsync("请先登录 Beans 账号", cancellationToken).ConfigureAwait(false);

            try
            {
                var state = await _syncStore.ReadAsync(cancellationToken).ConfigureAwait(false);
                state = await PullAllAsync(metadata.Server, state, cancellationToken).ConfigureAwait(false);

                var conflicts = 0;
                while (state.Pending.Count > 0)
                {
                    try
                    {
                        var batch = state.Pending.Take(200).ToArray();
                        var acknowledged = await _api.SendAuthorizedAsync<IReadOnlyList<BeansSyncEnvelope>>(
                            metadata.Server,
                            HttpMethod.Post,
                            "v1/sync/batch",
                            batch,
                            cancellationToken).ConfigureAwait(false);
                        foreach (var envelope in acknowledged) ValidateEnvelope(envelope);
                        var acknowledgedIds = acknowledged.Select(item => item.Id).ToHashSet();
                        state = state with
                        {
                            Cursor = Math.Max(state.Cursor, acknowledged.Count == 0 ? state.Cursor : acknowledged.Max(item => item.Revision)),
                            Pending = state.Pending.Where(item => !acknowledgedIds.Contains(item.Id)).ToArray(),
                            Mirror = MergeMirror(state.Mirror, acknowledged)
                        };
                        await _syncStore.WriteAsync(state, cancellationToken).ConfigureAwait(false);
                        conflicts = 0;
                    }
                    catch (BeansApiException exception) when (exception.StatusCode == HttpStatusCode.Conflict && conflicts++ < 2)
                    {
                        state = await PullAllAsync(metadata.Server, state, cancellationToken).ConfigureAwait(false);
                    }
                }

                metadata = metadata with { LastSyncedAt = DateTimeOffset.UtcNow, SyncCursor = state.Cursor };
                await _metadata.WriteAsync(metadata, cancellationToken).ConfigureAwait(false);
                var snapshot = await BuildSnapshotAsync(metadata, cancellationToken).ConfigureAwait(false);
                return new BeansAccountActionResult(true, snapshot, "Beans 数据同步完成");
            }
            catch (BeansApiException exception)
            {
                return await FailureAsync(exception.SafeMessage, cancellationToken).ConfigureAwait(false);
            }
            catch (CryptographicException)
            {
                return await FailureAsync("同步数据校验失败，未写入本地数据", cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var metadata = await _metadata.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (metadata.Server is not null && metadata.AccountId is not null)
            {
                try
                {
                    await _api.SendAuthorizedEmptyAsync(metadata.Server, HttpMethod.Post, "v1/auth/logout", null, cancellationToken).ConfigureAwait(false);
                }
                catch (BeansApiException)
                {
                    // Local logout still completes when the server is unavailable.
                }
            }

            await _credentials.DeletePlatformAsync(BeansCredentialNames.Platform, cancellationToken).ConfigureAwait(false);
            await _metadata.WriteAsync(metadata with
            {
                PendingRegistrationId = null,
                PendingRegistrationExpiresAt = null,
                AccountId = null,
                DisplayName = "",
                Email = "",
                LastSyncedAt = null,
                SyncCursor = 0
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EnqueueAsync<T>(
        string entityType,
        string entityId,
        T payload,
        bool deleted = false,
        CancellationToken cancellationToken = default)
    {
        ValidateEntity(entityType, entityId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var metadata = await _metadata.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (metadata.AccountId is null) throw new InvalidOperationException("请先登录 Beans 账号");
            var key = await ReadVaultKeyAsync(cancellationToken).ConfigureAwait(false);
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
            try
            {
                var encrypted = _cryptography.Encrypt(plaintext, key);
                try
                {
                    var state = await _syncStore.ReadAsync(cancellationToken).ConfigureAwait(false);
                    var baseRevision = state.Mirror
                        .Where(item => item.EntityType == entityType && item.EntityId == entityId)
                        .Select(item => item.Revision)
                        .DefaultIfEmpty(0)
                        .Max();
                    var pending = state.Pending
                        .Where(item => item.EntityType != entityType || item.EntityId != entityId)
                        .Append(new BeansSyncEnvelope(
                            Guid.NewGuid(), entityType, entityId, metadata.DeviceId, baseRevision, 0, deleted,
                            Convert.ToBase64String(encrypted), DateTimeOffset.UtcNow))
                        .ToArray();
                    await _syncStore.WriteAsync(state with { Pending = pending }, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(encrypted);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(key);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<T?> ReadAsync<T>(string entityType, string entityId, CancellationToken cancellationToken = default)
    {
        ValidateEntity(entityType, entityId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _syncStore.ReadAsync(cancellationToken).ConfigureAwait(false);
            var record = state.Pending.LastOrDefault(item => item.EntityType == entityType && item.EntityId == entityId)
                ?? state.Mirror.Where(item => item.EntityType == entityType && item.EntityId == entityId).MaxBy(item => item.Revision);
            if (record is null || record.Deleted) return default;
            ValidateEnvelope(record);
            var key = await ReadVaultKeyAsync(cancellationToken).ConfigureAwait(false);
            var encrypted = Convert.FromBase64String(record.Ciphertext);
            try
            {
                var plaintext = _cryptography.Decrypt(encrypted, key);
                try { return JsonSerializer.Deserialize<T>(plaintext, JsonOptions); }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveVaultAsync<T>(T payload, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var metadata = await RequireSignedInMetadataAsync(cancellationToken).ConfigureAwait(false);
            var key = await ReadVaultKeyAsync(cancellationToken).ConfigureAwait(false);
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
            try
            {
                var encrypted = _cryptography.Encrypt(plaintext, key);
                try
                {
                    var existing = await _api.TrySendAuthorizedAsync<BeansVaultEnvelope>(
                        metadata.Server!, HttpMethod.Get, "v1/vault/", null, cancellationToken).ConfigureAwait(false);
                    var version = existing.StatusCode == HttpStatusCode.NotFound ? 1 : (existing.Value?.Version ?? 0) + 1;
                    await _api.SendAuthorizedEmptyAsync(
                        metadata.Server!, HttpMethod.Put, "v1/vault/",
                        new BeansVaultEnvelope(version, Convert.ToBase64String(encrypted), DateTimeOffset.UtcNow),
                        cancellationToken).ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(encrypted); }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<T?> LoadVaultAsync<T>(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var metadata = await RequireSignedInMetadataAsync(cancellationToken).ConfigureAwait(false);
            var response = await _api.TrySendAuthorizedAsync<BeansVaultEnvelope>(
                metadata.Server!, HttpMethod.Get, "v1/vault/", null, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound || response.Value is null) return default;
            var key = await ReadVaultKeyAsync(cancellationToken).ConfigureAwait(false);
            var encrypted = Convert.FromBase64String(response.Value.Ciphertext);
            try
            {
                var plaintext = _cryptography.Decrypt(encrypted, key);
                try { return JsonSerializer.Deserialize<T>(plaintext, JsonOptions); }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<BeansAccountActionResult> CompleteSignInAsync(
        BeansAccountMetadata metadata,
        BeansAuthSession session,
        byte[] vaultKey,
        CancellationToken cancellationToken)
    {
        await _api.SaveTokensAsync(session.Tokens, cancellationToken).ConfigureAwait(false);
        await _credentials.SaveAsync(
            BeansCredentialNames.Platform,
            BeansCredentialNames.VaultKey,
            Convert.ToBase64String(vaultKey),
            cancellationToken).ConfigureAwait(false);
        await _credentials.SaveAsync(
            BeansCredentialNames.Platform,
            BeansCredentialNames.PendingVaultKey,
            string.Empty,
            cancellationToken).ConfigureAwait(false);
        metadata = metadata with
        {
            PendingRegistrationId = null,
            PendingRegistrationExpiresAt = null,
            AccountId = session.Account.Id,
            DisplayName = session.Account.Nickname,
            Email = session.Account.Email,
            LastSyncedAt = null,
            SyncCursor = 0
        };
        await _metadata.WriteAsync(metadata, cancellationToken).ConfigureAwait(false);
        var snapshot = new BeansAccountSnapshot(
            BeansAccountState.SignedIn,
            metadata.AccountId,
            metadata.DisplayName,
            metadata.Email,
            null,
            "Beans 账号已登录");
        return new BeansAccountActionResult(true, snapshot, snapshot.SafeMessage);
    }

    private async Task<BeansLocalSyncState> PullAllAsync(
        Uri server,
        BeansLocalSyncState state,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var page = await _api.SendAuthorizedAsync<BeansSyncPage>(
                server,
                HttpMethod.Get,
                $"v1/sync/?cursor={Math.Max(0, state.Cursor)}",
                null,
                cancellationToken).ConfigureAwait(false);
            foreach (var envelope in page.Records) ValidateEnvelope(envelope);
            var mirror = MergeMirror(state.Mirror, page.Records);
            state = state with
            {
                Cursor = Math.Max(state.Cursor, page.Cursor),
                Mirror = mirror,
                Pending = RebasePending(state.Pending, mirror)
            };
            await _syncStore.WriteAsync(state, cancellationToken).ConfigureAwait(false);
            if (!page.HasMore) return state;
        }
    }

    private static IReadOnlyList<BeansSyncEnvelope> MergeMirror(
        IReadOnlyList<BeansSyncEnvelope> current,
        IReadOnlyList<BeansSyncEnvelope> incoming)
    {
        var merged = current.ToDictionary(item => (item.EntityType, item.EntityId));
        foreach (var item in incoming)
        {
            var key = (item.EntityType, item.EntityId);
            if (!merged.TryGetValue(key, out var existing) || item.Revision > existing.Revision) merged[key] = item;
        }
        return merged.Values.OrderBy(item => item.EntityType).ThenBy(item => item.EntityId).ToArray();
    }

    private static IReadOnlyList<BeansSyncEnvelope> RebasePending(
        IReadOnlyList<BeansSyncEnvelope> pending,
        IReadOnlyList<BeansSyncEnvelope> mirror)
    {
        var revisions = mirror.ToDictionary(item => (item.EntityType, item.EntityId), item => item.Revision);
        return pending.Select(item => revisions.TryGetValue((item.EntityType, item.EntityId), out var revision)
                ? item with { BaseRevision = revision }
                : item)
            .ToArray();
    }

    private async Task<BeansAccountMetadata> RequireSignedInMetadataAsync(CancellationToken cancellationToken)
    {
        var metadata = await _metadata.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (metadata.Server is null || metadata.AccountId is null) throw new InvalidOperationException("请先登录 Beans 账号");
        return metadata;
    }

    private async Task<byte[]> ReadVaultKeyAsync(CancellationToken cancellationToken)
    {
        var raw = await _credentials.ReadAsync(BeansCredentialNames.Platform, BeansCredentialNames.VaultKey, cancellationToken).ConfigureAwait(false);
        if (!TryDecodeKey(raw, out var key)) throw new CryptographicException("Beans 保险库密钥不可用");
        return key;
    }

    private async Task<BeansAccountMetadata> EnsureDeviceAsync(BeansAccountMetadata metadata, CancellationToken cancellationToken)
    {
        if (metadata.DeviceId != Guid.Empty) return metadata;
        metadata = metadata with { DeviceId = Guid.NewGuid() };
        await _metadata.WriteAsync(metadata, cancellationToken).ConfigureAwait(false);
        return metadata;
    }

    private async Task<BeansAccountSnapshot> BuildSnapshotAsync(BeansAccountMetadata metadata, CancellationToken cancellationToken)
    {
        if (metadata.AccountId is not null)
        {
            var refresh = await _credentials.ReadAsync(BeansCredentialNames.Platform, BeansCredentialNames.RefreshToken, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(refresh))
                return new BeansAccountSnapshot(BeansAccountState.SignedIn, metadata.AccountId, metadata.DisplayName, metadata.Email, metadata.LastSyncedAt, "Beans 账号已登录");
        }
        if (metadata.PendingRegistrationId is not null)
            return new BeansAccountSnapshot(BeansAccountState.PendingVerification, null, metadata.DisplayName, metadata.Email, null, "等待邮箱验证");
        return new BeansAccountSnapshot(BeansAccountState.SignedOut, null, "", "", null, "尚未登录 Beans 账号");
    }

    private async Task<BeansAccountActionResult> FailureAsync(string message, CancellationToken cancellationToken)
    {
        var metadata = await _metadata.ReadAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = new BeansAccountSnapshot(
            BeansAccountState.Error,
            metadata.AccountId,
            metadata.DisplayName,
            metadata.Email,
            metadata.LastSyncedAt,
            message);
        return new BeansAccountActionResult(false, snapshot, message);
    }

    private static string? ValidateRegistration(BeansRegistrationRequest request)
    {
        if (!TryNormalizeServer(request.Server, out _)) return "请输入有效的 Beans 服务地址（http 或 https）";
        if (request.Nickname.Trim().Length is < 2 or > 40) return "昵称长度应为 2 到 40 个字符";
        if (!IsEmail(request.Email)) return "邮箱格式无效";
        if (request.Password.Length < 8) return "密码至少需要 8 个字符";
        if (request.DeviceName.Trim().Length is < 1 or > 80) return "设备名称长度应为 1 到 80 个字符";
        return null;
    }

    private static string? ValidateLogin(BeansLoginRequest request)
    {
        if (!TryNormalizeServer(request.Server, out _)) return "请输入有效的 Beans 服务地址（http 或 https）";
        if (!IsEmail(request.Email)) return "邮箱格式无效";
        if (string.IsNullOrEmpty(request.Password)) return "请输入密码";
        if (request.DeviceName.Trim().Length is < 1 or > 80) return "设备名称长度应为 1 到 80 个字符";
        return null;
    }

    private static bool IsEmail(string value)
    {
        try { return new MailAddress(value.Trim()).Address == value.Trim(); }
        catch (FormatException) { return false; }
    }

    private static Uri NormalizeServer(Uri server) =>
        TryNormalizeServer(server, out var normalized)
            ? normalized
            : throw new ArgumentException("Beans 服务地址无效", nameof(server));

    private static bool TryNormalizeServer(Uri? server, out Uri normalized)
    {
        normalized = null!;
        if (server is null || !server.IsAbsoluteUri || server.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(server.UserInfo)) return false;
        var builder = new UriBuilder(server) { Query = "", Fragment = "" };
        if (!builder.Path.EndsWith('/')) builder.Path += "/";
        normalized = builder.Uri;
        return true;
    }

    private static bool TryDecodeKey(string? value, out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            key = Convert.FromBase64String(value);
            if (key.Length == 32) return true;
            CryptographicOperations.ZeroMemory(key);
            key = [];
            return false;
        }
        catch (FormatException) { return false; }
    }

    private static void ValidateEntity(string entityType, string entityId)
    {
        if (!SupportedEntityTypes.Contains(entityType)) throw new ArgumentException("不支持的 Beans 同步实体类型", nameof(entityType));
        if (string.IsNullOrWhiteSpace(entityId) || entityId.Length > 256) throw new ArgumentException("Beans 同步实体标识无效", nameof(entityId));
    }

    private static void ValidateEnvelope(BeansSyncEnvelope envelope)
    {
        ValidateEntity(envelope.EntityType, envelope.EntityId);
        if (envelope.Id == Guid.Empty || envelope.DeviceId == Guid.Empty || envelope.Revision < 0 || envelope.BaseRevision < 0)
            throw new CryptographicException("Beans 同步记录无效");
        try
        {
            if (Convert.FromBase64String(envelope.Ciphertext).Length < 28)
                throw new CryptographicException("Beans 同步密文无效");
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("Beans 同步密文无效", exception);
        }
    }
}
