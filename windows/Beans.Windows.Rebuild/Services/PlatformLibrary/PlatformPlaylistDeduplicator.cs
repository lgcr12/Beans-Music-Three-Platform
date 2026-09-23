using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.PlatformLibrary;

internal static class PlatformPlaylistDeduplicator
{
    public static IReadOnlyList<PlatformUserPlaylist> Deduplicate(
        IEnumerable<PlatformUserPlaylist> playlists,
        PlatformId? platformFilter = null)
    {
        return playlists
            .Where(item => platformFilter is null || item.Platform == platformFilter)
            .Where(item => !string.IsNullOrWhiteSpace(item.Title))
            .GroupBy(VisibleIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item.IsFavoriteCollection)
                .ThenByDescending(item => !string.IsNullOrWhiteSpace(item.CoverUri))
                .ThenByDescending(item => item.TrackCount ?? 0)
                .ThenBy(item => item.NativeId, StringComparer.OrdinalIgnoreCase)
                .First())
            .ToArray();
    }

    internal static string VisibleIdentity(PlatformUserPlaylist playlist) =>
        $"{playlist.Platform.ToStableId()}|{NormalizeVisibleKey(playlist.Title)}|{NormalizeVisibleKey(playlist.Creator)}";

    private static string NormalizeVisibleKey(string? value) =>
        string.Join(' ', (value ?? string.Empty)
            .Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
