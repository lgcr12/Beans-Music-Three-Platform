using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Beans.Core;

public sealed record PlatformProfile(string Id, string Nickname);

/// <summary>Runs third-party requests on the client. Cookies never pass through the Beans server.</summary>
public sealed class PlatformMusicClient(HttpClient http)
{
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
                playlists.Add(new(GetInt64(item, "id"), GetString(item, "name") ?? "未命名歌单", UriOrNull(GetString(item, "coverImgUrl")), GetInt32(item, "trackCount"), creator ?? nickname, "netease"));
            }
        }
        return (new PlatformProfile(userId.ToString(), nickname), playlists);
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

        var listUrl = $"https://c.y.qq.com/fav/fcgi-bin/fcg_get_profile_order_asset.fcg?ct=20&cid=205360956&userid={Uri.EscapeDataString(uin)}&reqtype=3&sin=0&ein=80&format=json";
        using var listRequest = Create(HttpMethod.Get, listUrl, cookies, "https://y.qq.com/portal/profile.html");
        using var listResponse = await http.SendAsync(listRequest, ct);
        await EnsurePlatformSuccessAsync(listResponse, "QQ 音乐");
        using var listJson = JsonDocument.Parse(await listResponse.Content.ReadAsStreamAsync(ct));
        var playlists = FindArray(listJson.RootElement, "cdlist") ?? FindArray(listJson.RootElement, "disslist");
        var result = new List<MirrorPlaylist>();
        if (playlists is { } array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var id = GetInt64(item, "dissid");
                if (id == 0) id = GetInt64(item, "tid");
                result.Add(new(id, GetString(item, "dissname") ?? GetString(item, "dirname") ?? "未命名歌单", UriOrNull(GetString(item, "logo") ?? GetString(item, "picurl")), GetInt32(item, "songnum"), nickname, "qq"));
            }
        }
        return (new PlatformProfile(uin, nickname), result);
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
    private static int Gtk(string value)
    {
        long hash = 5381;
        foreach (var character in value) hash += (hash << 5) + character;
        return (int)(hash & 0x7fffffff);
    }
    private static string? GetString(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property.ToString() : null;
    private static int GetInt32(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.TryGetInt32(out var result) ? result : 0;
    private static long GetInt64(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && (property.TryGetInt64(out var result) || long.TryParse(property.ToString(), out result)) ? result : 0;
    private static int? FindInt(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                if (property.NameEquals(name) && (property.Value.TryGetInt32(out var result) || int.TryParse(property.Value.ToString(), out result))) return result;
                var nested = FindInt(property.Value, name);
                if (nested is not null) return nested;
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) { var nested = FindInt(item, name); if (nested is not null) return nested; }
        return null;
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

    private static JsonElement? FindArray(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                if (property.NameEquals(name) && property.Value.ValueKind == JsonValueKind.Array) return property.Value;
                var nested = FindArray(property.Value, name);
                if (nested is not null) return nested;
            }
        return null;
    }
}
