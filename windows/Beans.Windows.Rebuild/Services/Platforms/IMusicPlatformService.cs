using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Platforms;

public interface IMusicPlatformService
{
    PlatformId PlatformId { get; }
    string DisplayName { get; }
    CredentialState LoginState { get; }
    MusicPlatformDescriptor Descriptor { get; }

    Task<SearchResult> SearchAsync(string query, int page, int pageSize, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetSearchSuggestionsAsync(string query, CancellationToken cancellationToken);
    Task<PlatformHomeContent> GetHomeContentAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<MusicTrack>> GetDailyRecommendationsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<RankingList>> GetRankingsAsync(CancellationToken cancellationToken);
    Task<RankingDetail> GetRankingDetailAsync(MusicIdentity identity, CancellationToken cancellationToken);
    Task<PlaylistDetail> GetPlaylistAsync(MusicIdentity identity, CancellationToken cancellationToken);
    Task<MusicAlbum> GetAlbumAsync(MusicIdentity identity, CancellationToken cancellationToken);
    Task<MusicArtist> GetArtistAsync(MusicIdentity identity, CancellationToken cancellationToken);
    Task<IReadOnlyList<MusicTrack>> GetArtistTracksAsync(MusicIdentity identity, CancellationToken cancellationToken);
    Task<IReadOnlyList<MusicAlbum>> GetArtistAlbumsAsync(MusicIdentity identity, CancellationToken cancellationToken);
    Task<LyricDocument> GetLyricsAsync(MusicIdentity identity, CancellationToken cancellationToken);
    Task<IReadOnlyList<PlatformComment>> GetCommentsAsync(MusicIdentity identity, int page, CancellationToken cancellationToken);
    Task<PlaybackSource> GetPlaybackUrlAsync(MusicIdentity identity, AudioQuality requestedQuality, CancellationToken cancellationToken);
    Task<IReadOnlyList<AudioQuality>> GetAvailableQualitiesAsync(MusicIdentity identity, CancellationToken cancellationToken);
    Task<PlatformAccount?> GetUserProfileAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<MusicPlaylist>> GetUserPlaylistsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<MusicTrack>> GetFavoriteTracksAsync(CancellationToken cancellationToken);
    Task<MusicPlaylist> CreatePlaylistAsync(string name, CancellationToken cancellationToken);
    Task AddTrackToPlaylistAsync(MusicIdentity playlist, MusicIdentity track, CancellationToken cancellationToken);
    Task RemoveTrackFromPlaylistAsync(MusicIdentity playlist, MusicIdentity track, CancellationToken cancellationToken);
    Task FavoriteTrackAsync(MusicIdentity track, CancellationToken cancellationToken);
    Task UnfavoriteTrackAsync(MusicIdentity track, CancellationToken cancellationToken);
    Task<CredentialState> RefreshCredentialStateAsync(CancellationToken cancellationToken);
    Task LogoutAsync(CancellationToken cancellationToken);
}
