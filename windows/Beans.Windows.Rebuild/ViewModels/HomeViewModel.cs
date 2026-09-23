using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Library;
using Beans.Windows.Rebuild.Services.LocalMusic;
using Beans.Windows.Rebuild.Services.Platforms;
using LocalCatalogTrack = Beans.Windows.Rebuild.Services.LocalMusic.LocalTrack;

namespace Beans.Windows.Rebuild.ViewModels;

public sealed class HomeViewModel : INotifyPropertyChanged, IDisposable
{
    private const string DefaultArtwork = "ms-appx:///Assets/Branding/beans-icon.png";
    private readonly IMusicDiscoveryService _discovery;
    private readonly IUserLibraryService _library;
    private readonly ILocalMusicCatalog _localCatalog;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private CancellationTokenSource? _loadCancellation;
    private bool _isInitialized;
    private bool _isLoading;
    private string _heroTitle = "首页正在准备";
    private string _heroSubtitle = "正在读取公开推荐与本机音乐库";
    private string _heroImageUri = DefaultArtwork;
    private string _heroActionLabel = "暂无可打开内容";
    private string _statusText = "准备加载真实内容";
    private string _playlistStatusText = "正在加载公开推荐歌单…";
    private string _trackStatusText = "正在读取最近播放与本地索引…";
    private string _recommendationStatusText = "正在读取平台推荐…";
    private string _favoriteSummaryText = "正在读取本机收藏…";

    public HomeViewModel(
        IMusicDiscoveryService discovery,
        IUserLibraryService library,
        ILocalMusicCatalog localCatalog)
    {
        _discovery = discovery;
        _library = library;
        _localCatalog = localCatalog;
    }

    public ObservableCollection<HomePlaylist> Playlists { get; } = [];
    public ObservableCollection<HomeTrack> RecentTracks { get; } = [];
    public ObservableCollection<HomeRecommendation> Recommendations { get; } = [];

    public string HeroTitle { get => _heroTitle; private set => Set(ref _heroTitle, value); }
    public string HeroSubtitle { get => _heroSubtitle; private set => Set(ref _heroSubtitle, value); }
    public string HeroImageUri { get => _heroImageUri; private set => Set(ref _heroImageUri, value); }
    public string HeroActionLabel { get => _heroActionLabel; private set => Set(ref _heroActionLabel, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string PlaylistStatusText { get => _playlistStatusText; private set => Set(ref _playlistStatusText, value); }
    public string TrackStatusText { get => _trackStatusText; private set => Set(ref _trackStatusText, value); }
    public string RecommendationStatusText { get => _recommendationStatusText; private set => Set(ref _recommendationStatusText, value); }
    public string FavoriteSummaryText { get => _favoriteSummaryText; private set => Set(ref _favoriteSummaryText, value); }
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public HomeRouteTarget? HeroTarget { get; private set; }
    public bool HasHeroTarget => HeroTarget is not null;
    public bool HasPlaylists => Playlists.Count > 0;
    public bool HasTracks => RecentTracks.Count > 0;
    public bool HasRecommendations => Recommendations.Count > 0;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? StateChanged;

    public async Task InitializeAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (_isInitialized && !forceRefresh) return;
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            if (_isInitialized && !forceRefresh) return;
            _loadCancellation?.Cancel();
            _loadCancellation?.Dispose();
            _loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _loadCancellation.Token;
            IsLoading = true;
            StatusText = forceRefresh ? "正在刷新首页真实内容…" : "正在加载首页真实内容…";
            StateChanged?.Invoke(this, EventArgs.Empty);

            var qqTask = LoadDiscoveryAsync("qq", forceRefresh, token);
            var netEaseTask = LoadDiscoveryAsync("netease", forceRefresh, token);
            var libraryTask = LoadLibraryAsync(token);
            var localTask = LoadLocalAsync(token);
            await Task.WhenAll(qqTask, netEaseTask, libraryTask, localTask);
            token.ThrowIfCancellationRequested();

            var discoveryResults = new[] { await qqTask, await netEaseTask };
            var liveContents = discoveryResults
                .Where(result => result.Content is { IsPreview: false } && result.Content.DataOrigin != DiscoveryDataOrigin.Preview)
                .Select(result => result.Content!)
                .ToArray();
            var libraryResult = await libraryTask;
            var localResult = await localTask;

            ApplyHero(liveContents);
            ApplyPlaylists(liveContents);
            ApplyTracks(liveContents, libraryResult.Snapshot, localResult.Tracks);
            ApplyRecommendations(liveContents);
            ApplyLibrarySummary(libraryResult);

            var loadedSources = new List<string>();
            if (liveContents.Any(content => content.PlatformId == "qq")) loadedSources.Add("QQ 音乐");
            if (liveContents.Any(content => content.PlatformId == "netease")) loadedSources.Add("网易云音乐");
            if (libraryResult.Snapshot is not null) loadedSources.Add("本机音乐库");
            if (localResult.Succeeded) loadedSources.Add("本地索引");
            StatusText = loadedSources.Count > 0
                ? $"已加载：{string.Join("、", loadedSources)}"
                : "公开推荐和本机音乐库暂时无法加载，请稍后重试";
            _isInitialized = true;
        }
        finally
        {
            IsLoading = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
            _loadGate.Release();
        }
    }

