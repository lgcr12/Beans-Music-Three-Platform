using Microsoft.Extensions.Logging;
using Windows.Security.Credentials;

namespace Beans.Windows.Rebuild.Services.Security;

public sealed class CredentialLockerStore(ILogger<CredentialLockerStore> logger) : ISecureCredentialStore
{
    private const string ResourcePrefix = "BeansMusic.Rebuild";

    public Task SaveAsync(string platformId, string secretName, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var vault = new PasswordVault();
        var resource = Resource(platformId);
        try
        {
            var existing = vault.Retrieve(resource, secretName);
            vault.Remove(existing);
        }
        catch
        {
            // The credential does not exist yet. Secret values are never logged.
        }
        vault.Add(new PasswordCredential(resource, secretName, value));
        logger.LogInformation("Secure credential updated for platform {PlatformId}", platformId);
        return Task.CompletedTask;
    }

    public Task<string?> ReadAsync(string platformId, string secretName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var credential = new PasswordVault().Retrieve(Resource(platformId), secretName);
            credential.RetrievePassword();
            return Task.FromResult<string?>(credential.Password);
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }

    public Task DeletePlatformAsync(string platformId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var vault = new PasswordVault();
        try
        {
            foreach (var credential in vault.FindAllByResource(Resource(platformId))) vault.Remove(credential);
        }
        catch
        {
            // PasswordVault throws when the resource has no credentials.
        }
        logger.LogInformation("Secure credentials removed for platform {PlatformId}", platformId);
        return Task.CompletedTask;
    }

    private static string Resource(string platformId) => $"{ResourcePrefix}.{platformId.Trim().ToLowerInvariant()}";
}
