using System.Globalization;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Details;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.Platforms.NetEase.Dto;

namespace Beans.Windows.Rebuild.Services.Platforms.NetEase;

public sealed class NetEaseMusicDetailAdapter : IOnlineMusicDetailAdapter
{
    private static readonly Uri PublicReferer = new("https://music.163.com/");
    private readonly IPlatformHttpClient _httpClient;
    private readonly IPlatformJsonSerializer _serializer;

    public NetEaseMusicDetailAdapter(IPlatformHttpClientFactory httpClientFactory, IPlatformJsonSerializer serializer)
    {
        _httpClient = httpClientFactory.Get("netease");
        _serializer = serializer;
    }

    public PlatformId Platform => PlatformId.NetEaseMusic;
    public bool Supports(OnlineMusicDetailKind kind) => kind is OnlineMusicDetailKind.Playlist or OnlineMusicDetailKind.Ranking
        or OnlineMusicDetailKind.Album or OnlineMusicDetailKind.Artist;

    public async Task<OnlineMusicDetailResponse> GetDetailAsync(OnlineMusicDetailQuery query, CancellationToken cancellationToken)
    {
        if (query.Platform != Platform || !OnlineMusicDetailValidator.IsValid(query.Platform, query.Kind, query.NativeId))
            return OnlineMusicDetailResponse.Failure(OnlineMusicDetailState.InvalidRequest, "网易云音乐详情标识无效", PlatformErrorCode.InvalidRequest);

        return query.Kind switch
        {
            OnlineMusicDetailKind.Playlist or OnlineMusicDetailKind.Ranking => await GetPlaylistAsync(query, cancellationToken),
            OnlineMusicDetailKind.Album => await GetAlbumAsync(query, cancellationToken),
            OnlineMusicDetailKind.Artist => await GetArtistAsync(query, cancellationToken),
            _ => OnlineMusicDetailResponse.Failure(OnlineMusicDetailState.Unsupported, "网易云音乐当前不支持此详情类型", PlatformErrorCode.Unsupported)
        };
    }

    private async Task<OnlineMusicDetailResponse> GetPlaylistAsync(OnlineMusicDetailQuery query, CancellationToken cancellationToken)
    {
        var parser = new JsonPlatformResponseParser<NetEasePlaylistDetailEnvelope, OnlineMusicDetailContent>(
            _serializer, envelope => ParsePlaylist(envelope, query));
        var response = await _httpClient.SendAsync(
            () => CreatePost("https://music.163.com/weapi/v6/playlist/detail", new Dictionary<string, object?>
            {
                ["id"] = query.NativeId, ["n"] = 1000, ["s"] = 8
            }), Context(query, cancellationToken), parser);
        return ToDetailResponse(response);
    }

    private async Task<OnlineMusicDetailResponse> GetAlbumAsync(OnlineMusicDetailQuery query, CancellationToken cancellationToken)
    {
        var parser = new JsonPlatformResponseParser<NetEaseAlbumDetailEnvelope, OnlineMusicDetailContent>(
            _serializer, envelope => ParseAlbum(envelope, query));
        var response = await _httpClient.SendAsync(
            () => CreatePost($"https://music.163.com/weapi/v1/album/{query.NativeId}", new Dictionary<string, object?>
            {
                ["offset"] = 0, ["total"] = true, ["limit"] = 1000
            }), Context(query, cancellationToken), parser);
        return ToDetailResponse(response);
    }

    private async Task<OnlineMusicDetailResponse> GetArtistAsync(OnlineMusicDetailQuery query, CancellationToken cancellationToken)
    {
        var parser = new JsonPlatformResponseParser<NetEaseArtistDetailEnvelope, OnlineMusicDetailContent>(
            _serializer, envelope => ParseArtist(envelope, query));
        var response = await _httpClient.SendAsync(
            () => CreatePost($"https://music.163.com/weapi/v1/artist/{query.NativeId}", new Dictionary<string, object?>()),
            Context(query, cancellationToken), parser);
        return ToDetailResponse(response);
    }

