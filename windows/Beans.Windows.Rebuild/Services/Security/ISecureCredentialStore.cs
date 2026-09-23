namespace Beans.Windows.Rebuild.Services.Security;

public interface ISecureCredentialStore
{
    Task SaveAsync(string platformId, string secretName, string value, CancellationToken cancellationToken = default);
    Task<string?> ReadAsync(string platformId, string secretName, CancellationToken cancellationToken = default);
    Task DeletePlatformAsync(string platformId, CancellationToken cancellationToken = default);
}
