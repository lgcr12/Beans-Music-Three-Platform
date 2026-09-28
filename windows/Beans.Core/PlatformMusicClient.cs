using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Beans.Core;

public sealed record PlatformProfile(string Id, string Nickname);

/// <summary>Runs third-party requests on the client. Cookies never pass through the Beans server.</summary>
public sealed class PlatformMusicClient(HttpClient http)
{
    private static readonly object DiagnosticLock = new();
    public Task<CredentialProbeOutcome> ProbeAsync(
        string provider,
        IReadOnlyDictionary<string, string> cookies,
        CancellationToken ct = default) => provider switch
        {
            "qq" => ProbeQqAsync(cookies, ct),
            "netease" => ProbeNeteaseAsync(cookies, ct),
            _ => Task.FromResult(new CredentialProbeOutcome(CredentialProbeStatus.NotAuthorized, false, null, false, "unsupported_platform"))
        };

    public async Task<(PlatformProfile Profile, IReadOnlyList<MirrorPlaylist> Playlists)> ValidateAndLoadAsync(
        string provider,
        IReadOnlyDictionary<string, string> cookies,
        CancellationToken ct = default)
    {
        if (!PlatformCredentialPolicy.LooksUsable(provider, cookies))
            throw new InvalidOperationException("平台登录凭证不完整");
        return provider switch
        {
            "qq" => await LoadQqAsync(cookies, ct),
            "netease" => await LoadNeteaseAsync(cookies, ct),
            _ => throw new NotSupportedException("不支持的平台")
        };
    }

    public async Task<IReadOnlyList<PlatformPlaylistTrack>> LoadPlaylistTracksAsync(
        string provider,
        long playlistId,
        IReadOnlyDictionary<string, string> cookies,
        string? nativeKind = null,
        CancellationToken ct = default)
    {
        if (playlistId <= 0) throw new ArgumentOutOfRangeException(nameof(playlistId));
        if (!PlatformCredentialPolicy.LooksUsable(provider, cookies))
            throw new UnauthorizedAccessException("平台授权需要重新登录");
        return provider switch
        {
            "qq" => await LoadQqPlaylistTracksAsync(playlistId, nativeKind, cookies, ct),
            "netease" => await LoadNeteasePlaylistTracksAsync(playlistId, cookies, ct),
            _ => throw new NotSupportedException("不支持的平台")
        };
    }

    public Task<IReadOnlyList<PlatformPlaylistTrack>> LoadPlaylistTracksAsync(
        string provider,
        long playlistId,
        IReadOnlyDictionary<string, string> cookies,
        CancellationToken ct) => LoadPlaylistTracksAsync(provider, playlistId, cookies, null, ct);

    public async Task<PlatformPlaybackSource> ResolvePlaybackAsync(
        PlatformPlaylistTrack track,
        IReadOnlyDictionary<string, string> cookies,
        CancellationToken ct = default)
    {
        if (!PlatformCredentialPolicy.LooksUsable(track.Source, cookies))
            throw new UnauthorizedAccessException("平台授权需要重新登录");
        return track.Source switch
        {
            "qq" => await ResolveQqPlaybackAsync(track, cookies, ct),
            "netease" => await ResolveNeteasePlaybackAsync(track, cookies, ct),
            _ => throw new NotSupportedException("不支持的平台")
        };
    }

    public async Task DownloadPlaybackAsync(
        PlatformPlaybackSource source,
        string provider,
        IReadOnlyDictionary<string, string> cookies,
        Stream destination,
        CancellationToken ct = default)
    {
        using var request = CreatePlaybackRequest(source.Uri, provider, cookies);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException($"{(provider == "qq" ? "QQ 音乐" : "网易云音乐")}播放授权已失效");
        response.EnsureSuccessStatusCode();

        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
            contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("平台返回的不是可播放音频");

        await response.Content.CopyToAsync(destination, ct);
        await destination.FlushAsync(ct);
        if (destination.CanSeek && destination.Length == 0)
            throw new InvalidOperationException("平台返回了空音频文件");
    }

