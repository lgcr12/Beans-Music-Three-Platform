using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.PlatformLibrary;
using Beans.Windows.Rebuild.Services.Playback;
using Beans.Windows.Rebuild.Services.Security;

namespace Beans.Windows.Rebuild.Services.Platforms.QQ;

/// <summary>
/// Loads the library visible to the currently authorized QQ Music account.
/// Session values remain in Credential Locker and are never included in cache
/// keys, returned models, diagnostics, or safe error messages.
/// </summary>
public sealed class QqPlatformLibraryAdapter : IPlatformLibraryAdapter
{
    private static readonly Uri ProfileEndpoint = new("https://c.y.qq.com/rsc/fcgi-bin/fcg_get_profile_homepage.fcg");
    private readonly IPlatformHttpClient _httpClient;
    private readonly ISecureCredentialStore _credentials;
    private readonly IPlatformJsonSerializer _serializer;

    public QqPlatformLibraryAdapter(
        IPlatformHttpClientFactory httpClientFactory,
        ISecureCredentialStore credentials,
        IPlatformJsonSerializer serializer)
    {
        _httpClient = httpClientFactory.Get("qq");
        _credentials = credentials;
        _serializer = serializer;
    }

    public PlatformId Platform => PlatformId.QqMusic;

    public async Task<PlatformLibrarySnapshot> LoadAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = await ProviderCredentialReader.ReadSessionAsync(_credentials, "qq", cancellationToken);
        if (!TryGetIdentity(session, out var uin, out var cookies))
            return Snapshot(CredentialState.NotAuthorized, "QQ 音乐", null, [],
                PlatformLibraryState.NotAuthorized, "请先登录 QQ 音乐");

        var gtk = Gtk(First(cookies, "p_skey", "skey") ?? string.Empty);
        var profileUri = new Uri($"{ProfileEndpoint}?cid=205360838&userid={Uri.EscapeDataString(uin)}&reqfrom=1&g_tk={gtk}&loginUin={Uri.EscapeDataString(uin)}&format=json");
        var profile = await SendGetAsync(profileUri, session!, uin, "library-profile",
            $"qq:library-profile:{StableKey(uin)}", cancellationToken);
        if (!profile.IsSuccess || profile.Value.ValueKind == JsonValueKind.Undefined)
            return Snapshot(CredentialStateFor(profile.Error?.Code), "QQ 音乐", uin, [],
                LibraryStateFor(profile.Error?.Code), SafeProfileMessage(profile.Error?.Code));

        var displayName = FindString(profile.Value, "nick") ?? FindString(profile.Value, "nickname") ?? "QQ 音乐用户";
        var playlists = new List<PlatformUserPlaylist>();
        CollectPlaylists(profile.Value, displayName, playlists);
        var partial = false;

        var assetUri = new Uri("https://c.y.qq.com/fav/fcgi-bin/fcg_get_profile_order_asset.fcg" +
            $"?ct=20&cid=205360956&userid={Uri.EscapeDataString(uin)}&reqtype=3&sin=0&ein=9999&format=json&g_tk={gtk}");
        var assets = await SendGetAsync(assetUri, session!, uin, "library-assets",
            $"qq:library-assets:{StableKey(uin)}", cancellationToken);
        if (assets.IsSuccess) CollectPlaylists(assets.Value, displayName, playlists);
        else partial = true;

        var createdUri = new Uri("https://c.y.qq.com/rsc/fcgi-bin/fcg_user_created_diss" +
            $"?hostuin={Uri.EscapeDataString(uin)}&sin=0&size=999&g_tk={gtk}&format=json");
        var created = await SendGetAsync(createdUri, session!, uin, "library-created",
            $"qq:library-created:{StableKey(uin)}", cancellationToken);
        if (created.IsSuccess) CollectPlaylists(created.Value, displayName, playlists);
        else partial = true;

