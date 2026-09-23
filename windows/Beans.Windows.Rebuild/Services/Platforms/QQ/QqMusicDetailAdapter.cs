using System.Net.Http.Json;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Details;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.Platforms.QQ.Dto;

namespace Beans.Windows.Rebuild.Services.Platforms.QQ;

public sealed class QqMusicDetailAdapter : IOnlineMusicDetailAdapter
{
    private static readonly Uri PublicReferer = new("https://y.qq.com/");
    private static readonly Uri MusicuEndpoint = new("https://u.y.qq.com/cgi-bin/musicu.fcg");
    private readonly IPlatformHttpClient _httpClient;
    private readonly IPlatformJsonSerializer _serializer;

    public QqMusicDetailAdapter(IPlatformHttpClientFactory httpClientFactory, IPlatformJsonSerializer serializer)
    {
        _httpClient = httpClientFactory.Get("qq");
        _serializer = serializer;
    }

    public PlatformId Platform => PlatformId.QqMusic;
    public bool Supports(OnlineMusicDetailKind kind) => kind is OnlineMusicDetailKind.Playlist
        or OnlineMusicDetailKind.Ranking or OnlineMusicDetailKind.Artist;

    public async Task<OnlineMusicDetailResponse> GetDetailAsync(OnlineMusicDetailQuery query, CancellationToken cancellationToken)
    {
        if (query.Platform != Platform || !OnlineMusicDetailValidator.IsValid(query.Platform, query.Kind, query.NativeId))
            return OnlineMusicDetailResponse.Failure(OnlineMusicDetailState.InvalidRequest, "QQ 音乐详情标识无效", PlatformErrorCode.InvalidRequest);

        return query.Kind switch
        {
            OnlineMusicDetailKind.Playlist => await GetPlaylistAsync(query, cancellationToken),
            OnlineMusicDetailKind.Ranking => await GetRankingAsync(query, cancellationToken),
            OnlineMusicDetailKind.Artist => await GetArtistAsync(query, cancellationToken),
            OnlineMusicDetailKind.Album => OnlineMusicDetailResponse.Failure(OnlineMusicDetailState.Unsupported,
                "QQ 音乐公开专辑详情协议尚未确认，当前版本不加载该详情", PlatformErrorCode.Unsupported),
            _ => OnlineMusicDetailResponse.Failure(OnlineMusicDetailState.Unsupported, "QQ 音乐当前不支持此详情类型", PlatformErrorCode.Unsupported)
        };
    }

    private async Task<OnlineMusicDetailResponse> GetPlaylistAsync(OnlineMusicDetailQuery query, CancellationToken cancellationToken)
    {
        var parser = new JsonPlatformResponseParser<QqPlaylistDetailEnvelope, OnlineMusicDetailContent>(
            _serializer, envelope => ParsePlaylist(envelope, query));
        var endpoint = new Uri($"https://c.y.qq.com/qzone/fcg-bin/fcg_ucc_getcdinfo_byids_cp.fcg?type=1&json=1&utf8=1&onlysong=0&format=json&disstid={query.NativeId}");
        var response = await _httpClient.SendAsync(() => CreateGet(endpoint), Context(query, cancellationToken), parser);
        return ToDetailResponse(response);
    }

    private async Task<OnlineMusicDetailResponse> GetRankingAsync(OnlineMusicDetailQuery query, CancellationToken cancellationToken)
    {
        var parser = new JsonPlatformResponseParser<QqRankingDetailEnvelope, OnlineMusicDetailContent>(
            _serializer, envelope => ParseRanking(envelope, query));
        var endpoint = new Uri($"https://c.y.qq.com/v8/fcg-bin/fcg_v8_toplist_cp.fcg?topid={query.NativeId}&page=detail&type=top&song_num=100&format=json");
        var response = await _httpClient.SendAsync(() => CreateGet(endpoint), Context(query, cancellationToken), parser);
        return ToDetailResponse(response);
    }

    private async Task<OnlineMusicDetailResponse> GetArtistAsync(OnlineMusicDetailQuery query, CancellationToken cancellationToken)
    {
        var parser = new JsonPlatformResponseParser<QqSingerSongsEnvelope, OnlineMusicDetailContent>(
            _serializer, envelope => ParseArtist(envelope, query));
        var response = await _httpClient.SendAsync(() => CreateArtistRequest(query.NativeId), Context(query, cancellationToken), parser);
        return ToDetailResponse(response);
    }

