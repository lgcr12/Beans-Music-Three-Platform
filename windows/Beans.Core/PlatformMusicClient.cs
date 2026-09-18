using System.Net.Http.Headers;
using System.Text.Json;

namespace Beans.Core;

public sealed record PlatformProfile(string Id, string Nickname);

/// <summary>Runs third-party requests on the client. Cookies never pass through the Beans server.</summary>
public sealed class PlatformMusicClient(HttpClient http)
{
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
    private static string? GetString(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property.ToString() : null;
    private static int GetInt32(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.TryGetInt32(out var result) ? result : 0;
    private static long GetInt64(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && (property.TryGetInt64(out var result) || long.TryParse(property.ToString(), out result)) ? result : 0;
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