        var distinct = playlists
            .Where(item => !string.IsNullOrWhiteSpace(item.NativeId))
            // QQ returns the same user playlist from profile, asset and created
            // endpoints with different kind labels. The provider ID is the
            // stable identity; keeping NativeKind in the key renders duplicates.
            .GroupBy(item => item.NativeId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item.IsFavoriteCollection)
                .ThenByDescending(item => item.TrackCount ?? 0)
                .First())
            // QQ occasionally assigns different ids and cover URLs to the
            // same visible playlist across profile, asset and created APIs.
            // Title + creator is the stable identity shown to the user.
            .GroupBy(PlatformPlaylistDeduplicator.VisibleIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item.IsFavoriteCollection)
                .ThenByDescending(item => item.TrackCount ?? 0)
                .First())
            .OrderByDescending(item => item.IsFavoriteCollection)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var state = partial ? PlatformLibraryState.Partial : distinct.Length == 0 ? PlatformLibraryState.Empty : PlatformLibraryState.Succeeded;
        var message = partial
            ? "QQ 音乐账号已验证，部分歌单暂时无法加载"
            : distinct.Length == 0 ? "QQ 音乐账号当前没有可导入的歌单" : "QQ 音乐歌单已更新";
        return Snapshot(CredentialState.Valid, displayName, uin, distinct, state, message, partial);
    }

    public async Task<IReadOnlyList<PlatformPlaylistTrack>> LoadPlaylistTracksAsync(
        PlatformUserPlaylist playlist,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (playlist.Platform != Platform || !long.TryParse(playlist.NativeId, NumberStyles.None,
                CultureInfo.InvariantCulture, out var playlistId) || playlistId <= 0)
            return [];

        var session = await ProviderCredentialReader.ReadSessionAsync(_credentials, "qq", cancellationToken);
        if (!TryGetIdentity(session, out var uin, out var cookies)) return [];
        var gtk = Gtk(First(cookies, "p_skey", "skey") ?? string.Empty);
        return playlist.NativeKind.ToLowerInvariant() switch
        {
            "favorite" => await LoadFavoriteTracksAsync(uin, gtk, session!, cancellationToken),
            "dir" => await LoadDirectoryTracksAsync(playlistId, uin, gtk, session!, cancellationToken),
            "diss" or "tid" => await LoadDissTracksAsync(playlistId, uin, gtk, session!, cancellationToken),
            _ => []
        };
    }

    private async Task<IReadOnlyList<PlatformPlaylistTrack>> LoadFavoriteTracksAsync(
        string uin,
        int gtk,
        string session,
        CancellationToken cancellationToken)
    {
        var uri = new Uri("https://c.y.qq.com/splcloud/fcgi-bin/fcg_musiclist_getmyfav.fcg" +
            $"?dirid=201&dirinfo=1&g_tk={gtk}&loginUin={Uri.EscapeDataString(uin)}&format=json&utf8=1");
        var response = await SendGetAsync(uri, session, uin, "favorite-tracks",
            $"qq:favorite-tracks:{StableKey(uin)}", cancellationToken);
        if (!response.IsSuccess) return [];
        if (FindArray(response.Value, "songlist") is { } songList)
            return ParseTracks(songList);
        if (FindObject(response.Value, "mapmid") is not { } mapMid) return [];
        var mids = mapMid.EnumerateObject()
            .Select(property => property.Name)
            .Where(IsSafeMid)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return mids.Length == 0 ? [] : await LoadSongDetailsAsync(mids, session, uin, cancellationToken);
    }

    private async Task<IReadOnlyList<PlatformPlaylistTrack>> LoadDirectoryTracksAsync(
        long playlistId,
        string uin,
        int gtk,
        string session,
        CancellationToken cancellationToken)
    {
        var uri = new Uri("https://c.y.qq.com/splcloud/fcgi-bin/fcg_musiclist_getmyfav.fcg" +
            $"?format=json&cid=205360955&reqtype=3&uin={Uri.EscapeDataString(uin)}&dirid={playlistId}&songstatus=1&start=0&num=9999&g_tk={gtk}&loginUin={Uri.EscapeDataString(uin)}&platform=yqq.json");
        var response = await SendGetAsync(uri, session, uin, "directory-tracks",
            $"qq:directory-tracks:{StableKey(playlistId.ToString(CultureInfo.InvariantCulture))}", cancellationToken);
        if (!response.IsSuccess) return [];
        if (FindArray(response.Value, "songlist") is { } songList)
        {
            var tracks = ParseTracks(songList);
            if (tracks.Count > 0 || songList.GetArrayLength() == 0) return tracks;
        }
        if (FindObject(response.Value, "mapmid") is not { } mapMid) return [];
        var mids = mapMid.EnumerateObject().Select(property => property.Name).Where(IsSafeMid).ToArray();
        return mids.Length == 0 ? [] : await LoadSongDetailsAsync(mids, session, uin, cancellationToken);
    }

    private async Task<IReadOnlyList<PlatformPlaylistTrack>> LoadDissTracksAsync(
        long playlistId,
        string uin,
        int gtk,
        string session,
        CancellationToken cancellationToken)
    {
        var uri = new Uri("https://c.y.qq.com/qzone/fcg-bin/fcg_ucc_getcdinfo_byids_cp.fcg" +
            $"?type=1&json=1&utf8=1&onlysong=0&new_format=1&song_begin=0&song_num=9999&disstid={playlistId}&g_tk={gtk}&loginUin={Uri.EscapeDataString(uin)}&format=json&inCharset=utf8&outCharset=utf-8&platform=yqq.json");
        var response = await SendGetAsync(uri, session, uin, "playlist-tracks",
            $"qq:playlist-tracks:{StableKey(playlistId.ToString(CultureInfo.InvariantCulture))}", cancellationToken);
        if (response.IsSuccess && FindArray(response.Value, "songlist") is { } list)
            return ParseTracks(list);

        var payload = JsonSerializer.Serialize(new
        {
            comm = new { ct = 24, cv = 0 },
            req_1 = new
            {
                module = "music.playlist.PlayListDataServer",
                method = "GetPlaylistDetail",
                param = new { id = playlistId, uin = 0, song_begin = 0, song_num = 9999 }
            }
        });
        var musicu = await SendPostAsync(payload, session, uin, "playlist-tracks-fallback",
            $"qq:playlist-tracks-fallback:{StableKey(playlistId.ToString(CultureInfo.InvariantCulture))}", cancellationToken);
        return musicu.IsSuccess && FindArray(musicu.Value, "songlist") is { } fallback ? ParseTracks(fallback) : [];
    }

    private async Task<IReadOnlyList<PlatformPlaylistTrack>> LoadSongDetailsAsync(
        IReadOnlyList<string> songMids,
        string session,
        string uin,
        CancellationToken cancellationToken)
    {
        var tracks = new List<PlatformPlaylistTrack>(songMids.Count);
        const int batchSize = 10;
        for (var offset = 0; offset < songMids.Count; offset += batchSize)
        {
            var batch = songMids.Skip(offset).Take(batchSize).ToArray();
            var payload = new Dictionary<string, object>
            {
                ["comm"] = new { ct = 24, cv = 0 }
            };
            for (var index = 0; index < batch.Length; index++)
            {
                payload[$"req_{index}"] = new
                {
                    module = "music.pf_song_detail_svr",
                    method = "get_song_detail_yqq",
                    param = new { song_mid = batch[index] }
                };
            }
            var response = await SendPostAsync(JsonSerializer.Serialize(payload), session, uin, "song-details",
                $"qq:song-details:{StableKey(string.Join(':', batch))}", cancellationToken);
            if (!response.IsSuccess) continue;
            for (var index = 0; index < batch.Length; index++)
            {
                if (response.Value.TryGetProperty($"req_{index}", out var request) &&
                    FindObject(request, "track_info") is { } item && ParseTrack(item) is { } track)
                    tracks.Add(track);
            }
        }
        return tracks;
    }

    private Task<PlatformResponse<JsonElement>> SendGetAsync(
        Uri uri,
        string session,
        string uin,
        string operation,
        string requestKey,
        CancellationToken cancellationToken) =>
        _httpClient.SendAsync(() => CreateRequest(HttpMethod.Get, uri, session),
            Context(operation, requestKey, uin, cancellationToken), EnvelopeParser());

    private Task<PlatformResponse<JsonElement>> SendPostAsync(
        string payload,
        string session,
        string uin,
        string operation,
        string requestKey,
        CancellationToken cancellationToken) =>
        _httpClient.SendAsync(() =>
        {
            var request = CreateRequest(HttpMethod.Post, new Uri("https://u.y.qq.com/cgi-bin/musicu.fcg"), session);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            return request;
        }, Context(operation, requestKey, uin, cancellationToken), EnvelopeParser(false));

    private IPlatformResponseParser<JsonElement> EnvelopeParser(bool validateTopLevelCode = true) =>
        new JsonPlatformResponseParser<JsonElement, JsonElement>(_serializer, root =>
        {
            if (root.ValueKind != JsonValueKind.Object)
                return PlatformParseResult<JsonElement>.Failure(PlatformErrorCode.InvalidResponse, "QQ 音乐账号响应无效");
            if (validateTopLevelCode && TryGetInt(root, "code", out var code) && code is not (0 or 1000))
                return PlatformParseResult<JsonElement>.Failure(
                    code is 401 or 403 or 1001 ? PlatformErrorCode.Unauthorized : PlatformErrorCode.ServiceUnavailable,
                    code is 401 or 403 or 1001 ? "QQ 音乐登录已失效" : "QQ 音乐账号服务暂时不可用");
            return PlatformParseResult<JsonElement>.Success(root);
        });

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, string session)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Referrer = new Uri("https://y.qq.com/");
        request.Headers.TryAddWithoutValidation("Origin", "https://y.qq.com");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) BeansMusic/1.0");
        ProviderCredentialReader.ApplyCookieHeader(request, session);
        return request;
    }

    private static PlatformRequestContext Context(
        string operation,
        string requestKey,
        string uin,
        CancellationToken cancellationToken) =>
        new("qq", operation, requestKey, TimeSpan.FromSeconds(15), true,
            PlatformCachePolicy.NetworkOnly, AccountIdentityHasher.Hash(uin), false, 0, cancellationToken);

    private static void CollectPlaylists(JsonElement value, string creator, ICollection<PlatformUserPlaylist> target)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var title = FirstString(value, "dissname", "dirname", "diss_name", "name", "title");
            var id = FirstPositiveInt64(value, "dissid");
            var kind = id > 0 ? "diss" : string.Empty;
            if (id == 0) { id = FirstPositiveInt64(value, "dirid"); if (id > 0) kind = "dir"; }
            if (id == 0 && !string.IsNullOrWhiteSpace(title)) { id = FirstPositiveInt64(value, "tid"); if (id > 0) kind = "tid"; }
            if (id == 0) { id = FirstPositiveInt64(value, "diss_id"); if (id > 0) kind = "diss"; }
            if (id == 0 && !string.IsNullOrWhiteSpace(title) && LooksLikePlaylist(value))
            {
                id = FirstPositiveInt64(value, "id");
                if (id > 0) kind = "diss";
            }
            if (id > 0 && !string.IsNullOrWhiteSpace(title))
            {
                var favorite = (kind == "dir" && id == 201) || IsFavoriteName(title);
                if (favorite) kind = "favorite";
                var cover = NormalizeImage(FirstString(value, "logo", "picurl", "diss_cover", "dir_pic_url",
                    "pic_url", "cover", "cover_url", "headurl", "imgurl"));
                var count = FirstNonNegativeInt(value, "songnum", "song_count", "song_cnt", "songcount",
                    "songNum", "total_song_num", "totalSongNum");
                target.Add(new PlatformUserPlaylist(PlatformId.QqMusic,
                    id.ToString(CultureInfo.InvariantCulture), kind, title.Trim(), creator, cover,
                    count >= 0 ? count : null, favorite));
            }
            foreach (var property in value.EnumerateObject()) CollectPlaylists(property.Value, creator, target);
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) CollectPlaylists(item, creator, target);
        }
    }

    private static IReadOnlyList<PlatformPlaylistTrack> ParseTracks(JsonElement songs)
    {
        if (songs.ValueKind != JsonValueKind.Array) return [];
        return songs.EnumerateArray().Select(ParseTrack).Where(item => item is not null).Cast<PlatformPlaylistTrack>().ToArray();
    }

    private static PlatformPlaylistTrack? ParseTrack(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object) return null;
        var item = raw;
        foreach (var wrapper in new[] { "track_info", "musicData", "songInfo", "data" })
        {
            if (!item.TryGetProperty(wrapper, out var nested) || nested.ValueKind != JsonValueKind.Object) continue;
            item = nested;
            break;
        }
        var nativeId = FirstString(item, "songmid", "mid", "Fsong_mid");
        if (!IsSafeMid(nativeId)) return null;
        var title = FirstString(item, "songname", "name", "title", "Fsong_name") ?? "未知歌曲";
        var albumObject = item.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object ? album : default;
        var albumMid = FirstString(item, "albummid", "albumMid", "Falbum_mid") ??
                       (albumObject.ValueKind == JsonValueKind.Object ? FirstString(albumObject, "mid") : null);
        var albumName = FirstString(item, "albumname", "Falbum_name") ??
                        (albumObject.ValueKind == JsonValueKind.Object ? FirstString(albumObject, "name") : null) ?? "未知专辑";
        var file = item.TryGetProperty("file", out var fileObject) && fileObject.ValueKind == JsonValueKind.Object ? fileObject : default;
        var mediaMid = FirstString(item, "strMediaMid", "media_mid") ??
                       (file.ValueKind == JsonValueKind.Object ? FirstString(file, "media_mid") : null) ?? nativeId;
        var seconds = FirstNonNegativeInt(item, "interval", "duration");
        var quality = Quality(file);
        return new PlatformPlaylistTrack(PlatformId.QqMusic, nativeId!, mediaMid, title.Trim(),
            JoinArtists(item), albumName.Trim(),
            string.IsNullOrWhiteSpace(albumMid) ? string.Empty : $"https://y.gtimg.cn/music/photo_new/T002R300x300M000{albumMid}.jpg",
            seconds > 0 ? TimeSpan.FromSeconds(seconds) : null, quality, AvailabilityState.Available);
    }

    private static string Quality(JsonElement file)
    {
        if (file.ValueKind != JsonValueKind.Object) return "未知";
        if (FirstPositiveInt64(file, "size_hires", "size_hires_flac") > 0) return "Hi-Res";
        if (FirstPositiveInt64(file, "size_flac", "size_ape") > 0) return "FLAC";
        if (FirstPositiveInt64(file, "size_320mp3") > 0) return "320K";
        if (FirstPositiveInt64(file, "size_128mp3") > 0) return "128K";
        return "未知";
    }

    private static string JoinArtists(JsonElement item)
    {
        if (item.TryGetProperty("singer", out var singers) && singers.ValueKind == JsonValueKind.Array)
        {
            var names = singers.EnumerateArray().Select(value => FirstString(value, "name"))
                .Where(value => !string.IsNullOrWhiteSpace(value));
            var joined = string.Join(" / ", names!);
            if (!string.IsNullOrWhiteSpace(joined)) return joined;
        }
        return FirstString(item, "Fsinger_name") ?? "未知歌手";
    }

    private static PlatformLibrarySnapshot Snapshot(
        CredentialState credentialState,
        string displayName,
        string? nativeUserId,
        IReadOnlyList<PlatformUserPlaylist> playlists,
        PlatformLibraryState state,
        string message,
        bool partial = false)
    {
        var now = DateTimeOffset.UtcNow;
        var probe = new PlatformProbeSnapshot(PlatformId.QqMusic, credentialState, displayName, nativeUserId,
            PlatformMembershipState.Unknown, "会员状态需以具体歌曲播放权益为准", now, message);
        return new PlatformLibrarySnapshot(PlatformId.QqMusic, probe, playlists, state,
            SearchDataOrigin.Live, now, message, partial);
    }

    private static CredentialState CredentialStateFor(PlatformErrorCode? code) => code switch
    {
        PlatformErrorCode.Unauthorized or PlatformErrorCode.CredentialExpired => CredentialState.Expired,
        _ => CredentialState.Error
    };

    private static PlatformLibraryState LibraryStateFor(PlatformErrorCode? code) => code switch
    {
        PlatformErrorCode.Unauthorized or PlatformErrorCode.CredentialExpired => PlatformLibraryState.NotAuthorized,
        _ => PlatformLibraryState.Error
    };

    private static string SafeProfileMessage(PlatformErrorCode? code) => code switch
    {
        PlatformErrorCode.Unauthorized or PlatformErrorCode.CredentialExpired => "QQ 音乐登录已过期，请重新登录",
        _ => "QQ 音乐账号信息暂时无法加载"
    };

    private static bool TryGetIdentity(
        string? session,
        out string uin,
        out IReadOnlyDictionary<string, string> cookies)
    {
        cookies = string.IsNullOrWhiteSpace(session)
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : ProviderCredentialReader.ParseCookieHeader(session);
        uin = First(cookies, "uin", "wxuin")?.TrimStart('o', 'O') ?? string.Empty;
        return long.TryParse(uin, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 &&
               !string.IsNullOrWhiteSpace(First(cookies, "qm_keyst", "qqmusic_key", "p_skey", "skey"));
    }

    private static bool IsFavoriteName(string value) =>
        value.Contains("我喜欢", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("我的喜欢", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("喜欢的音乐", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikePlaylist(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object &&
        (new[]
        {
            "songnum", "song_count", "song_cnt", "songcount", "songNum", "total_song_num", "totalSongNum",
            "logo", "picurl", "diss_cover", "dir_pic_url", "cover", "cover_url", "songlist"
        }.Any(name => value.TryGetProperty(name, out _)));

    private static bool IsSafeMid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');

    private static string NormalizeImage(string? raw)
    {
        var value = raw?.Trim().Replace("\\/", "/") ?? string.Empty;
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) value = "https://" + value[7..];
        else if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.AbsoluteUri : string.Empty;
    }

    private static string? First(IReadOnlyDictionary<string, string> values, params string[] names) =>
        names.Select(name => values.TryGetValue(name, out var value) ? value : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? FirstString(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!value.TryGetProperty(name, out var property)) continue;
            var text = property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return null;
    }

    private static string? FindString(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.NameEquals(name) && property.Value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(property.Value.GetString())) return property.Value.GetString();
                var nested = FindString(property.Value, name);
                if (nested is not null) return nested;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) { var nested = FindString(item, name); if (nested is not null) return nested; }
        return null;
    }

    private static JsonElement? FindArray(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                if (property.NameEquals(name) && property.Value.ValueKind == JsonValueKind.Array) return property.Value;
                if (FindArray(property.Value, name) is { } nested) return nested;
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (FindArray(item, name) is { } nested) return nested;
        return null;
    }

    private static JsonElement? FindObject(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                if (property.NameEquals(name) && property.Value.ValueKind == JsonValueKind.Object) return property.Value;
                if (FindObject(property.Value, name) is { } nested) return nested;
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (FindObject(item, name) is { } nested) return nested;
        return null;
    }

    private static long FirstPositiveInt64(JsonElement value, params string[] names)
    {
        foreach (var name in names)
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
                ((property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number) && number > 0) ||
                 (property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), out number) && number > 0)))
                return number;
        return 0;
    }

    private static int FirstNonNegativeInt(JsonElement value, params string[] names)
    {
        foreach (var name in names)
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
                ((property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number) && number >= 0) ||
                 (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out number) && number >= 0)))
                return number;
        return -1;
    }

    private static bool TryGetInt(JsonElement value, string name, out int result)
    {
        result = 0;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property)) return false;
        return property.ValueKind == JsonValueKind.Number
            ? property.TryGetInt32(out result)
            : property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out result);
    }

    private static int Gtk(string value)
    {
        long hash = 5381;
        foreach (var character in value) hash += (hash << 5) + character;
        return (int)(hash & 0x7fffffff);
    }

    private static string StableKey(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
}
