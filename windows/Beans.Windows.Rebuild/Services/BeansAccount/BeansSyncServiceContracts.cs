namespace Beans.Windows.Rebuild.Services.BeansAccount;

public interface IBeansSyncService
{
    Task EnqueueAsync<T>(string entityType, string entityId, T payload, bool deleted = false, CancellationToken cancellationToken = default);
    Task<T?> ReadAsync<T>(string entityType, string entityId, CancellationToken cancellationToken = default);
}

public interface IBeansVaultService
{
    Task SaveVaultAsync<T>(T payload, CancellationToken cancellationToken = default);
    Task<T?> LoadVaultAsync<T>(CancellationToken cancellationToken = default);
}
