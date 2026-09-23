using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Platforms;

public sealed class PreviewMusicPlatformService : IMusicPlatformService
{
    public PreviewMusicPlatformService(MusicPlatformDescriptor descriptor) => Descriptor = descriptor;

    public PlatformId PlatformId => Descriptor.Id;
    public string DisplayName => Descriptor.DisplayName;
    public CredentialState LoginState => CredentialState.NotAuthorized;
    public MusicPlatformDescriptor Descriptor { get; }

    public Task<SearchResult> SearchAsync(string query, int page, int pageSize, CancellationToken cancellationToken) => Unavailable<SearchResult>();
    public Task<IReadOnlyList<string>> GetSearchSuggestionsAsync(string query, CancellationToken cancellationToken) => Unavailable<IReadOnlyList<string>>();
    public Task<PlatformHomeContent> GetHomeContentAsync(CancellationToken cancellationToken) => Unavailable<PlatformHomeContent>();
    public Task<IReadOnlyList<MusicTrack>> GetDailyRecommendationsAsync(CancellationToken cancellationToken) => Unavailable<IReadOnlyList<MusicTrack>>();
    public Task<IReadOnlyList<RankingList>> GetRankingsAsync(CancellationToken cancellationToken) => Unavailable<IReadOnlyList<RankingList>>();
    public Task<RankingDetail> GetRankingDetailAsync(MusicIdentity identity, CancellationToken cancellationToken) => Unavailable<RankingDetail>();
    public Task<PlaylistDetail> GetPlaylistAsync(MusicIdentity identity, CancellationToken cancellationToken) => Unavailable<PlaylistDetail>();
    public Task<MusicAlbum> GetAlbumAsync(MusicIdentity identity, CancellationToken cancellationToken) => Unavailable<MusicAlbum>();
    public Task<MusicArtist> GetArtistAsync(MusicIdentity identity, CancellationToken cancellationToken) => Unavailable<MusicArtist>();
    public Task<IReadOnlyList<MusicTrack>> GetArtistTracksAsync(MusicIdentity identity, CancellationToken cancellationToken) => Unavailable<IReadOnlyList<MusicTrack>>();
    public Task<IReadOnlyList<MusicAlbum>> GetArtistAlbumsAsync(MusicIdentity identity, CancellationToken cancellationToken) => Unavailable<IReadOnlyList<MusicAlbum>>();
    public Task<LyricDocument> GetLyricsAsync(MusicIdentity identity, CancellationToken cancellationToken) => Unavailable<LyricDocument>();
    public Task<IReadOnlyList<PlatformComment>> GetCommentsAsync(MusicIdentity identity, int page, CancellationToken cancellationToken) => Unavailable<IReadOnlyList<PlatformComment>>();
    public Task<PlaybackSource> GetPlaybackUrlAsync(MusicIdentity identity, AudioQuality requestedQuality, CancellationToken cancellationToken) => Unavailable<PlaybackSource>();
    public Task<IReadOnlyList<AudioQuality>> GetAvailableQualitiesAsync(MusicIdentity identity, CancellationToken cancellationToken) => Unavailable<IReadOnlyList<AudioQuality>>();
    public Task<PlatformAccount?> GetUserProfileAsync(CancellationToken cancellationToken) => Task.FromResult<PlatformAccount?>(null);
    public Task<IReadOnlyList<MusicPlaylist>> GetUserPlaylistsAsync(CancellationToken cancellationToken) => Unavailable<IReadOnlyList<MusicPlaylist>>();
    public Task<IReadOnlyList<MusicTrack>> GetFavoriteTracksAsync(CancellationToken cancellationToken) => Unavailable<IReadOnlyList<MusicTrack>>();
    public Task<MusicPlaylist> CreatePlaylistAsync(string name, CancellationToken cancellationToken) => Unavailable<MusicPlaylist>();
    public Task AddTrackToPlaylistAsync(MusicIdentity playlist, MusicIdentity track, CancellationToken cancellationToken) => Unavailable();
    public Task RemoveTrackFromPlaylistAsync(MusicIdentity playlist, MusicIdentity track, CancellationToken cancellationToken) => Unavailable();
    public Task FavoriteTrackAsync(MusicIdentity track, CancellationToken cancellationToken) => Unavailable();
    public Task UnfavoriteTrackAsync(MusicIdentity track, CancellationToken cancellationToken) => Unavailable();
    public Task<CredentialState> RefreshCredentialStateAsync(CancellationToken cancellationToken) => Task.FromResult(CredentialState.NotAuthorized);
    public Task LogoutAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static Task<T> Unavailable<T>() => Task.FromException<T>(new NotSupportedException("真实平台适配器尚未接入"));
    private static Task Unavailable() => Task.FromException(new NotSupportedException("真实平台适配器尚未接入"));
}
