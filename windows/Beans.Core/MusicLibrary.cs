using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace Beans.Core;

public sealed record LocalMusicTrack(string Id, string Path, string Title, string Extension, long Size, DateTimeOffset ModifiedAt);
public sealed record LyricLine(TimeSpan Time, string Text, string? Translation = null);
public sealed record DownloadRequest(Uri Source, string DestinationPath);

public static class LocalMusicScanner
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".aac", ".flac", ".wav", ".ogg", ".wma" };

    public static async Task<IReadOnlyList<LocalMusicTrack>> ScanAsync(IEnumerable<string> roots, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var result = new List<LocalMusicTrack>();
            foreach (var root in roots.Where(Directory.Exists))
            {
                foreach (var path in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!Extensions.Contains(Path.GetExtension(path))) continue;
                    var file = new FileInfo(path);
                    result.Add(new(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path))).ToLowerInvariant(), path, Path.GetFileNameWithoutExtension(path), file.Extension, file.Length, file.LastWriteTimeUtc));
                }
            }
            return (IReadOnlyList<LocalMusicTrack>)result.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        }, ct);
    }
}

public static class LrcParser
{
    private static readonly Regex Timestamp = new(@"\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);

    public static IReadOnlyList<LyricLine> Parse(string text)
    {
        var result = new List<LyricLine>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var matches = Timestamp.Matches(raw);
            if (matches.Count == 0) continue;
            var lyric = Timestamp.Replace(raw, "").Trim();
            foreach (Match match in matches)
            {
                var fraction = match.Groups[3].Value.PadRight(3, '0');
                if (fraction.Length > 3) fraction = fraction[..3];
                var time = TimeSpan.FromMinutes(int.Parse(match.Groups[1].Value))
                    + TimeSpan.FromSeconds(int.Parse(match.Groups[2].Value))
                    + TimeSpan.FromMilliseconds(fraction.Length == 0 ? 0 : int.Parse(fraction));
                result.Add(new(time, lyric));
            }
        }
        return result
            .GroupBy(line => line.Time)
            .Select(group =>
            {
                var values = group.Select(line => line.Text).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
                return new LyricLine(group.Key, values.FirstOrDefault() ?? string.Empty, values.Skip(1).FirstOrDefault());
            })
            .OrderBy(line => line.Time)
            .ToList();
    }
}

public sealed class ResumableDownloader(HttpClient http)
{
    public async Task DownloadAsync(Uri source, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var partial = destinationPath + ".part";
        var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append) existing = 0;
        var total = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        long written = existing;
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
            written += count;
            if (total is > 0) progress?.Report(Math.Clamp((double)written / total.Value, 0, 1));
        }
        await output.FlushAsync(ct);
        File.Move(partial, destinationPath, true);
        progress?.Report(1);
    }
}
