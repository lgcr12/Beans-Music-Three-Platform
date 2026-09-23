using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.PlatformLibrary;
using Beans.Windows.Rebuild.Services.Playback;
using Beans.Windows.Rebuild.Services.Platforms.NetEase.Dto;
using Beans.Windows.Rebuild.Services.Security;

namespace Beans.Windows.Rebuild.Services.Platforms.NetEase;

public sealed class NetEasePlatformLibraryAdapter : IPlatformLibraryAdapter
{
    private const string PlatformKey = "netease";
    private const int SongDetailBatchSize = 500;
    private static readonly Uri Referer = new("https://music.163.com/");
    private static readonly Uri AccountEndpoint = new("https://music.163.com/api/nuser/account/get");
    private static readonly Uri MembershipEndpoint = new("https://music.163.com/api/music-vip-membership/client/vip/info");

    private readonly IPlatformHttpClient _httpClient;
    private readonly ISecureCredentialStore _credentials;
    private readonly IPlatformJsonSerializer _serializer;

    public NetEasePlatformLibraryAdapter(
        IPlatformHttpClientFactory httpClientFactory,
        ISecureCredentialStore credentials,
        IPlatformJsonSerializer serializer)
    {
        _httpClient = httpClientFactory.Get(PlatformKey);
        _credentials = credentials;
        _serializer = serializer;
    }

    public PlatformId Platform => PlatformId.NetEaseMusic;

    public async Task<PlatformLibrarySnapshot> LoadAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var loadedAt = DateTimeOffset.UtcNow;
        var session = await ProviderCredentialReader.ReadSessionAsync(_credentials, PlatformKey, cancellationToken);
        if (session is null)
            return Snapshot(
                Probe(CredentialState.NotAuthorized, "网易云音乐", null, PlatformMembershipState.Unknown,
                    "会员状态未知", loadedAt, "请先登录网易云音乐"),
                [], PlatformLibraryState.NotAuthorized, loadedAt, "请先登录网易云音乐");

        var accountHash = AccountIdentityHasher.Hash(session);
        var account = await SendAsync(
            () => CreateGet(AccountEndpoint, session),
            "library.account", "netease:library:account", accountHash,
            new JsonPlatformResponseParser<NetEaseLibraryAccountEnvelope, NetEaseLibraryAccount>(
                _serializer, ParseAccount), cancellationToken);

        if (!account.IsSuccess || account.Value is null)
        {
            var unauthorized = IsUnauthorized(account.Error?.Code);
            var message = unauthorized ? "网易云音乐登录已失效，请重新登录" : "网易云音乐账号信息暂时无法加载";
            return Snapshot(
                Probe(unauthorized ? CredentialState.Expired : CredentialState.Error, "网易云音乐", null,
                    PlatformMembershipState.Unknown, "会员状态未知", loadedAt, message),
                [], unauthorized ? PlatformLibraryState.NotAuthorized : PlatformLibraryState.Error, loadedAt, message);
        }

        var profile = account.Value;
        var membershipTask = SendAsync(
            () => CreateGet(MembershipEndpoint, session),
            "library.membership", "netease:library:membership", accountHash,
            new JsonPlatformResponseParser<NetEaseMembershipEnvelope, NetEaseMembership>(
                _serializer, ParseMembership), cancellationToken);

        var playlistsUri = new Uri(
            $"https://music.163.com/api/user/playlist?uid={profile.UserId.ToString(CultureInfo.InvariantCulture)}&limit=1000&offset=0");
        var playlistsTask = SendAsync(
            () => CreateGet(playlistsUri, session),
            "library.playlists", $"netease:library:playlists:{profile.UserId.ToString(CultureInfo.InvariantCulture)}", accountHash,
            new JsonPlatformResponseParser<NetEaseUserPlaylistsEnvelope, NetEaseUserPlaylists>(
                _serializer, ParsePlaylists), cancellationToken);

        await Task.WhenAll(membershipTask, playlistsTask);
        var membership = await membershipTask;
        var playlists = await playlistsTask;