    private static PlatformParseResult<OnlineMusicDetailContent> ParsePlaylist(NetEasePlaylistDetailEnvelope envelope, OnlineMusicDetailQuery query)
    {
        var business = BusinessError<OnlineMusicDetailContent>(envelope.Code, "网易云音乐歌单详情请求失败");
        if (business is not null) return business.Value;
        var playlist = envelope.Playlist;
        if (playlist?.Id is null or <= 0 || string.IsNullOrWhiteSpace(playlist.Name) || playlist.Tracks is null)
            return PlatformParseResult<OnlineMusicDetailContent>.Failure(PlatformErrorCode.InvalidResponse, "网易云音乐歌单详情缺少必需字段");
        var tracks = MapTracks(playlist.Tracks);
        if (tracks is null) return PlatformParseResult<OnlineMusicDetailContent>.Failure(PlatformErrorCode.InvalidResponse, "网易云音乐歌单包含无效曲目");
        var subtitle = FirstNonEmpty(playlist.Creator?.Nickname, query.Kind == OnlineMusicDetailKind.Ranking ? "网易云音乐公开榜单" : "网易云音乐公开歌单");
        return PlatformParseResult<OnlineMusicDetailContent>.Success(Content(query, playlist.Name!, subtitle,
            playlist.Description, playlist.CoverImageUrl, tracks));
    }

