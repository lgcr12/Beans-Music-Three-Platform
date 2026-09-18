using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Beans.Core;

public sealed record ReleaseAsset(string Name, Uri DownloadUrl);
public sealed record ApplicationRelease(string Version, string Name, string Notes, DateTimeOffset PublishedAt, Uri PageUrl, IReadOnlyList<ReleaseAsset> Assets);
public sealed record UpdateCheckResult(ApplicationRelease? Release, string? ETag, bool NotModified);

public sealed class ApplicationUpdateService(HttpClient http, Architecture? architecture = null)
{
    private const string ReleasesUrl = "https://api.github.com/repos/lgcr12/Beans-Music-Three-Platform/releases?per_page=30";
    private readonly Architecture _architecture = architecture ?? RuntimeInformation.ProcessArchitecture;

    public async Task<UpdateCheckResult> CheckAsync(string? etag = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesUrl);
        request.Headers.UserAgent.ParseAdd("BeansMusic-Windows/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!string.IsNullOrWhiteSpace(etag)) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var responseEtag = response.Headers.ETag?.ToString() ?? etag;
        if (response.StatusCode == HttpStatusCode.NotModified) return new(null, responseEtag, true);
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var releases = json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement.EnumerateArray().ToArray() : [];
        var selected = releases.FirstOrDefault(item => IsChannelRelease(item, legacy: false));
        if (selected.ValueKind == JsonValueKind.Undefined)
            selected = releases.FirstOrDefault(item => IsChannelRelease(item, legacy: true));
        var release = selected.ValueKind == JsonValueKind.Undefined ? null : ParseRelease(selected);
        return new(release, responseEtag, false);
    }

    private bool IsChannelRelease(JsonElement value, bool legacy)
    {
        if (value.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return false;
        if (value.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()) return false;
        var tag = value.TryGetProperty("tag_name", out var tagValue) ? tagValue.GetString() ?? string.Empty : string.Empty;
        var matchesTag = legacy
            ? tag.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            : tag.StartsWith("windows-v", StringComparison.OrdinalIgnoreCase);
        if (!matchesTag || !value.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return false;
        return assets.EnumerateArray().Any(asset => IsArchitectureAsset(asset, _architecture));
    }

    private static ApplicationRelease ParseRelease(JsonElement root)
    {
        var tag = root.GetProperty("tag_name").GetString() ?? "0";
        var version = tag.StartsWith("windows-v", StringComparison.OrdinalIgnoreCase)
            ? tag[9..]
            : tag.Trim().TrimStart('v', 'V');
        var page = new Uri(root.GetProperty("html_url").GetString()!);
        var assets = root.GetProperty("assets").EnumerateArray().Select(asset => new ReleaseAsset(
            asset.GetProperty("name").GetString() ?? "download",
            new Uri(asset.GetProperty("browser_download_url").GetString()!))).ToArray();
        return new ApplicationRelease(
            version.Split('-', 2)[0],
            root.TryGetProperty("name", out var name) ? name.GetString() ?? tag : tag,
            root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("published_at", out var published) && published.TryGetDateTimeOffset(out var date) ? date : DateTimeOffset.MinValue,
            page,
            assets);
    }

    private static bool IsArchitectureAsset(JsonElement asset, Architecture architecture)
    {
        var name = asset.TryGetProperty("name", out var value) ? value.GetString() ?? string.Empty : string.Empty;
        var architectureName = architecture == Architecture.Arm64 ? "arm64" : "x64";
        return name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase) &&
               name.Contains("windows", StringComparison.OrdinalIgnoreCase) &&
               name.Contains(architectureName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNewer(string candidate, string current)
    {
        static int[] Parts(string value) => value.Trim().TrimStart('v', 'V').Split('.', '-', '+')
            .Take(4).Select(part => int.TryParse(part, out var number) ? number : 0).ToArray();
        var left = Parts(candidate);
        var right = Parts(current);
        for (var index = 0; index < Math.Max(left.Length, right.Length); index++)
        {
            var a = index < left.Length ? left[index] : 0;
            var b = index < right.Length ? right[index] : 0;
            if (a != b) return a > b;
        }
        return false;
    }

    public static ReleaseAsset? SelectWindowsAsset(ApplicationRelease release, Architecture architecture)
    {
        var architectureName = architecture == Architecture.Arm64 ? "arm64" : "x64";
        return release.Assets.FirstOrDefault(asset =>
            asset.Name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase) &&
            asset.Name.Contains(architectureName, StringComparison.OrdinalIgnoreCase));
    }
}
