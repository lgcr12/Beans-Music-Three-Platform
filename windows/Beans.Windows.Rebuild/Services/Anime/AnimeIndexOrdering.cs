using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Anime;

public static class AnimeIndexOrdering
{
    // Calendar entries have no country field. Prefer explicit origin tags when
    // present; otherwise kana in the original title is a conservative signal.
    // Keep unclassified entries and preserve the user's sort within each group.
    public static bool IsJapanese(AnimeSubject subject)
    {
        var tags = subject.Genres.Select(AnimeCatalogService.Normalize).ToHashSet(StringComparer.Ordinal);
        if (tags.Overlaps(["国产", "中国", "中国大陆", "国漫", "欧美", "美国", "英国", "韩国", "韩漫"])) return false;
        if (tags.Overlaps(["日本", "日本动画", "日漫", "日本アニメ"])) return true;
        var title = subject.JapaneseTitle.Normalize(System.Text.NormalizationForm.FormKC);
        return title.Any(c => c is >= '\u3041' and <= '\u3096' or >= '\u30A1' and <= '\u30FA');
    }

    public static IReadOnlyList<AnimeSeries> JapaneseFirst(IEnumerable<AnimeSeries> series) =>
        series.OrderByDescending(group => group.Subjects.Any(IsJapanese)).ToArray();
}