        if (!playlists.IsSuccess || playlists.Value is null)
        {
            var unauthorized = IsUnauthorized(playlists.Error?.Code);
            var message = unauthorized ? "网易云音乐登录已失效，请重新登录" : "网易云音乐歌单暂时无法加载";
            return Snapshot(
                Probe(unauthorized ? CredentialState.Expired : CredentialState.Valid, profile.DisplayName, profile.UserId,
                    MembershipState(membership), MembershipLabel(membership), loadedAt, message),
                [], unauthorized ? PlatformLibraryState.NotAuthorized : PlatformLibraryState.Error, loadedAt, message);
        }

        var mapped = MapPlaylists(playlists.Value.Items, profile);
        var partial = !membership.IsSuccess || playlists.Value.InvalidItemCount > 0;
        var state = partial
            ? PlatformLibraryState.Partial
            : mapped.Count == 0 ? PlatformLibraryState.Empty : PlatformLibraryState.Succeeded;
        var safeMessage = partial
            ? !membership.IsSuccess
                ? "歌单已加载，会员状态暂时无法确认"
                : "部分歌单信息不完整，已显示可用内容"
            : mapped.Count == 0 ? "网易云音乐账号暂无歌单" : $"已加载 {mapped.Count} 个网易云音乐歌单";