    private async Task<IReadOnlyList<PlatformPlaylistTrack>> LoadQqPlaylistTracksAsync(
        long playlistId,
        string? nativeKind,
        IReadOnlyDictionary<string, string> cookies,
        CancellationToken ct)
    {
        var uin = (cookies.GetValueOrDefault("uin") ?? cookies.GetValueOrDefault("wxuin") ?? "0").TrimStart('o');
        var gtk = Gtk(FirstCookie(cookies, "p_skey", "skey"));
        if (nativeKind == "favorite")
            return await LoadQqFavoriteTracksAsync(uin, gtk, cookies, ct);
        if (nativeKind == "dir")
            return await LoadQqDirectoryTracksAsync(playlistId, uin, gtk, cookies, ct);

        var legacySongListKnown = false;
        try
        {
            var url = $"https://c.y.qq.com/qzone/fcg-bin/fcg_ucc_getcdinfo_byids_cp.fcg?type=1&json=1&utf8=1&onlysong=0&new_format=1&song_begin=0&song_num=9999&disstid={playlistId}&g_tk={gtk}&loginUin={Uri.EscapeDataString(uin)}&hostUin=0&format=json&inCharset=utf8&outCharset=utf-8&notice=0&platform=yqq.json&needNewCode=0";
            using var request = Create(HttpMethod.Get, url, cookies, "https://y.qq.com/");
            using var response = await http.SendAsync(request, ct);
            WriteQqDiagnostic("diss", (int)response.StatusCode, null, null);
            if (response.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
                var legacySongs = FindArray(json.RootElement, "songlist");
                WriteQqDiagnostic("diss-body", (int)response.StatusCode, FindInt(json.RootElement, "code"), legacySongs is not null);
                if (IsQqSuccess(json.RootElement) && legacySongs is { } songs)
                {
                    legacySongListKnown = true;
                    var tracks = ParseQqTracks(songs);
                    if (tracks.Count > 0) return tracks;
                }
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The legacy endpoint is frequently throttled. Continue with musicu.
        }

        var musicuTracks = await LoadQqMusicuPlaylistTracksAsync(playlistId, cookies, ct);
        if (musicuTracks.Count > 0) return musicuTracks;
        if (legacySongListKnown) return [];

        // QQ may expose a favorite folder as diss/tid metadata while only the
        // legacy directory endpoint returns its tracks. Missing songlist is an
        // unknown response shape, so try the directory endpoint before failing.
        return await LoadQqDirectoryTracksAsync(playlistId, uin, gtk, cookies, ct);
    }

    private async Task<IReadOnlyList<PlatformPlaylistTrack>> LoadQqFavoriteTracksAsync(
        string uin,
        int gtk,
        IReadOnlyDictionary<string, string> cookies,
        CancellationToken ct)
    {
        var url = $"https://c.y.qq.com/splcloud/fcgi-bin/fcg_musiclist_getmyfav.fcg?dirid=201&dirinfo=1&g_tk={gtk}&loginUin={Uri.EscapeDataString(uin)}&format=json&utf8=1";
        using var request = Create(HttpMethod.Get, url, cookies, "https://y.qq.com/n/yqq/playlist");
        using var response = await http.SendAsync(request, ct);
        WriteQqDiagnostic("favorite-map", (int)response.StatusCode, null, null);
        await EnsurePlatformSuccessAsync(response, "QQ 音乐");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        if (!IsQqSuccess(json.RootElement))
            throw new HttpRequestException("QQ 音乐未接受我喜欢歌单请求");

        if (FindObject(json.RootElement, "mapmid") is { } mapMid)
        {
            var songMids = mapMid.EnumerateObject()
                .Select(property => property.Name)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            WriteQqDiagnostic("favorite-map-body", (int)response.StatusCode, FindInt(json.RootElement, "code"), songMids.Length > 0);
            if (songMids.Length > 0)
                return await LoadQqSongDetailsAsync(songMids, cookies, ct);
        }

        var mapId = FindPositiveInt64(json.RootElement, "map");
        WriteQqDiagnostic("favorite-map-body", (int)response.StatusCode, FindInt(json.RootElement, "code"), mapId > 0);
        if (mapId <= 0) throw new HttpRequestException("QQ 音乐未返回我喜欢歌曲标识");
        return await LoadQqPlaylistTracksAsync(mapId, "diss", cookies, ct);
    }

    private async Task<IReadOnlyList<PlatformPlaylistTrack>> LoadQqSongDetailsAsync(
        IReadOnlyList<string> songMids,
        IReadOnlyDictionary<string, string> cookies,
        CancellationToken ct)
    {
        var tracks = new List<PlatformPlaylistTrack>(songMids.Count);
        // QQ rejects larger multi-request envelopes even though it still returns HTTP 200.
        const int batchSize = 10;
        for (var offset = 0; offset < songMids.Count; offset += batchSize)
        {
            var batch = songMids.Skip(offset).Take(batchSize).ToArray();
            var payload = new Dictionary<string, object>
            {
                ["comm"] = new Dictionary<string, object> { ["ct"] = 24, ["cv"] = 0 }
            };
            for (var index = 0; index < batch.Length; index++)
            {
                payload[$"req_{index}"] = new Dictionary<string, object>
                {
                    ["module"] = "music.pf_song_detail_svr",
                    ["method"] = "get_song_detail_yqq",
                    ["param"] = new Dictionary<string, object> { ["song_mid"] = batch[index] }
                };
            }

            using var request = Create(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg", cookies, "https://y.qq.com/");
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, ct);
            WriteQqDiagnostic("musicu-song-details", (int)response.StatusCode, null, null);
            await EnsurePlatformSuccessAsync(response, "QQ 音乐");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            for (var index = 0; index < batch.Length; index++)
            {
                if (!json.RootElement.TryGetProperty($"req_{index}", out var block) ||
                    !block.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("track_info", out var trackInfo)) continue;
                if (ParseQqTrack(trackInfo) is { } track) tracks.Add(track);
            }
        }
        WriteQqDiagnostic("musicu-song-details-body", 200, 0, tracks.Count > 0);
        return tracks;
    }

    private async Task<IReadOnlyList<PlatformPlaylistTrack>> LoadQqMusicuPlaylistTracksAsync(
        long playlistId,
        IReadOnlyDictionary<string, string> cookies,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object>
        {
            ["comm"] = new Dictionary<string, object> { ["ct"] = 24, ["cv"] = 0 },
            ["req_1"] = new Dictionary<string, object>
            {
                ["module"] = "music.playlist.PlayListDataServer",
                ["method"] = "GetPlaylistDetail",
                ["param"] = new Dictionary<string, object>
                {
                    ["id"] = playlistId,
                    ["uin"] = 0,
                    ["song_begin"] = 0,
                    ["song_num"] = 9999
                }
            }
        };
        using var request = Create(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg", cookies, "https://y.qq.com/");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        WriteQqDiagnostic("musicu-playlist", (int)response.StatusCode, null, null);
        await EnsurePlatformSuccessAsync(response, "QQ 音乐");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var songs = FindArray(json.RootElement, "songlist");
        WriteQqDiagnostic("musicu-playlist-body", (int)response.StatusCode, FindInt(json.RootElement, "code"), songs is not null);
        return songs is { } list ? ParseQqTracks(list) : [];
    }

    private async Task<IReadOnlyList<PlatformPlaylistTrack>> LoadQqDirectoryTracksAsync(
        long playlistId,
        string uin,
        int gtk,
        IReadOnlyDictionary<string, string> cookies,
        CancellationToken ct)
    {
        var directoryUrl = $"https://c.y.qq.com/splcloud/fcgi-bin/fcg_musiclist_getmyfav.fcg?format=json&cid=205360955&reqtype=3&uin={Uri.EscapeDataString(uin)}&dirid={playlistId}&songstatus=1&start=0&num=9999&g_tk={gtk}&loginUin={Uri.EscapeDataString(uin)}&platform=yqq.json";
        using var directoryRequest = Create(HttpMethod.Get, directoryUrl, cookies, "https://y.qq.com/");
        using var directoryResponse = await http.SendAsync(directoryRequest, ct);
        WriteQqDiagnostic("dir", (int)directoryResponse.StatusCode, null, null);
        await EnsurePlatformSuccessAsync(directoryResponse, "QQ 音乐");
        using var directoryJson = JsonDocument.Parse(await directoryResponse.Content.ReadAsStreamAsync(ct));
        WriteQqDiagnostic("dir-body", (int)directoryResponse.StatusCode, FindInt(directoryJson.RootElement, "code"), FindArray(directoryJson.RootElement, "songlist") is not null);
        if (!IsQqSuccess(directoryJson.RootElement))
            throw new HttpRequestException("QQ 音乐未接受歌单详情请求");
        var directorySongList = FindArray(directoryJson.RootElement, "songlist");
        if (directorySongList is { } songList)
        {
            var tracks = ParseQqTracks(songList);
            if (tracks.Count > 0) return tracks;
        }

        if (FindObject(directoryJson.RootElement, "mapmid") is { } mapMid)
        {
            var songMids = mapMid.EnumerateObject()
                .Select(property => property.Name)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (songMids.Length > 0)
                return await LoadQqSongDetailsAsync(songMids, cookies, ct);
        }

        if (directorySongList is { } knownSongList && knownSongList.GetArrayLength() == 0) return [];
        throw new HttpRequestException("QQ 音乐未返回歌单歌曲数据");
    }

    private static void WriteQqDiagnostic(string endpoint, int httpStatus, int? responseCode, bool? hasSongList)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeansMusic");
            Directory.CreateDirectory(directory);
            var line = $"{DateTimeOffset.UtcNow:O} endpoint={endpoint} http={httpStatus} code={responseCode?.ToString() ?? "none"} songlist={hasSongList?.ToString() ?? "unknown"}{Environment.NewLine}";
            lock (DiagnosticLock) File.AppendAllText(Path.Combine(directory, "platform-diagnostics.log"), line);
        }
        catch
        {
            // Diagnostics must never affect playlist loading.
        }
    }

