using Beans.Windows.Rebuild.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Beans.Windows.Rebuild.Pages.Anime;

public sealed class AnimeSeriesView : AnimeView
{
    private readonly AnimeSeries _series;
    private readonly ContentControl _editions = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status = AnimeUi.Muted("正在补充季度与剧场版…");

    public AnimeSeriesView(AnimeShell shell, AnimeSeries series) : base(shell)
    {
        _series = series;
        var body = AnimeUi.Stack(AnimeUi.Button("返回番剧索引", () => Shell.BrowseIndex()),
            AnimeUi.Text(series.Title, 26), AnimeUi.Muted("季度 / 剧场版 · 选择一个版本查看作品与主题曲"), _status, _editions);
        body.Margin = new Thickness(32, 12, 32, 24);
        Content = AnimeUi.Scroll(body);
        Render(series.Subjects);
        Loaded += (_, _) => _ = Run(Load);
    }

    private async Task Load()
    {
        var result = await Shell.SearchService.LoadSeriesAsync(_series, Lifetime.Token);
        Lifetime.Token.ThrowIfCancellationRequested();
        _status.Text = $"{result.Subjects.Count} 个版本 · {result.Status}";
        Render(result.Subjects);
    }

    private void Render(IReadOnlyList<AnimeSubject> subjects)
    {
        Shell.Prefetch.Prefetch(subjects);
        _editions.Content = AnimeUi.SubjectCards(
        subjects.OrderBy(s => s.Year == 0 ? int.MaxValue : s.Year).ThenBy(s => s.AirDate), subject => Shell.OpenEdition(subject, _series),
        caption: s => s.YearSeasonText, actionLabel: _ => "进入此版本");
    }
}
