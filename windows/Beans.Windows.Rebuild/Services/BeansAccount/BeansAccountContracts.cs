namespace Beans.Windows.Rebuild.Services.BeansAccount;

public enum BeansAccountState
{
    SignedOut,
    PendingVerification,
    SignedIn,
    Syncing,
    Error
}

public sealed record BeansAccountSnapshot(
    BeansAccountState State,
    Guid? AccountId,
    string DisplayName,
    string Email,
    DateTimeOffset? LastSyncedAt,
    string SafeMessage);

public sealed record BeansRegistrationRequest(
    Uri Server,
    string Nickname,
    string Email,
    string Password,
    string DeviceName);

public sealed record BeansLoginRequest(
    Uri Server,
    string Email,
    string Password,
    string DeviceName);

public sealed record BeansAccountActionResult(
    bool IsSuccess,
    BeansAccountSnapshot Snapshot,
    string SafeMessage,
    string? DevelopmentVerificationCode = null);

public interface IBeansAccountService
{
    Task<BeansAccountSnapshot> GetStateAsync(CancellationToken cancellationToken = default);
    Task<BeansAccountActionResult> StartRegistrationAsync(BeansRegistrationRequest request, CancellationToken cancellationToken = default);
    Task<BeansAccountActionResult> VerifyEmailAsync(string verificationCode, CancellationToken cancellationToken = default);
    Task<BeansAccountActionResult> LoginAsync(BeansLoginRequest request, CancellationToken cancellationToken = default);
    Task<BeansAccountActionResult> SyncAsync(CancellationToken cancellationToken = default);
    Task LogoutAsync(CancellationToken cancellationToken = default);
}