    private static PlatformParseResult<OnlineMusicDetailContent> ParsePlaylist(QqPlaylistDetailEnvelope envelope, OnlineMusicDetailQuery query)
    {
        var business = BusinessError<OnlineMusicDetailContent>(envelope.Code, "QQ 音乐歌单详情请求失败");
        if (business is not null) return business.Value;
        var playlist = envelope.Playlists?.FirstOrDefault();
        if (playlist is null || string.IsNullOrWhiteSpace(playlist.Name) || playlist.Tracks is null)
            return PlatformParseResult<OnlineMusicDetailContent>.Failure(PlatformErrorCode.InvalidResponse, "QQ 音乐歌单详情缺少必需字段");
        var tracks = MapTracks(playlist.Tracks);
        if (tracks is null) return PlatformParseResult<OnlineMusicDetailContent>.Failure(PlatformErrorCode.InvalidResponse, "QQ 音乐歌单包含无效曲目");
        return PlatformParseResult<OnlineMusicDetailContent>.Success(Content(query, playlist.Name!,
            FirstNonEmpty(playlist.Creator?.Name, "QQ 音乐公开歌单"), playlist.Description, playlist.Logo, tracks));
    }

    private static PlatformParseResult<OnlineMusicDetailContent> ParseRanking(QqRankingDetailEnvelope envelope, OnlineMusicDetailQuery query)
    {
        var business = BusinessError<OnlineMusicDetailContent>(envelope.Code, "QQ 音乐排行榜详情请求失败");
        if (business is not null) return business.Value;
        if (envelope.TopInfo is null || envelope.Songs is null)
            return PlatformParseResult<OnlineMusicDetailContent>.Failure(PlatformErrorCode.InvalidResponse, "QQ 音乐排行榜详情缺少必需字段");
        var rawTracks = envelope.Songs.Select(item => item.Data ?? item.SongInfo).Where(item => item is not null).Cast<QqDetailTrackDto>().ToArray();
        if (rawTracks.Length != envelope.Songs.Count)
            return PlatformParseResult<OnlineMusicDetailContent>.Failure(PlatformErrorCode.InvalidResponse, "QQ 音乐排行榜包含无效曲目");
        var tracks = MapTracks(rawTracks);
        if (tracks is null) return PlatformParseResult<OnlineMusicDetailContent>.Failure(PlatformErrorCode.InvalidResponse, "QQ 音乐排行榜包含无效曲目");
        return PlatformParseResult<OnlineMusicDetailContent>.Success(Content(query,
            FirstNonEmpty(envelope.TopInfo.ListName, query.FallbackTitle, "QQ 音乐排行榜"), "QQ 音乐公开排行榜",
            envelope.TopInfo.Description, FirstNonEmpty(envelope.TopInfo.AlbumPicture, envelope.TopInfo.AlternatePicture), tracks));
    }

    private static PlatformParseResult<OnlineMusicDetailContent> ParseArtist(QqSingerSongsEnvelope envelope, OnlineMusicDetailQuery query)
    {
        var business = BusinessError<OnlineMusicDetailContent>(envelope.Code, "QQ 音乐歌手详情请求失败");
        if (business is not null) return business.Value;
        var requestBusiness = BusinessError<OnlineMusicDetailContent>(envelope.SingerSongList?.Code, "QQ 音乐歌手歌曲请求失败");
        if (requestBusiness is not null) return requestBusiness.Value;
        var artist = envelope.SingerSongList?.Data;
        if (artist?.Songs is null)
            return PlatformParseResult<OnlineMusicDetailContent>.Failure(PlatformErrorCode.InvalidResponse, "QQ 音乐歌手详情缺少必需字段");
        var rawTracks = artist.Songs.Select(item => item.SongInfo).Where(item => item is not null).Cast<QqDetailTrackDto>().ToArray();
        if (rawTracks.Length != artist.Songs.Count)
            return PlatformParseResult<OnlineMusicDetailContent>.Failure(PlatformErrorCode.InvalidResponse, "QQ 音乐歌手热门歌曲包含无效曲目");
        var tracks = MapTracks(rawTracks);
        if (tracks is null) return PlatformParseResult<OnlineMusicDetailContent>.Failure(PlatformErrorCode.InvalidResponse, "QQ 音乐歌手热门歌曲包含无效曲目");
        return PlatformParseResult<OnlineMusicDetailContent>.Success(Content(query,
            FirstNonEmpty(query.FallbackTitle, "QQ 音乐歌手"), "QQ 音乐公开热门歌曲", string.Empty,
            $"https://y.gtimg.cn/music/photo_new/T001R300x300M000{query.NativeId}.jpg", tracks));
    }