        return Snapshot(
            Probe(CredentialState.Valid, profile.DisplayName, profile.UserId,
                MembershipState(membership), MembershipLabel(membership), loadedAt, "网易云音乐账号有效"),
            mapped, state, loadedAt, safeMessage, partial);
    }

    public async Task<IReadOnlyList<PlatformPlaylistTrack>> LoadPlaylistTracksAsync(
        PlatformUserPlaylist playlist,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        cancellationToken.ThrowIfCancellationRequested();
        if (playlist.Platform != Platform || !long.TryParse(playlist.NativeId, NumberStyles.None,
                CultureInfo.InvariantCulture, out var playlistId) || playlistId <= 0)
            return [];

        var session = await ProviderCredentialReader.ReadSessionAsync(_credentials, PlatformKey, cancellationToken);
        if (session is null) throw new UnauthorizedAccessException("请先登录网易云音乐");
        var accountHash = AccountIdentityHasher.Hash(session);
        var endpoint = new Uri(
            $"https://music.163.com/api/v6/playlist/detail?id={playlistId.ToString(CultureInfo.InvariantCulture)}&n=100000&s=8");
        var detail = await SendAsync(
            () => CreateGet(endpoint, session),
            "library.playlist-tracks", $"netease:library:playlist-tracks:{playlistId.ToString(CultureInfo.InvariantCulture)}", accountHash,
            new JsonPlatformResponseParser<NetEaseLibraryPlaylistDetailEnvelope, NetEaseLibraryPlaylistDetail>(
                _serializer, ParsePlaylistDetail), cancellationToken);

        EnsureTrackRequestSucceeded(detail, "网易云音乐歌单歌曲暂时无法加载");
        var payload = detail.Value!;
        var tracksById = payload.Tracks
            .Where(IsUsableTrack)
            .GroupBy(track => track.Id!.Value)
            .ToDictionary(group => group.Key, group => group.First());
        var orderedIds = payload.TrackIds.Count > 0
            ? payload.TrackIds
            : payload.Tracks.Where(IsUsableTrack).Select(track => track.Id!.Value).ToArray();
        var missingIds = orderedIds.Where(id => id > 0 && !tracksById.ContainsKey(id)).Distinct().ToArray();

        foreach (var batch in missingIds.Chunk(SongDetailBatchSize))
        {
            var response = await SendAsync(
                () => CreateSongDetailRequest(batch, session),
                "library.song-details", $"netease:library:song-details:{StableBatchKey(batch)}", accountHash,
                new JsonPlatformResponseParser<NetEaseSongDetailsEnvelope, IReadOnlyList<NetEaseDetailTrackDto>>(
                    _serializer, ParseSongDetails), cancellationToken);
            EnsureTrackRequestSucceeded(response, "网易云音乐歌曲详情暂时无法加载");
            foreach (var track in response.Value!.Where(IsUsableTrack)) tracksById.TryAdd(track.Id!.Value, track);
        }

        return orderedIds
            .Distinct()
            .Select(id => tracksById.TryGetValue(id, out var track)
                ? MapTrack(track)
                : UnavailableTrack(id))
            .ToArray();
    }

    private Task<PlatformResponse<T>> SendAsync<T>(
        Func<HttpRequestMessage> requestFactory,
        string operation,
        string requestKey,
        string accountHash,
        IPlatformResponseParser<T> parser,
        CancellationToken cancellationToken)
    {
        var context = new PlatformRequestContext(
            PlatformKey, operation, requestKey, TimeSpan.FromSeconds(15), true,
            PlatformCachePolicy.NetworkOnly, accountHash, false, 0, cancellationToken);
        return _httpClient.SendAsync(requestFactory, context, parser);
    }

    private static HttpRequestMessage CreateGet(Uri endpoint, string session)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        ApplyStandardHeaders(request, session);
        return request;
    }

    private static HttpRequestMessage CreateSongDetailRequest(IReadOnlyList<long> songIds, string session)
    {
        var descriptors = songIds.Select(id => new Dictionary<string, long> { ["id"] = id }).ToArray();
        var request = new HttpRequestMessage(HttpMethod.Post, "https://music.163.com/api/v3/song/detail")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["c"] = JsonSerializer.Serialize(descriptors),
                ["ids"] = JsonSerializer.Serialize(songIds)
            })
        };
        ApplyStandardHeaders(request, session);
        return request;
    }

    private static void ApplyStandardHeaders(HttpRequestMessage request, string session)
    {
        request.Headers.Referrer = Referer;
        request.Headers.UserAgent.ParseAdd("BeansMusic-Windows/Phase8");
        ProviderCredentialReader.ApplyCookieHeader(request, session);
    }

    private static PlatformParseResult<NetEaseLibraryAccount> ParseAccount(NetEaseLibraryAccountEnvelope envelope)
    {
        var error = BusinessError<NetEaseLibraryAccount>(envelope.Code, "网易云音乐账号信息请求失败");
        if (error is not null) return error.Value;
        if (envelope.Profile?.UserId is not > 0)
            return PlatformParseResult<NetEaseLibraryAccount>.Failure(PlatformErrorCode.Unauthorized, "网易云音乐登录已失效");
        return PlatformParseResult<NetEaseLibraryAccount>.Success(new NetEaseLibraryAccount(
            envelope.Profile.UserId.Value,
            FirstNonEmpty(envelope.Profile.Nickname, "网易云用户")));
    }

    private static PlatformParseResult<NetEaseMembership> ParseMembership(NetEaseMembershipEnvelope envelope)
    {
        var error = BusinessError<NetEaseMembership>(envelope.Code, "网易云音乐会员状态请求失败");
        if (error is not null) return error.Value;
        var level = new[]
        {
            envelope.RedVipLevel ?? 0,
            envelope.VipLevel ?? 0,
            envelope.VipCode ?? 0,
            FindMembershipLevel(envelope.Data)
        }.Max();
        return PlatformParseResult<NetEaseMembership>.Success(new NetEaseMembership(level > 0, level));
    }

    private static PlatformParseResult<NetEaseUserPlaylists> ParsePlaylists(NetEaseUserPlaylistsEnvelope envelope)
    {
        var error = BusinessError<NetEaseUserPlaylists>(envelope.Code, "网易云音乐歌单请求失败");
        if (error is not null) return error.Value;
        if (envelope.Playlists is null)
            return PlatformParseResult<NetEaseUserPlaylists>.Failure(PlatformErrorCode.InvalidResponse, "网易云音乐歌单响应缺少数据");
        var invalid = envelope.Playlists.Count(item => item.Id is not > 0 || string.IsNullOrWhiteSpace(item.Name));
        return PlatformParseResult<NetEaseUserPlaylists>.Success(new NetEaseUserPlaylists(envelope.Playlists, invalid));
    }

    private static PlatformParseResult<NetEaseLibraryPlaylistDetail> ParsePlaylistDetail(
        NetEaseLibraryPlaylistDetailEnvelope envelope)
    {
        var error = BusinessError<NetEaseLibraryPlaylistDetail>(envelope.Code, "网易云音乐歌单歌曲请求失败");
        if (error is not null) return error.Value;
        if (envelope.Playlist is null)
            return PlatformParseResult<NetEaseLibraryPlaylistDetail>.Failure(PlatformErrorCode.InvalidResponse, "网易云音乐歌单响应缺少数据");
        var ids = envelope.Playlist.TrackIds?
            .Where(item => item.Id is > 0)
            .Select(item => item.Id!.Value)
            .ToArray() ?? [];
        return PlatformParseResult<NetEaseLibraryPlaylistDetail>.Success(
            new NetEaseLibraryPlaylistDetail(envelope.Playlist.Tracks ?? [], ids));
    }

    private static PlatformParseResult<IReadOnlyList<NetEaseDetailTrackDto>> ParseSongDetails(
        NetEaseSongDetailsEnvelope envelope)
    {
        var error = BusinessError<IReadOnlyList<NetEaseDetailTrackDto>>(envelope.Code, "网易云音乐歌曲详情请求失败");
        if (error is not null) return error.Value;
        return envelope.Songs is null
            ? PlatformParseResult<IReadOnlyList<NetEaseDetailTrackDto>>.Failure(
                PlatformErrorCode.InvalidResponse, "网易云音乐歌曲详情响应缺少数据")
            : PlatformParseResult<IReadOnlyList<NetEaseDetailTrackDto>>.Success(envelope.Songs);
    }

    private static PlatformParseResult<T>? BusinessError<T>(int? code, string message)
    {
        if (code == 200) return null;
        if (code is null) return PlatformParseResult<T>.Failure(PlatformErrorCode.InvalidResponse, "网易云音乐响应缺少业务状态");
        return PlatformParseResult<T>.Failure(code is 301 or 401 or 403
            ? PlatformErrorCode.Unauthorized
            : PlatformErrorCode.ServiceUnavailable, message);
    }

    private static IReadOnlyList<PlatformUserPlaylist> MapPlaylists(
        IReadOnlyList<NetEaseUserPlaylistDto> values,
        NetEaseLibraryAccount profile) =>
        values
            .Where(item => item.Id is > 0 && !string.IsNullOrWhiteSpace(item.Name))
            .Select(item =>
            {
                var favorite = item.SpecialType == 5;
                var subscribed = item.Subscribed == true || item.Creator?.UserId is > 0 && item.Creator.UserId != profile.UserId;
                var kind = favorite ? "favorites" : subscribed ? "subscribed" : "created";
                return new PlatformUserPlaylist(
                    PlatformId.NetEaseMusic,
                    item.Id!.Value.ToString(CultureInfo.InvariantCulture),
                    kind,
                    item.Name!.Trim(),
                    FirstNonEmpty(item.Creator?.Nickname, profile.DisplayName),
                    NormalizeImage(item.CoverImageUrl),
                    item.TrackCount is >= 0 ? item.TrackCount : null,
                    favorite);
            })
            .GroupBy(item => item.NativeId, StringComparer.Ordinal)
            .Select(group => group.First())
            .GroupBy(PlatformPlaylistDeduplicator.VisibleIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item.IsFavoriteCollection)
                .ThenByDescending(item => item.TrackCount ?? 0)
                .First())
            .ToArray();

    private static PlatformPlaylistTrack MapTrack(NetEaseDetailTrackDto value)
    {
        var id = value.Id!.Value.ToString(CultureInfo.InvariantCulture);
        var artists = value.Artists ?? value.LegacyArtists ?? [];
        var artist = string.Join(" / ", artists.Select(item => item.Name?.Trim()).Where(item => !string.IsNullOrWhiteSpace(item)));
        var album = value.Album ?? value.LegacyAlbum;
        var duration = value.DurationMilliseconds ?? value.LegacyDurationMilliseconds;
        var availability = value.NoCopyrightReason is not null || value.Privilege?.Status is < 0
            ? AvailabilityState.Unavailable
            : value.Fee == 1 ? AvailabilityState.SubscriptionRequired : AvailabilityState.Available;
        return new PlatformPlaylistTrack(
            PlatformId.NetEaseMusic,
            id,
            null,
            FirstNonEmpty(value.Name, "未知歌曲"),
            FirstNonEmpty(artist, "未知歌手"),
            FirstNonEmpty(album?.Name, "未知专辑"),
            NormalizeImage(album?.PictureUrl),
            duration is > 0 ? TimeSpan.FromMilliseconds(duration.Value) : null,
            Quality(value),
            availability);
    }

    private static PlatformPlaylistTrack UnavailableTrack(long id) => new(
        PlatformId.NetEaseMusic,
        id.ToString(CultureInfo.InvariantCulture),
        null,
        "不可用歌曲",
        "未知歌手",
        "未知专辑",
        "ms-appx:///Assets/Branding/beans-icon.png",
        null,
        "未知",
        AvailabilityState.Unavailable);

    private static string Quality(NetEaseDetailTrackDto value)
    {
        if (value.Lossless?.Bitrate is > 0) return "SQ";
        if (value.High?.Bitrate is > 0) return $"{value.High.Bitrate.Value / 1000}K";
        if (value.Medium?.Bitrate is > 0) return $"{value.Medium.Bitrate.Value / 1000}K";
        if (value.Low?.Bitrate is > 0) return $"{value.Low.Bitrate.Value / 1000}K";
        return "未知";
    }

    private static PlatformMembershipState MembershipState(PlatformResponse<NetEaseMembership> response) =>
        !response.IsSuccess || response.Value is null
            ? PlatformMembershipState.Unknown
            : response.Value.IsActive ? PlatformMembershipState.Active : PlatformMembershipState.Inactive;

    private static string MembershipLabel(PlatformResponse<NetEaseMembership> response) =>
        !response.IsSuccess || response.Value is null
            ? "会员状态未知"
            : response.Value.IsActive ? "黑胶 VIP" : "普通账号";

    private static int FindMembershipLevel(JsonElement? data)
    {
        if (data is null) return 0;
        return FindMembershipLevel(data.Value);
    }

    private static int FindMembershipLevel(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if ((property.Name is "redVipLevel" or "vipLevel" or "vipCode") &&
                    property.Value.TryGetInt32(out var level)) return level;
            }
            foreach (var property in value.EnumerateObject())
            {
                var nested = FindMembershipLevel(property.Value);
                if (nested > 0) return nested;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var nested = FindMembershipLevel(item);
                if (nested > 0) return nested;
            }
        }
        return 0;
    }

    private static void EnsureTrackRequestSucceeded<T>(PlatformResponse<T> response, string fallback)
    {
        if (response.IsSuccess && response.Value is not null) return;
        if (IsUnauthorized(response.Error?.Code))
            throw new UnauthorizedAccessException("网易云音乐登录已失效，请重新登录");
        throw new InvalidOperationException(fallback);
    }

    private static bool IsUnauthorized(PlatformErrorCode? code) =>
        code is PlatformErrorCode.Unauthorized or PlatformErrorCode.CredentialExpired or PlatformErrorCode.Forbidden;

    private static bool IsUsableTrack(NetEaseDetailTrackDto value) =>
        value.Id is > 0 && !string.IsNullOrWhiteSpace(value.Name);

    private static string StableBatchKey(IReadOnlyList<long> values)
    {
        var raw = string.Join(',', values);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)))
            [..16].ToLowerInvariant();
    }

    private static string NormalizeImage(string? raw)
    {
        var value = raw?.Trim().Replace("\\/", "/") ?? string.Empty;
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) value = "https://" + value[7..];
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.AbsoluteUri
            : "ms-appx:///Assets/Branding/beans-icon.png";
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private PlatformLibrarySnapshot Snapshot(
        PlatformProbeSnapshot probe,
        IReadOnlyList<PlatformUserPlaylist> playlists,
        PlatformLibraryState state,
        DateTimeOffset loadedAt,
        string message,
        bool partial = false) =>
        new(Platform, probe, playlists, state, SearchDataOrigin.Live, loadedAt, message, partial);

    private PlatformProbeSnapshot Probe(
        CredentialState state,
        string displayName,
        long? userId,
        PlatformMembershipState membership,
        string membershipLabel,
        DateTimeOffset checkedAt,
        string message) =>
        new(Platform, state, displayName, userId?.ToString(CultureInfo.InvariantCulture), membership,
            membershipLabel, checkedAt, message);

    private sealed record NetEaseLibraryAccountEnvelope(
        [property: JsonPropertyName("code")] int? Code,
        [property: JsonPropertyName("profile")] NetEaseLibraryProfileDto? Profile);

    private sealed record NetEaseLibraryProfileDto(
        [property: JsonPropertyName("userId")] long? UserId,
        [property: JsonPropertyName("nickname")] string? Nickname);

    private sealed record NetEaseMembershipEnvelope(
        [property: JsonPropertyName("code")] int? Code,
        [property: JsonPropertyName("data")] JsonElement? Data,
        [property: JsonPropertyName("redVipLevel")] int? RedVipLevel,
        [property: JsonPropertyName("vipLevel")] int? VipLevel,
        [property: JsonPropertyName("vipCode")] int? VipCode);

    private sealed record NetEaseUserPlaylistsEnvelope(
        [property: JsonPropertyName("code")] int? Code,
        [property: JsonPropertyName("playlist")] IReadOnlyList<NetEaseUserPlaylistDto>? Playlists);

    private sealed record NetEaseUserPlaylistDto(
        [property: JsonPropertyName("id")] long? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("coverImgUrl")] string? CoverImageUrl,
        [property: JsonPropertyName("trackCount")] int? TrackCount,
        [property: JsonPropertyName("specialType")] int? SpecialType,
        [property: JsonPropertyName("subscribed")] bool? Subscribed,
        [property: JsonPropertyName("creator")] NetEaseLibraryCreatorDto? Creator);

    private sealed record NetEaseLibraryCreatorDto(
        [property: JsonPropertyName("userId")] long? UserId,
        [property: JsonPropertyName("nickname")] string? Nickname);

    private sealed record NetEaseLibraryPlaylistDetailEnvelope(
        [property: JsonPropertyName("code")] int? Code,
        [property: JsonPropertyName("playlist")] NetEaseLibraryPlaylistDto? Playlist);

    private sealed record NetEaseLibraryPlaylistDto(
        [property: JsonPropertyName("tracks")] IReadOnlyList<NetEaseDetailTrackDto>? Tracks,
        [property: JsonPropertyName("trackIds")] IReadOnlyList<NetEaseTrackIdDto>? TrackIds);

    private sealed record NetEaseTrackIdDto([property: JsonPropertyName("id")] long? Id);

    private sealed record NetEaseSongDetailsEnvelope(
        [property: JsonPropertyName("code")] int? Code,
        [property: JsonPropertyName("songs")] IReadOnlyList<NetEaseDetailTrackDto>? Songs);

    private sealed record NetEaseLibraryAccount(long UserId, string DisplayName);
    private sealed record NetEaseMembership(bool IsActive, int Level);
    private sealed record NetEaseUserPlaylists(IReadOnlyList<NetEaseUserPlaylistDto> Items, int InvalidItemCount);
    private sealed record NetEaseLibraryPlaylistDetail(
        IReadOnlyList<NetEaseDetailTrackDto> Tracks,
        IReadOnlyList<long> TrackIds);
}