    private static IReadOnlyList<PlatformPlaylistTrack> ParseQqTracks(JsonElement root)
    {
        var songs = root.ValueKind == JsonValueKind.Array ? root : FindArray(root, "songlist") ?? default;
        if (songs.ValueKind != JsonValueKind.Array) return [];
        var result = new List<PlatformPlaylistTrack>();
        foreach (var rawItem in songs.EnumerateArray())
        {
            if (ParseQqTrack(rawItem) is { } track) result.Add(track);
        }
        return result;
    }

    private static PlatformPlaylistTrack? ParseQqTrack(JsonElement rawItem)
    {
        if (rawItem.ValueKind != JsonValueKind.Object) return null;
        var item = rawItem;
        foreach (var wrapperName in new[] { "track_info", "musicData", "songInfo", "data" })
        {
            if (item.TryGetProperty(wrapperName, out var wrapped) && wrapped.ValueKind == JsonValueKind.Object)
            {
                item = wrapped;
                break;
            }
        }
        var album = item.TryGetProperty("album", out var nestedAlbum) && nestedAlbum.ValueKind == JsonValueKind.Object ? nestedAlbum : default;
        var albumMid = GetString(item, "albummid") ?? GetString(item, "albumMid") ?? GetString(item, "Falbum_mid") ??
            (album.ValueKind == JsonValueKind.Object ? GetString(album, "mid") : null);
        var cover = string.IsNullOrWhiteSpace(albumMid)
            ? null
            : UriOrNull($"https://y.gtimg.cn/music/photo_new/T002R300x300M000{albumMid}.jpg");
        var id = GetString(item, "songmid") ?? GetString(item, "mid") ?? GetString(item, "Fsong_mid");
        if (string.IsNullOrWhiteSpace(id))
        {
            var numericId = GetInt64(item, "songid");
            if (numericId == 0) numericId = GetInt64(item, "Fsong_id");
            if (numericId > 0) id = numericId.ToString();
        }
        if (string.IsNullOrWhiteSpace(id)) return null;
        var artist = JoinArtists(item, "singer");
        if (artist == "未知歌手") artist = GetString(item, "Fsinger_name") ?? artist;
        return new PlatformPlaylistTrack(
            id,
            GetString(item, "songname") ?? GetString(item, "name") ?? GetString(item, "title") ?? GetString(item, "Fsong_name") ?? "未知歌曲",
            artist,
            GetString(item, "albumname") ?? GetString(item, "Falbum_name") ?? (album.ValueKind == JsonValueKind.Object ? GetString(album, "name") : null) ?? "未知专辑",
            cover,
            "qq",
            GetString(item, "strMediaMid") ?? GetString(item, "media_mid") ??
                (item.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.Object ? GetString(file, "media_mid") : null) ?? id);
    }