    private static OnlineMusicDetailContent Content(OnlineMusicDetailQuery query, string title, string subtitle,
        string? description, string? cover, IReadOnlyList<SearchResultItem> tracks) =>
        new(PlatformId.QqMusic, query.Kind, query.NativeId, title.Trim(), subtitle.Trim(), description?.Trim() ?? string.Empty,
            NormalizeImage(cover), tracks, [], SearchDataOrigin.Live, DateTimeOffset.UtcNow);

    private static IReadOnlyList<SearchResultItem>? MapTracks(IReadOnlyList<QqDetailTrackDto> values)
    {
        if (values.Any(value => !IsValidTrackMid(FirstNonEmpty(value.Mid, value.SongMid)) ||
                                string.IsNullOrWhiteSpace(FirstNonEmpty(value.Name, value.SongName)))) return null;
        return values.Select(value =>
        {
            var id = FirstNonEmpty(value.Mid, value.SongMid);
            var artist = string.Join(" / ", (value.Singers ?? []).Select(item => item.Name?.Trim()).Where(name => !string.IsNullOrWhiteSpace(name)));
            if (artist.Length == 0) artist = "未知歌手";
            var albumName = FirstNonEmpty(value.Album?.Name, value.AlbumName, "未知专辑");
            var albumMid = FirstNonEmpty(value.Album?.Mid, value.AlbumMid);
            return new SearchResultItem(SearchResultType.Track, PlatformId.QqMusic, id, $"qq:track:{id}", FirstNonEmpty(value.Name, value.SongName),
                Subtitle: $"{artist} · {albumName}", Artist: artist, Album: albumName,
                CoverUri: albumMid.Length == 0 ? "ms-appx:///Assets/Branding/beans-icon.png" : $"https://y.gtimg.cn/music/photo_new/T002R300x300M000{albumMid}.jpg",
                Duration: value.DurationSeconds is > 0 ? TimeSpan.FromSeconds(value.DurationSeconds.Value) : null,
                Quality: Quality(value.File), IsPlayable: false, RestrictionState: "需要 QQ 音乐授权并解析播放地址",
                SourceDisplayName: "QQ 音乐", SourceBadgeText: "QQ 音乐", DataOrigin: SearchDataOrigin.Live,
                PayloadReference: $"qq:track:{id}", ProviderMediaId: FirstNonEmpty(value.File?.MediaMid, id));
        }).ToArray();
    }

    private static string Quality(QqDetailFileDto? file)
    {
        if (file?.SizeFlac > 0) return "FLAC";
        if (file?.SizeApe > 0) return "APE";
        if (file?.Size320 > 0) return "320K";
        if (file?.Size128 > 0) return "128K";
        return "未知";
    }

    private static HttpRequestMessage CreateGet(Uri endpoint)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Referrer = PublicReferer;
        return request;
    }

    private static HttpRequestMessage CreateArtistRequest(string singerMid)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, MusicuEndpoint);
        request.Headers.Referrer = PublicReferer;
        request.Content = JsonContent.Create(new
        {
            comm = new { ct = 24, cv = 0 },
            singerSongList = new
            {
                module = "musichall.song_list_server",
                method = "GetSingerSongList",
                param = new { singerMid, begin = 0, num = 50, order = 1 }
            }
        });
        return request;
    }

    private static PlatformRequestContext Context(OnlineMusicDetailQuery query, CancellationToken cancellationToken) =>
        PlatformRequestContext.PublicDiscovery("qq", $"qq.detail.{query.Kind.ToString().ToLowerInvariant()}",
            $"qq:detail:{query.Kind.ToString().ToLowerInvariant()}:{query.NativeId}", cancellationToken);

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
        return OnlineMusicDetailResponse.Failure(state, response.Error?.SafeMessage ?? "QQ 音乐详情暂时不可用", response.Error?.Code);
    }

    private static PlatformParseResult<T>? BusinessError<T>(int? code, string message)
    {
        if (code == 0) return null;
        if (code is null) return PlatformParseResult<T>.Failure(PlatformErrorCode.InvalidResponse, "QQ 音乐详情缺少业务状态");
        return PlatformParseResult<T>.Failure(code is 401 or 403 ? PlatformErrorCode.Unauthorized : code == 404 ? PlatformErrorCode.NotFound : PlatformErrorCode.ServiceUnavailable, message);
    }

    private static string NormalizeImage(string? raw)
    {
        var value = raw?.Trim().Replace("\\/", "/") ?? string.Empty;
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) value = "https://" + value[7..];
        else if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out _) ? value : "ms-appx:///Assets/Branding/beans-icon.png";
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static bool IsValidTrackMid(string value) => value.Length is > 0 and <= 128 && value.All(char.IsLetterOrDigit);
}
