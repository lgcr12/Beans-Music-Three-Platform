using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Beans.Core;

public sealed class BeansSyncCoordinator(BeansApiClient api, SyncOutbox store, Guid userId, Guid deviceId, byte[] vaultKey)
{
    public async Task EnqueueAsync<T>(string entityType, string entityId, T payload, bool deleted = false, CancellationToken ct = default)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions.Default);
        try { await store.EnqueueAsync(userId, entityType, entityId, deleted, AccountCrypto.Encrypt(plaintext, vaultKey), ct); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public async Task<T?> ReadAsync<T>(string entityType, string entityId, CancellationToken ct = default)
    {
        var record = (await store.ReadMirrorAsync(userId, entityType, ct)).FirstOrDefault(x => x.EntityId == entityId && !x.Deleted);
        if (record is null) return default;
        var plaintext = AccountCrypto.Decrypt(Convert.FromBase64String(record.Ciphertext), vaultKey);
        try { return JsonSerializer.Deserialize<T>(plaintext, JsonOptions.Default); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public async Task SyncAsync(CancellationToken ct = default)
    {
        await store.InitializeAsync(ct);
        await PullAllAsync(ct);
        var conflicts = 0;
        while (true)
        {
            var pending = await store.PendingAsync(userId, deviceId, 200, ct);
            if (pending.Count == 0) return;
            try
            {
                await store.AcknowledgeAsync(userId, await api.PushSyncAsync(pending, ct), ct);
                conflicts = 0;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Conflict && conflicts++ < 2)
            {
                await PullAllAsync(ct);
            }
        }
    }

    private async Task PullAllAsync(CancellationToken ct)
    {
        while (true)
        {
            var page = await api.PullSyncAsync((await store.AccountStateAsync(userId, ct)).Cursor, ct);
            await store.IngestAsync(userId, page, ct);
            if (!page.HasMore) return;
        }
    }
}
