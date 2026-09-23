using System.Globalization;
using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Pages.Shared;

public static class OnlinePageTrackMapper
{
    public const string PreviewUnavailableMessage = "预览条目不包含真实平台歌曲标识，无法播放";

    public static bool TryCreateSearchResult(
        PlatformId platform,
        string? nativeId,
        string? title,
        string? artist,
        string? album,
        TimeSpan? duration,
        string? coverUri,
        string? quality,
        SearchDataOrigin dataOrigin,
        out SearchResultItem? item,
        string? providerMediaId = null)
    {
        item = null;
        var id = nativeId?.Trim() ?? string.Empty;
        var normalizedTitle = title?.Trim() ?? string.Empty;
        if (dataOrigin == SearchDataOrigin.Preview || normalizedTitle.Length == 0 || !IsValidNativeTrackId(platform, id))
            return false;

        var source = platform.ToDisplayName();
        item = new SearchResultItem(
            SearchResultType.Track,
            platform,
            id,
            $"{platform.ToStableId()}:track:{id}",
            normalizedTitle,
            Subtitle: JoinSubtitle(artist, album),
            Artist: artist?.Trim() ?? string.Empty,
            Album: album?.Trim() ?? string.Empty,
            CoverUri: coverUri?.Trim() ?? string.Empty,
            Duration: duration,
            Quality: string.IsNullOrWhiteSpace(quality) ? "未知" : quality.Trim(),
            IsPlayable: false,
            RestrictionState: "需要平台授权并解析播放地址",
            SourceDisplayName: source,
            SourceBadgeText: source,
            DataOrigin: dataOrigin,
            PayloadReference: $"{platform.ToStableId()}:track:{id}",
            ProviderMediaId: providerMediaId);
        return true;
    }

    public static bool IsValidNativeTrackId(PlatformId platform, string? nativeId)
    {
        var id = nativeId?.Trim() ?? string.Empty;
        if (id.Length is 0 or > 128 || id.Any(char.IsWhiteSpace) || id.Contains('/') || id.Contains('\\') || id.Contains(':'))
            return false;

        return platform switch
        {
            PlatformId.QqMusic => id.All(char.IsLetterOrDigit),
            PlatformId.NetEaseMusic => long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0,
            _ => false
        };
    }

    public static TimeSpan? ParseDuration(string? value) =>
        TimeSpan.TryParseExact(value?.Trim(), [@"m\:ss", @"mm\:ss", @"h\:mm\:ss"], CultureInfo.InvariantCulture, out var duration)
            ? duration
            : null;

    private static string JoinSubtitle(string? artist, string? album) =>
        string.Join(" · ", new[] { artist?.Trim(), album?.Trim() }.Where(value => !string.IsNullOrWhiteSpace(value)));
}