    private async Task<DiscoveryLoadResult> LoadDiscoveryAsync(string platformId, bool forceRefresh, CancellationToken token)
    {
        try
        {
            var content = await _discovery.GetDiscoveryAsync(
                platformId,
                new DiscoveryRequest(platformId, ForceRefresh: forceRefresh),
                token);
            if (content.IsPreview || content.DataOrigin == DiscoveryDataOrigin.Preview)
                return new(null);
            return new(content);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return new(null); }
    }

    private async Task<LibraryLoadResult> LoadLibraryAsync(CancellationToken token)
    {
        try { return new(await _library.GetSnapshotAsync(token), null); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return new(null, "本机收藏暂时无法读取"); }
    }

    private async Task<LocalLoadResult> LoadLocalAsync(CancellationToken token)
    {
        try
        {
            var recent = await _localCatalog.GetRecentlyPlayedAsync(8, token);
            if (recent.Count == 0) recent = await _localCatalog.GetRecentlyAddedAsync(8, token);
            return new(recent, true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return new([], false); }
    }

    private void ApplyHero(IReadOnlyList<PlatformDiscoveryContent> contents)
    {
        var content = contents.FirstOrDefault(item =>
            !string.IsNullOrWhiteSpace(item.HeroContent.Title) &&
            !string.IsNullOrWhiteSpace(item.HeroContent.ImageUri));
        if (content is null)
        {
            HeroTitle = "暂无公开推荐";
            HeroSubtitle = "网络恢复后可查看 QQ 音乐与网易云音乐的公开内容";
            HeroImageUri = DefaultArtwork;
            HeroActionLabel = "暂无可打开内容";
            HeroTarget = null;
            OnPropertyChanged(nameof(HasHeroTarget));
            return;
        }

        var hero = content.HeroContent;
        HeroTitle = hero.Title;
        HeroSubtitle = hero.Subtitle;
        HeroImageUri = NormalizeArtwork(hero.ImageUri);
        HeroActionLabel = string.IsNullOrWhiteSpace(hero.ActionLabel) ? "查看内容" : hero.ActionLabel;
        HeroTarget = ResolveHeroTarget(content);
        OnPropertyChanged(nameof(HasHeroTarget));
    }

    private static HomeRouteTarget? ResolveHeroTarget(PlatformDiscoveryContent content)
    {
        var nativeId = content.HeroContent.TrackId;
        if (string.IsNullOrWhiteSpace(nativeId) || !PlatformIdExtensions.TryParseStableId(content.PlatformId, out var platform)) return null;
        var title = content.HeroContent.Title;
        if (content.RecommendedPlaylists.Any(item => item.Identity.NativeId == nativeId) ||
            content.PlaylistSquare.Any(item => item.Identity.NativeId == nativeId))
            return new("playlist", new PlatformRouteParameter(platform.ToStableId(), nativeId, title));
        if (content.Rankings.Any(item => item.Id == nativeId))
            return new("ranking", new PlatformRouteParameter(platform.ToStableId(), nativeId, title));
        return null;
    }

    private void ApplyPlaylists(IReadOnlyList<PlatformDiscoveryContent> contents)
    {
        Playlists.Clear();
        var byPlatform = contents.Select(content => content.RecommendedPlaylists
                .Concat(content.PlaylistSquare)
                .Where(item => !string.IsNullOrWhiteSpace(item.Identity.NativeId) && !string.IsNullOrWhiteSpace(item.Title))
                .DistinctBy(item => item.Identity.NativeId)
                .Take(4)
                .ToArray())
            .Where(items => items.Length > 0)
            .ToArray();
        for (var index = 0; Playlists.Count < 4 && byPlatform.Any(items => index < items.Length); index++)
        {
            foreach (var items in byPlatform.Where(items => index < items.Length))
            {
                var item = items[index];
                Playlists.Add(new HomePlaylist(
                    item.Identity.NativeId,
                    item.Title,
                    string.IsNullOrWhiteSpace(item.Creator) ? item.Identity.Platform.ToDisplayName() : item.Creator,
                    NormalizeArtwork(item.CoverUri),
                    FormatPlayCount(item.PlayCount),
                    item.Identity.Platform));
                if (Playlists.Count == 4) break;
            }
        }
        PlaylistStatusText = Playlists.Count > 0
            ? $"来自公开接口 · {Playlists.Count} 个歌单"
            : "QQ 音乐与网易云音乐的公开推荐暂时不可用";
        NotifyCollectionsChanged();
    }

    private void ApplyTracks(
        IReadOnlyList<PlatformDiscoveryContent> contents,
        UserLibrarySnapshot? library,
        IReadOnlyList<LocalCatalogTrack> localTracks)
    {
        RecentTracks.Clear();
        var targets = new List<SearchResultItem>();
        targets.AddRange(localTracks.Select(ToSearchResult));
        if (library is not null)
        {
            targets.AddRange(library.RecentHistory
                .Select(entry => entry.Item.TryCreateSearchResult(out var result) ? result : null)
                .OfType<SearchResultItem>());
        }
        targets.AddRange(contents.SelectMany(content => content.DailyRecommendations).Select(ToSearchResult));

        foreach (var item in targets
                     .Where(item => item.DataOrigin != SearchDataOrigin.Preview)
                     .DistinctBy(item => item.StableId)
                     .Take(8))
        {
            RecentTracks.Add(new HomeTrack(
                item.StableId,
                RecentTracks.Count + 1,
                item.Title,
                string.IsNullOrWhiteSpace(item.Artist) ? "未知歌手" : item.Artist,
                item.Album,
                item.DurationText,
                NormalizeArtwork(item.CoverUri),
                item.Platform,
                item));
        }
        TrackStatusText = RecentTracks.Count > 0
            ? "真实播放记录、本地索引与平台推荐"
            : "还没有播放记录或本地歌曲；账号推荐不可用时不会填充假歌曲";
        NotifyCollectionsChanged();
    }

    private void ApplyRecommendations(IReadOnlyList<PlatformDiscoveryContent> contents)
    {
        Recommendations.Clear();
        foreach (var item in contents.SelectMany(content => content.DailyRecommendations)
                     .Select(ToSearchResult)
                     .Where(item => item.DataOrigin != SearchDataOrigin.Preview)
                     .DistinctBy(item => item.StableId)
                     .Take(5))
        {
            Recommendations.Add(new HomeRecommendation(
                item.StableId,
                item.Title,
                string.IsNullOrWhiteSpace(item.Artist) ? item.Platform.ToDisplayName() : item.Artist,
                NormalizeArtwork(item.CoverUri),
                item.Platform,
                item));
        }
        RecommendationStatusText = Recommendations.Count > 0
            ? "来自已授权平台的真实推荐"
            : "账号每日推荐暂不可用；登录平台后可重试";
        NotifyCollectionsChanged();
    }

    private void ApplyLibrarySummary(LibraryLoadResult result)
    {
        if (result.Snapshot is null)
        {
            FavoriteSummaryText = result.Error ?? "本机收藏暂时无法读取";
            return;
        }
        var count = result.Snapshot.Favorites.Count;
        FavoriteSummaryText = count == 0 ? "本机收藏为空" : $"本机收藏 · {count} 项";
    }

    private static SearchResultItem ToSearchResult(LocalCatalogTrack track) => new(
        SearchResultType.Track,
        PlatformId.Local,
        track.Id,
        $"local:track:{track.Id}",
        track.Title,
        Artist: track.Artist,
        Album: track.Album,
        CoverUri: track.ArtworkUri,
        Duration: track.Duration,
        Quality: track.Format,
        IsPlayable: !track.IsMissing,
        RestrictionState: track.IsMissing ? "文件已移动或删除" : string.Empty,
        SourceDisplayName: "本地音乐",
        SourceBadgeText: "本地",
        DataOrigin: SearchDataOrigin.Live,
        PayloadReference: track.Id,
        PlaybackUri: track.IsMissing ? null : track.DisplayPath);

    private static SearchResultItem ToSearchResult(MusicTrack track)
    {
        var platform = track.Identity.Platform;
        var artist = string.Join(" / ", track.Artists.Select(item => item.Name).Where(value => !string.IsNullOrWhiteSpace(value)));
        return new SearchResultItem(
            SearchResultType.Track,
            platform,
            track.Identity.NativeId,
            $"{platform.ToStableId()}:track:{track.Identity.NativeId}",
            track.Title,
            Artist: artist,
            Album: track.Album?.Title ?? string.Empty,
            CoverUri: track.Cover?.AbsoluteUri ?? track.Album?.Cover?.AbsoluteUri ?? string.Empty,
            Duration: track.Duration,
            Quality: track.BestQuality?.ToString() ?? "未知",
            IsPlayable: false,
            RestrictionState: track.Availability switch
            {
                AvailabilityState.LoginRequired => "需要登录",
                AvailabilityState.SubscriptionRequired => "需要会员权益",
                AvailabilityState.RegionRestricted => "当前地区不可用",
                AvailabilityState.Unavailable => "当前不可播放",
                _ => "播放时解析官方地址"
            },
            SourceDisplayName: platform.ToDisplayName(),
            SourceBadgeText: platform.ToDisplayName(),
            DataOrigin: SearchDataOrigin.Live,
            PayloadReference: $"{platform.ToStableId()}:track:{track.Identity.NativeId}");
    }

    private static string NormalizeArtwork(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Uri.TryCreate(value, UriKind.Absolute, out _) ? value : DefaultArtwork;

    private static string FormatPlayCount(long? count) => count switch
    {
        null => string.Empty,
        >= 100_000_000 => $"{count.Value / 100_000_000d:0.#} 亿",
        >= 10_000 => $"{count.Value / 10_000d:0.#} 万",
        _ => count.Value.ToString()
    };

    private void NotifyCollectionsChanged()
    {
        OnPropertyChanged(nameof(HasPlaylists));
        OnPropertyChanged(nameof(HasTracks));
        OnPropertyChanged(nameof(HasRecommendations));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName!);
    }

    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadGate.Dispose();
    }

    private sealed record DiscoveryLoadResult(PlatformDiscoveryContent? Content);
    private sealed record LibraryLoadResult(UserLibrarySnapshot? Snapshot, string? Error);
    private sealed record LocalLoadResult(IReadOnlyList<LocalCatalogTrack> Tracks, bool Succeeded);
}
