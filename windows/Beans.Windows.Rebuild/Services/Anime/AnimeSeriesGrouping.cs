using System.Text.RegularExpressions;
using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Anime;

// Only explicit edition markers or an entire known title may identify a series.
// A shared keyword or the first few characters is not a series identity.
public static class AnimeSeriesGrouping
{
    private static readonly Regex Edition = new(
        @"\s*(?:第[一二三四五六七八九十百\d]+[季期部]|[2-9]\d*(?:st|nd|rd|th)?\s*(?:Season|シーズン)?|[ⅡⅢⅣⅤⅥ]+|II+|IV|VI*|Season\s*\d+|Final\s*Season|剧场版|劇場版|外传|外傳|外伝|特别篇|特別篇|特別編|OVA|OAD|总集篇|総集編)(?:\b|(?=[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}])|$).*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Prefix = new(@"^(?:剧场版|劇場版|映画)\s*", RegexOptions.CultureInvariant);

    public static IReadOnlyList<AnimeSeries> Group(IEnumerable<AnimeSubject> subjects)
    {
        var values = subjects.DistinctBy(s => s.BangumiId > 0 ? s.BangumiId.ToString() : s.Id).ToArray();
        var stems = values.SelectMany(s => new[] { Stem(s.Title), Stem(s.JapaneseTitle) }).Where(s => s.Length > 0).Distinct().OrderBy(s => s.Length).ToArray();
        string Key(string name)
        {
            var title = Stem(name);
            return stems.FirstOrDefault(root => root.Length >= 3 && title.Length > root.Length &&
                title.StartsWith(root, StringComparison.OrdinalIgnoreCase) && IsBoundary(title[root.Length])) ?? title;
        }
        var parents = Enumerable.Range(0, values.Length).ToArray();
        int Root(int index) { while (parents[index] != index) index = parents[index]; return index; }
        var owners = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < values.Length; i++)
            foreach (var name in new[] { values[i].Title, values[i].JapaneseTitle }.Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                var key = Key(name);
                if (owners.TryGetValue(key, out var owner)) parents[Root(i)] = Root(owner);
                else owners[key] = i;
            }
        var byId = values.Select((s, i) => (s.BangumiId, Index: i)).Where(pair => pair.BangumiId > 0).ToDictionary(pair => pair.BangumiId, pair => pair.Index);
        for (var i = 0; i < values.Length; i++)
            foreach (var related in values[i].RelatedAnimeIds)
                if (byId.TryGetValue(related, out var target)) parents[Root(i)] = Root(target);
        return values.Select((value, index) => (value, index)).GroupBy(item => Root(item.index))
            .Select(g => new AnimeSeries(g.Select(item => Key(item.value.Title)).OrderBy(t => t.Length).First(), g.Select(item => item.value).ToArray())).ToArray();
    }

    public static string Stem(string title)
    {
        var value = Prefix.Replace(title.Normalize(System.Text.NormalizationForm.FormKC).Trim(), "");
        var stripped = Edition.Replace(value, "").Trim(' ', '：', ':', '-', '－', '—');
        return stripped.Length >= 2 ? stripped : value;
    }

    private static bool IsBoundary(char c) => char.IsWhiteSpace(c) || c is ':' or '：' or '-' or '－' or '—' or '(' or '（';
}
