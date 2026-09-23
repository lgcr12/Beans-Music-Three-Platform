namespace Beans.Windows.Rebuild.Models;

public sealed record PlatformAccountState(
    PlatformId Platform,
    CredentialState CredentialState,
    string DisplayName,
    string SafeMessage,
    DateTimeOffset CheckedAt,
    bool CanAuthorize);
