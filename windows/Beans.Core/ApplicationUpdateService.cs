using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Beans.Core;

public sealed record ReleaseAsset(string Name, Uri DownloadUrl);
public sealed record ApplicationRelease(string Version, string Name, string Notes, DateTimeOffset PublishedAt, Uri PageUrl, IReadOnlyList<ReleaseAsset> Assets);
public sealed record UpdateCheckResult(ApplicationRelease? Release, string? ETag, bool NotModified);

public sealed class ApplicationUpdateService(HttpClient http)
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/lgcr12/Beans-Music-Three-Platform/releases/latest";

    public async Task<UpdateCheckResult> CheckAsync(string? etag = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        request.Headers.UserAgent.ParseAdd("BeansMusic-Windows/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!string.IsNullOrWhiteSpace(etag)) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var responseEtag = response.Headers.ETag?.ToString() ?? etag;
        if (response.StatusCode == HttpStatusCode.NotModified) return new(null, responseEtag, true);
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var root = json.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "0";
        var version = tag.Trim().TrimStart('v', 'V');
        var page = new Uri(root.GetProperty("html_url").GetString()!);
        var assets = root.TryGetProperty("assets", out var assetArray) && assetArray.ValueKind == JsonValueKind.Array
            ? assetArray.EnumerateArray().Select(asset => new ReleaseAsset(
                asset.GetProperty("name").GetString() ?? "download",
                new Uri(asset.GetProperty("browser_download_url").GetString()!))).ToArray()
            : [];
        var release = new ApplicationRelease(
            version,
            root.TryGetProperty("name", out var name) ? name.GetString() ?? tag : tag,
            root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("published_at", out var published) && published.TryGetDateTimeOffset(out var date) ? date : DateTimeOffset.MinValue,
            page,
            assets);
        return new(release, responseEtag, false);
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
