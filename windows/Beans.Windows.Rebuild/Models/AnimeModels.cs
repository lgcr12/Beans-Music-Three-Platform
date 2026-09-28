namespace Beans.Windows.Rebuild.Models;

public enum AnimeSongType { Opening, Ending, Insert, Character, Related }

public sealed class AnimeSubject
{
    public AnimeSubject(string id, string title, string japaneseTitle, string synopsis, string category, string imageUri, int year, int season = 1,
        string? romajiTitles = null, IReadOnlyList<string>? aliases = null, string? posterUri = null, string? backgroundUri = null,
        AnimeExternalLinks? externalLinks = null, IReadOnlyList<string>? genres = null)
    {
        Id = id;
        Title = title;
        JapaneseTitle = japaneseTitle;
        Synopsis = synopsis;
        Category = category;
        ImageUri = imageUri;
        Year = year;
        Season = season;
        RomajiTitles = romajiTitles ?? string.Empty;
        Aliases = aliases ?? [];
        PosterUri = posterUri ?? imageUri;
        BackgroundUri = backgroundUri ?? imageUri;
        ExternalLinks = externalLinks ?? new AnimeExternalLinks();
        Genres = genres ?? [category];
    }

    public string Id { get; set; }
    public int BangumiId { get; set; }
    public string ChineseTitle => Title;
    public string MetadataStatus { get; set; } = "精选资料";
    public string Title { get; set; }
    public string JapaneseTitle { get; set; }
    public string Synopsis { get; set; }
    public string Category { get; set; }
    public string ImageUri { get; set; }
    public int Year { get; set; }
    public int Season { get; set; }
    public string RomajiTitles { get; set; }
    public IReadOnlyList<string> Aliases { get; set; }
    public IReadOnlyList<string> Genres { get; set; }
    public string PosterUri { get; set; }
    public string BackgroundUri { get; set; }
    public AnimeExternalLinks ExternalLinks { get; set; }
    public string AirDate { get; set; } = "";
    public string Format { get; set; } = "";
    public IReadOnlyList<int> RelatedAnimeIds { get; set; } = [];
    public IReadOnlyList<AnimeThemeSong> ThemeSongs { get; set; } = [];
    public string YearSeasonText => string.Join(" · ", new[] { Year > 0 ? Year.ToString() : "年份待定", Format, string.Join(" / ", Genres) }.Where(v => !string.IsNullOrWhiteSpace(v)));
}

public sealed record AnimeBrowseQuery(string Keyword = "", int? Year = null, string? Genre = null, int Offset = 0);
public sealed record AnimeBrowseResult(IReadOnlyList<AnimeSubject> Subjects, bool HasMore, int NextOffset, string Status);
public sealed record AnimeSeries(string Title, IReadOnlyList<AnimeSubject> Subjects)
{
    public AnimeSubject Cover => Subjects[0];
}
public sealed record AnimeSeriesResult(IReadOnlyList<AnimeSubject> Subjects, string Status);

public sealed class AnimeExternalLinks
{
    public string? WatchUrl { get; set; }
    public string? BangumiUrl { get; set; }
    public string? OfficialUrl { get; set; }
}

public sealed class AnimePlatformMatch
{
    public PlatformId Platform { get; set; }
    public string NativeId { get; set; } = string.Empty;
    public string MatchState { get; set; } = "待匹配";
    public string Availability { get; set; } = "未知";
    public string SafeMessage { get; set; } = string.Empty;
    public SearchResultItem? Result { get; set; }
}

public sealed class AnimeThemeSong
{
    public AnimeThemeSong(string id, string animeId, string title, string artist, AnimeSongType type, int season, IReadOnlyList<SearchResultItem> matches,
        string version = "完整版", double relationConfidence = 1d, IReadOnlyList<AnimePlatformMatch>? platformMatches = null)
    {
        Id = id;
        AnimeId = animeId;
        Title = title;
        Artist = artist;
        Type = type;
        Season = season;
        Matches = matches;
        Version = version;
        RelationConfidence = relationConfidence;
        PlatformMatches = platformMatches ?? [];
    }

    public string Id { get; set; }
    public string AnimeId { get; set; }
    public string Title { get; set; }
    public string Artist { get; set; }
    public AnimeSongType Type { get; set; }
    public int Season { get; set; }
    public IReadOnlyList<SearchResultItem> Matches { get; set; }
    public string Version { get; set; }
    public double RelationConfidence { get; set; }
    public string RelationSource { get; set; } = string.Empty;
    public IReadOnlyList<string> ArtistAliases { get; set; } = [];
    public IReadOnlyList<AnimePlatformMatch> PlatformMatches { get; set; }
}
