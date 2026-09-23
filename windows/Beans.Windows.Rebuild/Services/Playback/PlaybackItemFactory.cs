using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Playback;

public sealed record PlaybackOperationResult(
    bool IsSuccess,
    PlaybackItem? Item,
    PlaybackRestriction Restriction,
    string SafeMessage)
{
    public static PlaybackOperationResult Failure(PlaybackRestriction restriction, string message) =>
        new(false, null, restriction, message);
}

/// <summary>
/// Converts a search track and an optional resolved source into the existing
/// player model. This pure boundary keeps source resolution out of MediaPlayer.
/// </summary>
public static class PlaybackItemFactory
{
    private const string DefaultArtwork = "ms-appx:///Assets/Home/hero-mountain-lake.jpg";

    public static PlaybackOperationResult Create(SearchResultItem item, PlaybackSourceResult? sourceResult = null)
    {
        if (item.ResultType != SearchResultType.Track)
            return PlaybackOperationResult.Failure(PlaybackRestriction.NotImplemented, "当前结果不是可播放歌曲");
        if (item.DataOrigin == SearchDataOrigin.Preview)
            return PlaybackOperationResult.Failure(PlaybackRestriction.NotImplemented, "预览内容不能进入真实播放链路");

        PlaybackSource source;
        if (item.IsPlayable && !string.IsNullOrWhiteSpace(item.PlaybackUri))
        {
            if (!TryCreateUri(item.PlaybackUri, out var localUri))
                return PlaybackOperationResult.Failure(PlaybackRestriction.FileUnavailable, "歌曲文件不可用");
            source = new PlaybackSource(localUri, ParseQuality(item.Quality), null);
        }
        else
        {
            if (sourceResult is null)
                return PlaybackOperationResult.Failure(PlaybackRestriction.ProviderUnavailable, "播放源尚未准备");
            if (!sourceResult.IsSuccess || sourceResult.Source is null)
                return PlaybackOperationResult.Failure(sourceResult.Restriction, sourceResult.SafeMessage);
            source = sourceResult.Source;
        }

        var id = string.IsNullOrWhiteSpace(item.StableId)
            ? $"{item.Platform.ToStableId()}:track:{item.NativeId}"
            : item.StableId;
        var artwork = string.IsNullOrWhiteSpace(item.CoverUri) ? DefaultArtwork : item.CoverUri;
        var label = string.IsNullOrWhiteSpace(item.SourceDisplayName) ? item.Platform.ToDisplayName() : item.SourceDisplayName;
        var qualityLabel = sourceResult is null && !string.IsNullOrWhiteSpace(item.Quality)
            ? item.Quality
            : QualityLabel(source.Quality);
        return new PlaybackOperationResult(
            true,
            new PlaybackItem(
                id,
                item.Title,
                item.Artist,
                item.Album,
                artwork,
                source.Uri.AbsoluteUri,
                item.Duration ?? TimeSpan.Zero,
                label,
                qualityLabel,
                item.Platform,
                item.NativeId,
                item.DataOrigin,
                item.ProviderMediaId),
            PlaybackRestriction.None,
            "播放源已准备");
    }

    private static bool TryCreateUri(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out uri!)) return true;
        try
        {
            uri = new Uri(Path.GetFullPath(value));
            return true;
        }
        catch (Exception) when (value.Length > 0)
        {
            uri = null!;
            return false;
        }
    }

    private static AudioQuality ParseQuality(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "HI-RES" or "HIRES" => AudioQuality.HiRes,
        "无损" or "LOSSLESS" => AudioQuality.Lossless,
        "SQ" or "超高" or "VERYHIGH" => AudioQuality.VeryHigh,
        "HQ" or "高" or "HIGH" => AudioQuality.High,
        _ => AudioQuality.Standard
    };

    private static string QualityLabel(AudioQuality quality) => quality switch
    {
        AudioQuality.HiRes => "Hi-Res",
        AudioQuality.Lossless => "无损",
        AudioQuality.VeryHigh => "SQ",
        AudioQuality.High => "HQ",
        _ => "标准"
    };
}