    private static PlatformParseResult<OnlineMusicDetailContent> ParseAlbum(NetEaseAlbumDetailEnvelope envelope, OnlineMusicDetailQuery query)
    {
        var business = BusinessError<OnlineMusicDetailContent>(envelope.Code, "网易云音乐专辑详情请求失败");
        if (business is not null) return business.Value;
        var album = envelope.Album;
        if (album?.Id is null or <= 0 || string.IsNullOrWhiteSpace(album.Name) || envelope.Songs is null)
            return PlatformParseResult<OnlineMusicDetailContent>.Failure(PlatformErrorCode.InvalidResponse, "网易云音乐专辑详情缺少必需字段");
        var tracks = MapTracks(envelope.Songs);
        if (tracks is null) return PlatformParseResult<OnlineMusicDetailContent>.Failure(PlatformErrorCode.InvalidResponse, "网易云音乐专辑包含无效曲目");
        var date = FormatDate(album.PublishTime);
        var subtitle = string.Join(" · ", new[] { album.Artist?.Name?.Trim(), date }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return PlatformParseResult<OnlineMusicDetailContent>.Success(Content(query, album.Name!, subtitle,
            album.Description, album.PictureUrl, tracks));
    }

    private static PlatformParseResult<OnlineMusicDetailContent> ParseArtist(NetEaseArtistDetailEnvelope envelope, OnlineMusicDetailQuery query)
    {
        var business = BusinessError<OnlineMusicDetailContent>(envelope.Code, "网易云音乐歌手详情请求失败");
        if (business is not null) return business.Value;
        var artist = envelope.Artist;
        if (artist?.Id is null or <= 0 || string.IsNullOrWhiteSpace(artist.Name) || envelope.HotSongs is null)
            return PlatformParseResult<OnlineMusicDetailContent>.Failure(PlatformErrorCode.InvalidResponse, "网易云音乐歌手详情缺少必需字段");
        var tracks = MapTracks(envelope.HotSongs);
        if (tracks is null) return PlatformParseResult<OnlineMusicDetailContent>.Failure(PlatformErrorCode.InvalidResponse, "网易云音乐歌手热门歌曲包含无效曲目");
        return PlatformParseResult<OnlineMusicDetailContent>.Success(Content(query, artist.Name!, "网易云音乐歌手",
            artist.BriefDescription, FirstNonEmpty(artist.PictureUrl, artist.AvatarUrl), tracks));
    }

    private static OnlineMusicDetailContent Content(OnlineMusicDetailQuery query, string title, string subtitle,
        string? description, string? cover, IReadOnlyList<SearchResultItem> tracks) =>
        new(PlatformId.NetEaseMusic, query.Kind, query.NativeId, title.Trim(), subtitle.Trim(), description?.Trim() ?? string.Empty,
            NormalizeImage(cover), tracks, [], SearchDataOrigin.Live, DateTimeOffset.UtcNow);

    private static IReadOnlyList<SearchResultItem>? MapTracks(IReadOnlyList<NetEaseDetailTrackDto> values)
    {
        if (values.Any(value => value.Id is null or <= 0 || string.IsNullOrWhiteSpace(value.Name))) return null;
        return values.Select(value =>
        {
            var id = value.Id!.Value.ToString(CultureInfo.InvariantCulture);
            var artists = value.Artists ?? value.LegacyArtists ?? [];
            var artist = string.Join(" / ", artists.Select(item => item.Name?.Trim()).Where(name => !string.IsNullOrWhiteSpace(name)));
            if (artist.Length == 0) artist = "未知歌手";
            var album = value.Album ?? value.LegacyAlbum;
            var duration = value.DurationMilliseconds ?? value.LegacyDurationMilliseconds;
            return new SearchResultItem(SearchResultType.Track, PlatformId.NetEaseMusic, id, $"netease:track:{id}", value.Name!.Trim(),
                Subtitle: $"{artist} · {FirstNonEmpty(album?.Name, "未知专辑")}", Artist: artist,
                Album: FirstNonEmpty(album?.Name, "未知专辑"), CoverUri: NormalizeImage(album?.PictureUrl),
                Duration: duration is > 0 ? TimeSpan.FromMilliseconds(duration.Value) : null,
                Quality: Quality(value), IsPlayable: false, RestrictionState: "需要网易云音乐授权并解析播放地址",
                SourceDisplayName: "网易云音乐", SourceBadgeText: "网易云音乐", DataOrigin: SearchDataOrigin.Live,
                PayloadReference: $"netease:track:{id}");
        }).ToArray();
    }

    private static string Quality(NetEaseDetailTrackDto track)
    {
        if (track.Lossless?.Bitrate is > 0) return "SQ";
        if (track.High?.Bitrate is > 0) return $"{track.High.Bitrate.Value / 1000}K";
        if (track.Medium?.Bitrate is > 0) return $"{track.Medium.Bitrate.Value / 1000}K";
        if (track.Low?.Bitrate is > 0) return $"{track.Low.Bitrate.Value / 1000}K";
        return "未知";
    }

    private static HttpRequestMessage CreatePost(string endpoint, IReadOnlyDictionary<string, object?> payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Referrer = PublicReferer;
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 BeansMusic-Windows/1.0");
        request.Content = NetEaseWeapi.CreateContent(payload);
        return request;
    }

    private static PlatformRequestContext Context(OnlineMusicDetailQuery query, CancellationToken cancellationToken) =>
        PlatformRequestContext.PublicDiscovery("netease", $"netease.detail.{query.Kind.ToString().ToLowerInvariant()}",
            $"netease:detail:{query.Kind.ToString().ToLowerInvariant()}:{query.NativeId}", cancellationToken);

    private static OnlineMusicDetailResponse ToDetailResponse(PlatformResponse<OnlineMusicDetailContent> response)
    {
        if (response.IsSuccess && response.Value is not null) return OnlineMusicDetailResponse.Success(response.Value);
        var state = response.Error?.Code switch
        {
            PlatformErrorCode.Unsupported => OnlineMusicDetailState.Unsupported,
            PlatformErrorCode.Unauthorized or PlatformErrorCode.CredentialExpired => OnlineMusicDetailState.Unauthorized,
            PlatformErrorCode.NotFound => OnlineMusicDetailState.Empty,
            _ => OnlineMusicDetailState.Error
        };
        return OnlineMusicDetailResponse.Failure(state, response.Error?.SafeMessage ?? "网易云音乐详情暂时不可用", response.Error?.Code);
    }

    private static PlatformParseResult<T>? BusinessError<T>(int? code, string message)
    {
        if (code == 200) return null;
        if (code is null) return PlatformParseResult<T>.Failure(PlatformErrorCode.InvalidResponse, "网易云音乐详情缺少业务状态");
        return PlatformParseResult<T>.Failure(code is 301 or 302 or 401 ? PlatformErrorCode.Unauthorized : code == 404 ? PlatformErrorCode.NotFound : PlatformErrorCode.ServiceUnavailable, message);
    }

    private static string NormalizeImage(string? raw)
    {
        var value = raw?.Trim().Replace("\\/", "/") ?? string.Empty;
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) value = "https://" + value[7..];
        return Uri.TryCreate(value, UriKind.Absolute, out _) ? value : "ms-appx:///Assets/Branding/beans-icon.png";
    }

    private static string FormatDate(long? timestamp)
    {
        if (timestamp is not > 0) return string.Empty;
        try { return DateTimeOffset.FromUnixTimeMilliseconds(timestamp.Value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        catch (ArgumentOutOfRangeException) { return string.Empty; }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