    private async Task<IReadOnlyList<PlatformPlaylistTrack>> LoadNeteasePlaylistTracksAsync(
        long playlistId,
        IReadOnlyDictionary<string, string> cookies,
        CancellationToken ct)
    {
        var url = $"https://music.163.com/api/v6/playlist/detail?id={playlistId}&n=100000&s=8";
        using var request = Create(HttpMethod.Get, url, cookies, "https://music.163.com/");
        using var response = await http.SendAsync(request, ct);
        await EnsurePlatformSuccessAsync(response, "网易云音乐");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var root = json.RootElement;
        if (root.TryGetProperty("code", out var code) && code.TryGetInt32(out var value) && value is 301 or 401 or 403)
            throw new UnauthorizedAccessException("网易云音乐授权已失效");
        if (!root.TryGetProperty("playlist", out var playlist) || playlist.ValueKind != JsonValueKind.Object)
            return [];

        var result = playlist.TryGetProperty("tracks", out var songs) && songs.ValueKind == JsonValueKind.Array
            ? ParseNeteaseTracks(songs).ToList()
            : [];
        if (!playlist.TryGetProperty("trackIds", out var trackIds) || trackIds.ValueKind != JsonValueKind.Array)
            return result;

        var loadedIds = result.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var missingIds = trackIds.EnumerateArray()
            .Select(item => GetInt64(item, "id"))
            .Where(id => id > 0 && !loadedIds.Contains(id.ToString()))
            .Distinct()
            .ToArray();
        if (missingIds.Length == 0) return result;

        result.AddRange(await LoadNeteaseSongDetailsAsync(missingIds, cookies, ct));
        return result
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    private async Task<IReadOnlyList<PlatformPlaylistTrack>> LoadNeteaseSongDetailsAsync(
        IReadOnlyList<long> songIds,
        IReadOnlyDictionary<string, string> cookies,
        CancellationToken ct)
    {
        var result = new List<PlatformPlaylistTrack>(songIds.Count);
        const int batchSize = 500;
        for (var offset = 0; offset < songIds.Count; offset += batchSize)
        {
            var batch = songIds.Skip(offset).Take(batchSize).ToArray();
            var descriptors = batch.Select(id => new Dictionary<string, long> { ["id"] = id }).ToArray();
            using var request = Create(HttpMethod.Post, "https://music.163.com/api/v3/song/detail", cookies, "https://music.163.com/");
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["c"] = JsonSerializer.Serialize(descriptors),
                ["ids"] = JsonSerializer.Serialize(batch)
            });
            using var response = await http.SendAsync(request, ct);
            await EnsurePlatformSuccessAsync(response, "网易云音乐");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            if (json.RootElement.TryGetProperty("code", out var code) && code.TryGetInt32(out var value) && value is 301 or 401 or 403)
                throw new UnauthorizedAccessException("网易云音乐授权已失效");
            if (json.RootElement.TryGetProperty("songs", out var songs) && songs.ValueKind == JsonValueKind.Array)
                result.AddRange(ParseNeteaseTracks(songs));
        }
        return result;
    }

    private static IReadOnlyList<PlatformPlaylistTrack> ParseNeteaseTracks(JsonElement songs) =>
        songs.EnumerateArray().Select(item =>
        {
            var album = item.TryGetProperty("al", out var modernAlbum) ? modernAlbum :
                item.TryGetProperty("album", out var legacyAlbum) ? legacyAlbum : default;
            return new PlatformPlaylistTrack(
                GetInt64(item, "id").ToString(),
                GetString(item, "name") ?? "未知歌曲",
                JoinArtists(item, item.TryGetProperty("ar", out _) ? "ar" : "artists"),
                album.ValueKind == JsonValueKind.Object ? GetString(album, "name") ?? "未知专辑" : "未知专辑",
                album.ValueKind == JsonValueKind.Object ? UriOrNull(GetString(album, "picUrl")) : null,
                "netease");
        }).Where(item => item.Id != "0").ToArray();

    private async Task<(PlatformProfile, IReadOnlyList<MirrorPlaylist>)> LoadNeteaseAsync(IReadOnlyDictionary<string, string> cookies, CancellationToken ct)
    {
        using var account = Create(HttpMethod.Get, "https://music.163.com/api/nuser/account/get", cookies, "https://music.163.com/");
        using var accountResponse = await http.SendAsync(account, ct);
        await EnsurePlatformSuccessAsync(accountResponse, "网易云音乐");
        using var accountJson = JsonDocument.Parse(await accountResponse.Content.ReadAsStreamAsync(ct));
        var root = accountJson.RootElement;
        if (root.TryGetProperty("code", out var code) && code.TryGetInt32(out var codeValue) && codeValue is 301 or 401 or 403)
            throw new UnauthorizedAccessException("网易云音乐授权已失效");
        if (!root.TryGetProperty("profile", out var profile) || profile.ValueKind != JsonValueKind.Object)
            throw new UnauthorizedAccessException("网易云音乐授权需要重新登录");
        var userId = GetInt64(profile, "userId");
        if (userId <= 0) throw new UnauthorizedAccessException("网易云音乐授权需要重新登录");
        var nickname = GetString(profile, "nickname") ?? "网易云用户";

        using var playlistsRequest = Create(HttpMethod.Get, $"https://music.163.com/api/user/playlist?uid={userId}&limit=1000&offset=0", cookies, "https://music.163.com/");
        using var playlistsResponse = await http.SendAsync(playlistsRequest, ct);
        await EnsurePlatformSuccessAsync(playlistsResponse, "网易云音乐");
        using var playlistsJson = JsonDocument.Parse(await playlistsResponse.Content.ReadAsStreamAsync(ct));
        var playlists = new List<MirrorPlaylist>();
        if (playlistsJson.RootElement.TryGetProperty("playlist", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                var creator = item.TryGetProperty("creator", out var owner) ? GetString(owner, "nickname") : null;
                playlists.Add(new(GetInt64(item, "id"), GetString(item, "name") ?? "未命名歌单", UriOrNull(GetString(item, "coverImgUrl")), GetInt32(item, "trackCount"), creator ?? nickname, "netease", true));
            }
        }
        return (new PlatformProfile(userId.ToString(), nickname), playlists);
    }

    private async Task<PlatformPlaybackSource> ResolveQqPlaybackAsync(
        PlatformPlaylistTrack track,
        IReadOnlyDictionary<string, string> cookies,
        CancellationToken ct)
    {
        var uin = (cookies.GetValueOrDefault("uin") ?? cookies.GetValueOrDefault("wxuin") ?? "0").TrimStart('o');
        if (!long.TryParse(uin, out var numericUin) || numericUin <= 0)
            throw new UnauthorizedAccessException("QQ 音乐授权需要重新登录");
        var loginKey = FirstCookie(cookies, "qm_keyst", "qqmusic_key", "p_skey", "skey");
        if (string.IsNullOrWhiteSpace(loginKey))
            throw new UnauthorizedAccessException("QQ 音乐播放凭证缺失，请重新授权");

        var mediaMid = string.IsNullOrWhiteSpace(track.MediaId) ? track.Id : track.MediaId;
        var guidBytes = SHA256.HashData(Encoding.UTF8.GetBytes("beans-qq-guid:" + uin));
        var guid = (BitConverter.ToUInt32(guidBytes, 0) % 900_000_000 + 100_000_000).ToString();
        foreach (var quality in new[] { "M800", "M500", "C400", "F000" })
        {
            var extension = quality.StartsWith('F') ? "flac" : quality.StartsWith('C') ? "m4a" : "mp3";
            var filename = $"{quality}{mediaMid}.{extension}";
            var data = JsonSerializer.Serialize(new
            {
                comm = new { uin = numericUin, format = "json", ct = 19, cv = 0, g_tk = Gtk(FirstCookie(cookies, "p_skey", "skey")), authst = loginKey },
                req = new { module = "CDN.SrfCdnDispatchServer", method = "GetCdnDispatch", param = new { guid, calltype = 0, userip = "" } },
                req_0 = new { module = "vkey.GetVkeyServer", method = "CgiGetVkey", param = new { filename = new[] { filename }, guid, songmid = new[] { track.Id }, songtype = new[] { 0 }, uin, loginflag = 1, platform = "20" } }
            });
            using var request = Create(HttpMethod.Get, "https://u.y.qq.com/cgi-bin/musicu.fcg?format=json&data=" + Uri.EscapeDataString(data), cookies, "https://y.qq.com/");
            request.Headers.TryAddWithoutValidation("Origin", "https://y.qq.com");
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) continue;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            if (!json.RootElement.TryGetProperty("req_0", out var block) ||
                !block.TryGetProperty("data", out var responseData) ||
                !responseData.TryGetProperty("midurlinfo", out var infos) || infos.ValueKind != JsonValueKind.Array) continue;
            var info = infos.EnumerateArray().FirstOrDefault(value => !string.IsNullOrWhiteSpace(GetString(value, "purl")));
            if (info.ValueKind == JsonValueKind.Undefined) continue;
            var purl = GetString(info, "purl")!;
            var bases = responseData.TryGetProperty("sip", out var sip) && sip.ValueKind == JsonValueKind.Array
                ? sip.EnumerateArray().Select(value => value.GetString()).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToList()
                : [];
            bases.AddRange(["https://isure.stream.qqmusic.qq.com/", "https://dl.stream.qqmusic.qq.com/", "https://ws.stream.qqmusic.qq.com/"]);
            foreach (var item in bases.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var candidate = purl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? purl
                    : item.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase).TrimEnd('/') + "/" + purl.TrimStart('/');
                if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) continue;
                using var probe = CreatePlaybackRequest(uri, "qq", cookies);
                probe.Headers.Range = new RangeHeaderValue(0, 1);
                probe.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                using var cdn = await http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct);
                if (cdn.StatusCode is System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.PartialContent)
                    return new PlatformPlaybackSource(uri, quality);
                if (purl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) break;
            }
        }
        throw new InvalidOperationException("QQ 音乐未返回可播放地址，歌曲可能需要会员权限");
    }

    private async Task<PlatformPlaybackSource> ResolveNeteasePlaybackAsync(
        PlatformPlaylistTrack track,
        IReadOnlyDictionary<string, string> cookies,
        CancellationToken ct)
    {
        if (!long.TryParse(track.Id, out var songId) || songId <= 0)
            throw new InvalidOperationException("网易云音乐歌曲标识无效");
        foreach (var bitrate in new[] { 320000, 192000, 128000 })
        {
            var url = $"https://music.163.com/api/song/enhance/player/url?id={songId}&ids=%5B{songId}%5D&br={bitrate}";
            using var request = Create(HttpMethod.Get, url, cookies, "https://music.163.com/");
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) continue;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) continue;
            var item = data.EnumerateArray().FirstOrDefault();
            var playbackUrl = item.ValueKind == JsonValueKind.Object ? GetString(item, "url") : null;
            if (Uri.TryCreate(playbackUrl, UriKind.Absolute, out var uri))
                return new PlatformPlaybackSource(uri, bitrate.ToString());
        }
        throw new InvalidOperationException("网易云音乐未返回可播放地址，歌曲可能需要会员权限");
    }

    private async Task<(PlatformProfile, IReadOnlyList<MirrorPlaylist>)> LoadQqAsync(IReadOnlyDictionary<string, string> cookies, CancellationToken ct)
    {
        var uin = (cookies.TryGetValue("uin", out var raw) ? raw : cookies.GetValueOrDefault("wxuin") ?? "0").TrimStart('o');
        if (!long.TryParse(uin, out var numericUin) || numericUin <= 0) throw new UnauthorizedAccessException("QQ 音乐授权需要重新登录");
        var profileUrl = $"https://c.y.qq.com/rsc/fcgi-bin/fcg_get_profile_homepage.fcg?cid=205360838&userid={Uri.EscapeDataString(uin)}&reqfrom=1&g_tk=5381&loginUin={Uri.EscapeDataString(uin)}&format=json";
        using var profileRequest = Create(HttpMethod.Get, profileUrl, cookies, "https://y.qq.com/");
        using var profileResponse = await http.SendAsync(profileRequest, ct);
        await EnsurePlatformSuccessAsync(profileResponse, "QQ 音乐");
        using var profileJson = JsonDocument.Parse(await profileResponse.Content.ReadAsStreamAsync(ct));
        if (!IsQqSuccess(profileJson.RootElement)) throw new UnauthorizedAccessException("QQ 音乐授权需要重新登录");
        var nickname = FindString(profileJson.RootElement, "nick") ?? FindString(profileJson.RootElement, "nickname") ?? "QQ 音乐用户";

        var listUrl = $"https://c.y.qq.com/fav/fcgi-bin/fcg_get_profile_order_asset.fcg?ct=20&cid=205360956&userid={Uri.EscapeDataString(uin)}&reqtype=3&sin=0&ein=9999&format=json";
        using var listRequest = Create(HttpMethod.Get, listUrl, cookies, "https://y.qq.com/portal/profile.html");
        using var listResponse = await http.SendAsync(listRequest, ct);
        await EnsurePlatformSuccessAsync(listResponse, "QQ 音乐");
        using var listJson = JsonDocument.Parse(await listResponse.Content.ReadAsStreamAsync(ct));
        var result = new List<MirrorPlaylist>();
        CollectQqPlaylists(profileJson.RootElement, nickname, result);
        CollectQqPlaylists(listJson.RootElement, nickname, result);
        var createdUrl = $"https://c.y.qq.com/rsc/fcgi-bin/fcg_user_created_diss?hostuin={Uri.EscapeDataString(uin)}&sin=0&size=999&g_tk={Gtk(FirstCookie(cookies, "p_skey", "skey"))}&format=json";
        try
        {
            using var createdRequest = Create(HttpMethod.Get, createdUrl, cookies, "https://y.qq.com/portal/profile.html");
            using var createdResponse = await http.SendAsync(createdRequest, ct);
            if (createdResponse.IsSuccessStatusCode)
            {
                using var createdJson = JsonDocument.Parse(await createdResponse.Content.ReadAsStreamAsync(ct));
                CollectQqPlaylists(createdJson.RootElement, nickname, result);
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The legacy created-playlist endpoint is supplementary; keep the primary result if unavailable.
        }
        result = result
            .Where(item => item.Id > 0)
            .GroupBy(item => $"{item.NativeKind ?? "unknown"}:{item.Id}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.TrackCount).First())
            .ToList();
        return (new PlatformProfile(uin, nickname), result);
    }

    private static void CollectQqPlaylists(JsonElement value, string nickname, List<MirrorPlaylist> target)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var name = GetString(value, "dissname") ?? GetString(value, "dirname") ?? GetString(value, "diss_name") ??
                GetString(value, "name") ?? GetString(value, "title");
            var id = GetInt64(value, "dissid");
            var nativeKind = id > 0 ? "diss" : null;
            if (id == 0)
            {
                id = GetInt64(value, "dirid");
                if (id > 0) nativeKind = "dir";
            }
            if (id == 0 && !string.IsNullOrWhiteSpace(name))
            {
                id = GetInt64(value, "tid");
                if (id > 0) nativeKind = "tid";
            }
            if (id == 0)
            {
                id = GetInt64(value, "diss_id");
                if (id > 0) nativeKind = "diss";
            }
            if (id == 0 && !string.IsNullOrWhiteSpace(name))
            {
                id = GetInt64(value, "id");
                if (id > 0) nativeKind = "diss";
            }
            if (id > 0 && !string.IsNullOrWhiteSpace(name))
            {
                if (nativeKind == "dir" && id == 201 || IsQqFavoritePlaylistName(name)) nativeKind = "favorite";
                var cover = GetString(value, "logo") ?? GetString(value, "picurl") ?? GetString(value, "diss_cover") ??
                    GetString(value, "dir_pic_url") ?? GetString(value, "pic_url") ?? GetString(value, "cover") ??
                    GetString(value, "cover_url") ?? GetString(value, "headurl") ?? GetString(value, "imgurl");
                var count = GetInt32Any(value, "songnum", "song_count", "song_cnt", "songcount", "songNum", "total_song_num", "totalSongNum");
                if (count == 0 && FindArray(value, "songlist") is { } songs) count = songs.GetArrayLength();
                target.Add(new MirrorPlaylist(id, name, UriOrNull(cover), count, nickname, "qq", count > 0, nativeKind));
            }
            foreach (var property in value.EnumerateObject()) CollectQqPlaylists(property.Value, nickname, target);
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) CollectQqPlaylists(item, nickname, target);
        }
    }

    private async Task<CredentialProbeOutcome> ProbeNeteaseAsync(IReadOnlyDictionary<string, string> cookies, CancellationToken ct)
    {
        if (!PlatformCredentialPolicy.LooksUsable("netease", cookies))
            return new(CredentialProbeStatus.NotAuthorized, false, null, false, "not_authorized");
        try
        {
            using var account = Create(HttpMethod.Get, "https://music.163.com/api/nuser/account/get", cookies, "https://music.163.com/");
            using var accountResponse = await http.SendAsync(account, HttpCompletionOption.ResponseHeadersRead, ct);
            if (accountResponse.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return new(CredentialProbeStatus.Invalid, false, null, false, "profile_unauthorized", HttpStatus: (int)accountResponse.StatusCode);
            if (!accountResponse.IsSuccessStatusCode)
                return new(CredentialProbeStatus.NetworkError, false, null, null, "profile_http_error", HttpStatus: (int)accountResponse.StatusCode);
            using var accountJson = JsonDocument.Parse(await accountResponse.Content.ReadAsStreamAsync(ct));
            var root = accountJson.RootElement;
            if (root.TryGetProperty("code", out var code) && code.TryGetInt32(out var codeValue) && codeValue is 301 or 401 or 403)
                return new(CredentialProbeStatus.Invalid, false, null, false, $"profile_{codeValue}");
            if (!root.TryGetProperty("profile", out var profile) || profile.ValueKind != JsonValueKind.Object || GetInt64(profile, "userId") <= 0)
                return new(CredentialProbeStatus.Invalid, false, null, false, "profile_missing");

            using var vip = Create(HttpMethod.Get, "https://music.163.com/api/music-vip-membership/client/vip/info", cookies, "https://music.163.com/");
            using var vipResponse = await http.SendAsync(vip, HttpCompletionOption.ResponseHeadersRead, ct);
            if (vipResponse.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return new(CredentialProbeStatus.Invalid, false, null, false, "membership_unauthorized", HttpStatus: (int)vipResponse.StatusCode);
            if (!vipResponse.IsSuccessStatusCode)
                return new(CredentialProbeStatus.NetworkError, true, null, null, "membership_http_error", HttpStatus: (int)vipResponse.StatusCode);
            using var vipJson = JsonDocument.Parse(await vipResponse.Content.ReadAsStreamAsync(ct));
            var vipRoot = vipJson.RootElement;
            if (vipRoot.TryGetProperty("code", out var vipCode) && vipCode.TryGetInt32(out var vipCodeValue) && vipCodeValue is 301 or 401 or 403)
                return new(CredentialProbeStatus.Invalid, false, null, false, $"membership_{vipCodeValue}");
            var vipLevel = FindInt(vipRoot, "redVipLevel") ?? FindInt(vipRoot, "vipLevel") ?? FindInt(vipRoot, "vipCode") ?? 0;
            return new(CredentialProbeStatus.Valid, true, vipLevel > 0 ? "黑胶 VIP" : "普通账号", null, "membership_valid");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(CredentialProbeStatus.NetworkError, false, null, null, "timeout");
        }
        catch (HttpRequestException)
        {
            return new(CredentialProbeStatus.NetworkError, false, null, null, "request_failed");
        }
        catch (JsonException)
        {
            return new(CredentialProbeStatus.NetworkError, false, null, null, "invalid_response");
        }
    }

    private async Task<CredentialProbeOutcome> ProbeQqAsync(IReadOnlyDictionary<string, string> cookies, CancellationToken ct)
    {
        if (!PlatformCredentialPolicy.LooksUsable("qq", cookies))
            return new(CredentialProbeStatus.NotAuthorized, false, null, false, "not_authorized");
        var uin = (cookies.GetValueOrDefault("uin") ?? cookies.GetValueOrDefault("wxuin") ?? "0").TrimStart('o');
        if (!long.TryParse(uin, out var numericUin) || numericUin <= 0)
            return new(CredentialProbeStatus.Invalid, false, null, false, "profile_missing_uin");
        var loginKey = FirstCookie(cookies, "qm_keyst", "qqmusic_key", "p_skey", "skey");
        try
        {
            var profileUrl = $"https://c.y.qq.com/rsc/fcgi-bin/fcg_get_profile_homepage.fcg?cid=205360838&userid={Uri.EscapeDataString(uin)}&reqfrom=1&g_tk={Gtk(FirstCookie(cookies, "p_skey", "skey"))}&loginUin={Uri.EscapeDataString(uin)}&format=json";
            using var profileRequest = Create(HttpMethod.Get, profileUrl, cookies, "https://y.qq.com/");
            using var profileResponse = await http.SendAsync(profileRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            if (profileResponse.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return new(CredentialProbeStatus.Invalid, false, null, false, "profile_unauthorized", HttpStatus: (int)profileResponse.StatusCode);
            if (!profileResponse.IsSuccessStatusCode)
                return new(CredentialProbeStatus.NetworkError, false, null, null, "profile_http_error", HttpStatus: (int)profileResponse.StatusCode);
            using var profileJson = JsonDocument.Parse(await profileResponse.Content.ReadAsStreamAsync(ct));
            if (!IsQqSuccess(profileJson.RootElement))
                return new(CredentialProbeStatus.Invalid, false, null, false, "profile_rejected");
            if (string.IsNullOrWhiteSpace(loginKey))
                return new(CredentialProbeStatus.PlaybackLimited, true, null, false, "missing_playback_credential");

            var sample = await FindQqProbeSongAsync(cookies, ct);
            if (sample is null)
                return new(CredentialProbeStatus.Valid, true, null, null, "sample_unavailable");

            CredentialProbeOutcome? bestFailure = null;
            foreach (var quality in new[] { "M800", "F000", "M500", "C400" })
            {
                var outcome = await ProbeQqVkeyAsync(sample.Value.SongMid, sample.Value.MediaMid, quality, uin, loginKey, cookies, ct);
                if (outcome.Status == CredentialProbeStatus.Valid)
                    return outcome with { Membership = "会员播放可用" };
                if (outcome.Status == CredentialProbeStatus.NetworkError) bestFailure ??= outcome;
                else bestFailure = outcome;
            }
            return bestFailure ?? new(CredentialProbeStatus.PlaybackLimited, true, null, false, "vkey_rejected");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(CredentialProbeStatus.NetworkError, false, null, null, "timeout");
        }
        catch (HttpRequestException)
        {
            return new(CredentialProbeStatus.NetworkError, false, null, null, "request_failed");
        }
        catch (JsonException)
        {
            return new(CredentialProbeStatus.NetworkError, false, null, null, "invalid_response");
        }
    }

    private async Task<(string SongMid, string MediaMid)?> FindQqProbeSongAsync(IReadOnlyDictionary<string, string> cookies, CancellationToken ct)
    {
        var url = "https://c.y.qq.com/soso/fcgi-bin/client_search_cp?format=json&p=1&n=8&w=" + Uri.EscapeDataString("周杰伦 晴天");
        using var request = Create(HttpMethod.Get, url, cookies, "https://y.qq.com/");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) return null;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        if (!json.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("song", out var song) ||
            !song.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in list.EnumerateArray())
        {
            if (!string.Equals(GetString(item, "songname"), "晴天", StringComparison.OrdinalIgnoreCase)) continue;
            var singerMatches = item.TryGetProperty("singer", out var singers) && singers.ValueKind == JsonValueKind.Array &&
                singers.EnumerateArray().Any(value => (GetString(value, "name") ?? string.Empty).Contains("周杰伦", StringComparison.OrdinalIgnoreCase));
            var songMid = GetString(item, "songmid");
            if (!singerMatches || string.IsNullOrWhiteSpace(songMid)) continue;
            var mediaMid = GetString(item, "strMediaMid") ?? songMid;
            return (songMid, mediaMid);
        }
        return null;
    }

    private async Task<CredentialProbeOutcome> ProbeQqVkeyAsync(
        string songMid,
        string mediaMid,
        string quality,
        string uin,
        string loginKey,
        IReadOnlyDictionary<string, string> cookies,
        CancellationToken ct)
    {
        var extension = quality.StartsWith('F') ? "flac" : quality.StartsWith('C') ? "m4a" : "mp3";
        var filename = $"{quality}{mediaMid}.{extension}";
        var guidBytes = SHA256.HashData(Encoding.UTF8.GetBytes("beans-qq-guid:" + uin));
        var guid = (BitConverter.ToUInt32(guidBytes, 0) % 900_000_000 + 100_000_000).ToString();
        var data = JsonSerializer.Serialize(new
        {
            comm = new { uin = long.Parse(uin), format = "json", ct = 19, cv = 0, g_tk = Gtk(FirstCookie(cookies, "p_skey", "skey")), authst = loginKey },
            req = new { module = "CDN.SrfCdnDispatchServer", method = "GetCdnDispatch", param = new { guid, calltype = 0, userip = "" } },
            req_0 = new { module = "vkey.GetVkeyServer", method = "CgiGetVkey", param = new { filename = new[] { filename }, guid, songmid = new[] { songMid }, songtype = new[] { 0 }, uin, loginflag = 1, platform = "20" } }
        });
        var url = "https://u.y.qq.com/cgi-bin/musicu.fcg?format=json&data=" + Uri.EscapeDataString(data);
        using var request = Create(HttpMethod.Get, url, cookies, "https://y.qq.com/");
        request.Headers.TryAddWithoutValidation("Origin", "https://y.qq.com");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            return new(CredentialProbeStatus.NetworkError, true, null, null, "vkey_http_error", quality, HttpStatus: (int)response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        if (!json.RootElement.TryGetProperty("req_0", out var req) || !req.TryGetProperty("data", out var responseData) ||
            !responseData.TryGetProperty("midurlinfo", out var infos) || infos.ValueKind != JsonValueKind.Array)
            return new(CredentialProbeStatus.NetworkError, true, null, null, "vkey_invalid_response", quality);
        var info = infos.EnumerateArray().FirstOrDefault(value => !string.IsNullOrWhiteSpace(GetString(value, "purl")));
        if (info.ValueKind == JsonValueKind.Undefined)
        {
            var first = infos.EnumerateArray().FirstOrDefault();
            var resultCode = first.ValueKind == JsonValueKind.Undefined ? null : FindInt(first, "result");
            return new(CredentialProbeStatus.PlaybackLimited, true, null, false, resultCode is null ? "vkey_rejected" : $"vkey_{resultCode}", quality, resultCode);
        }
        var purl = GetString(info, "purl")!;
        var bases = responseData.TryGetProperty("sip", out var sip) && sip.ValueKind == JsonValueKind.Array
            ? sip.EnumerateArray().Select(value => value.GetString()).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToList()
            : [];
        bases.AddRange(["https://isure.stream.qqmusic.qq.com/", "https://dl.stream.qqmusic.qq.com/", "https://ws.stream.qqmusic.qq.com/", "https://streamoc.music.tc.qq.com/"]);
        int? lastStatus = null;
        foreach (var item in bases.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = purl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? purl
                : item.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase).TrimEnd('/') + "/" + purl.TrimStart('/');
            using var probe = Create(HttpMethod.Get, candidate, cookies, "https://y.qq.com/");
            probe.Headers.Range = new RangeHeaderValue(0, 1);
            probe.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
            using var cdn = await http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct);
            lastStatus = (int)cdn.StatusCode;
            var contentType = cdn.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var accepted = cdn.StatusCode is System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.PartialContent;
            if (accepted && !contentType.Contains("json", StringComparison.OrdinalIgnoreCase) && !contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                await using var stream = await cdn.Content.ReadAsStreamAsync(ct);
                var twoBytes = new byte[2];
                _ = await stream.ReadAsync(twoBytes.AsMemory(0, 2), ct);
                return new(CredentialProbeStatus.Valid, true, null, true, "playable", quality, HttpStatus: lastStatus);
            }
            if (purl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) break;
        }
        return new(CredentialProbeStatus.PlaybackLimited, true, null, false, "cdn_rejected", quality, HttpStatus: lastStatus);
    }

    private static HttpRequestMessage Create(HttpMethod method, string url, IReadOnlyDictionary<string, string> cookies, string referer)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 BeansMusic/1.0");
        request.Headers.Referrer = new Uri(referer);
        request.Headers.Add("Cookie", string.Join("; ", cookies.Where(x => !string.IsNullOrWhiteSpace(x.Value)).Select(x => $"{x.Key}={x.Value}")));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static HttpRequestMessage CreatePlaybackRequest(
        Uri uri,
        string provider,
        IReadOnlyDictionary<string, string> cookies)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) BeansMusic/1.5.7");
        request.Headers.Referrer = new Uri(provider == "qq" ? "https://y.qq.com/" : "https://music.163.com/");
        if (provider == "qq") request.Headers.TryAddWithoutValidation("Origin", "https://y.qq.com");
        request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .Select(item => $"{item.Key}={item.Value}")));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));
        return request;
    }

    private static async Task EnsurePlatformSuccessAsync(HttpResponseMessage response, string platform)
    {
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException($"{platform}授权已失效");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{platform}请求失败", null, response.StatusCode);
        await Task.CompletedTask;
    }

    private static bool IsQqSuccess(JsonElement root) => root.TryGetProperty("code", out var code) && code.TryGetInt32(out var value) && (value is 0 or 1000);
    private static string FirstCookie(IReadOnlyDictionary<string, string> cookies, params string[] names) =>
        names.Select(name => cookies.GetValueOrDefault(name)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    public static bool IsQqFavoritePlaylistName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        (name.Contains("我喜欢", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("我的喜欢", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("喜欢的音乐", StringComparison.OrdinalIgnoreCase));
    private static int Gtk(string value)
    {
        long hash = 5381;
        foreach (var character in value) hash += (hash << 5) + character;
        return (int)(hash & 0x7fffffff);
    }
    private static string? GetString(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property.ToString() : null;
    private static string JoinArtists(JsonElement value, string propertyName)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(propertyName, out var artists) || artists.ValueKind != JsonValueKind.Array)
            return "未知歌手";
        var names = artists.EnumerateArray()
            .Select(artist => GetString(artist, "name"))
            .Where(name => !string.IsNullOrWhiteSpace(name));
        var joined = string.Join(" / ", names!);
        return string.IsNullOrWhiteSpace(joined) ? "未知歌手" : joined;
    }
    private static int GetInt32Any(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            var result = GetInt32(value, name);
            if (result != 0) return result;
        }
        return 0;
    }
    private static JsonElement? FindArray(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.NameEquals(name) && property.Value.ValueKind == JsonValueKind.Array) return property.Value;
                if (FindArray(property.Value, name) is { } nested) return nested;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                if (FindArray(item, name) is { } nested) return nested;
        }
        return null;
    }
    private static JsonElement? FindObject(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.NameEquals(name) && property.Value.ValueKind == JsonValueKind.Object) return property.Value;
                if (FindObject(property.Value, name) is { } nested) return nested;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                if (FindObject(item, name) is { } nested) return nested;
        }
        return null;
    }
    private static int GetInt32(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property)) return 0;
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number)
            ? number
            : int.TryParse(property.ToString(), out var parsed) ? parsed : 0;
    }

    private static long GetInt64(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property)) return 0;
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number)
            ? number
            : long.TryParse(property.ToString(), out var parsed) ? parsed : 0;
    }
    private static int? FindInt(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                if (property.NameEquals(name))
                {
                    if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var result)) return result;
                    if (property.Value.ValueKind == JsonValueKind.String && int.TryParse(property.Value.GetString(), out result)) return result;
                }
                var nested = FindInt(property.Value, name);
                if (nested is not null) return nested;
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) { var nested = FindInt(item, name); if (nested is not null) return nested; }
        return null;
    }

    private static long GetPositiveInt64Scalar(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number > 0) return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number) && number > 0) return number;
        return 0;
    }
    private static long FindPositiveInt64(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.NameEquals(name) && GetPositiveInt64Scalar(property.Value) is var result && result > 0) return result;
                var nested = FindPositiveInt64(property.Value, name);
                if (nested > 0) return nested;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var nested = FindPositiveInt64(item, name);
                if (nested > 0) return nested;
            }
        }
        return 0;
    }
    private static Uri? UriOrNull(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    private static string? FindString(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                if (property.NameEquals(name) && property.Value.ValueKind == JsonValueKind.String) return property.Value.GetString();
                var nested = FindString(property.Value, name);
                if (nested is not null) return nested;
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) { var nested = FindString(item, name); if (nested is not null) return nested; }
        return null;
    }

}
