using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Anime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Beans.Windows.Rebuild.Pages.Anime;

public sealed class AnimeIndexView : AnimeView
{
    private readonly ContentControl _cards = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ContentControl _genres = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _query = new() { PlaceholderText = "搜索中文名、日文名、罗马音" };
    private readonly ComboBox _years = new() { MinWidth = 126, PlaceholderText = "全部年份" };
    private readonly TextBlock _status = AnimeUi.Muted("");
    private readonly Button _sort, _more;
    private readonly List<AnimeSubject> _subjects = [];
    private string _genre = "全部";
    private int _sortIndex, _generation, _nextOffset, _visibleCount = 24;
    private bool _hasMore;
    private CancellationTokenSource? _request;

    public AnimeIndexView(AnimeShell shell, string? genre = null, string? query = null) : base(shell)
    {
        _genre = genre ?? "全部";
        _query.Text = query ?? "";
        _sort = AnimeUi.Button("最新放送  ⌄", () => { });
        var menu = new MenuFlyout();
        foreach (var (label, index) in new[] { ("最新放送", 0), ("最早放送", 1), ("作品名称", 2) })
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => { _sortIndex = index; _sort.Content = label + "  ⌄"; RenderCards(); };
            menu.Items.Add(item);
        }
        _sort.Flyout = menu;
        _years.Items.Add("全部年份");
        foreach (var year in Enumerable.Range(1960, DateTime.Now.Year - 1960 + 2).Reverse()) _years.Items.Add(year.ToString());
        _years.SelectedIndex = 0;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_years, "首播年份");
        _years.SelectionChanged += (_, _) => _ = Run(() => Refresh());
        _query.TextChanged += (_, _) => _ = Run(() => Refresh(debounce: true));
        _more = AnimeUi.Button("加载更多番剧", () =>
        {
            if (AnimeSeriesGrouping.Group(_subjects).Count > _visibleCount) { _visibleCount += 24; RenderCards(); }
            else _ = Run(() => Refresh(append: true));
        }, "primary");
        _more.HorizontalAlignment = HorizontalAlignment.Center;
        _more.Visibility = Visibility.Collapsed;
        var retry = AnimeUi.Button("重新加载", () => _ = Run(() => Refresh()));
        var filters = new Grid { ColumnSpacing = 12, ColumnDefinitions = { new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto } } };
        filters.Children.Add(AnimeUi.Inline(AnimeUi.Text("年份：", 12), _years));
        Grid.SetColumn(_sort, 1); filters.Children.Add(_sort);
        var statusRow = new Grid { ColumnSpacing = 12, ColumnDefinitions = { new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto } } };
        statusRow.Children.Add(_status); Grid.SetColumn(retry, 1); statusRow.Children.Add(retry);
        var content = AnimeUi.Stack(AnimeUi.Text("找到那部，让你记住旋律的动画", 22), AnimeUi.SearchBox(_query), _genres, filters, statusRow, _cards, _more);
        var card = AnimeUi.Card(content);
        card.Padding = new Thickness(28, 22, 28, 28);
        card.Margin = new Thickness(48, 6, 48, 24);
        Content = AnimeUi.Scroll(card);
        RenderFilters();
        SizeChanged += (_, e) => card.Margin = new Thickness(e.NewSize.Width < 1100 ? 24 : 48, 6, e.NewSize.Width < 1100 ? 24 : 48, 24);
        Loaded += (_, _) => _ = Run(() => Refresh());
        Unloaded += (_, _) => _request?.Cancel();
    }

    private void RenderFilters() => _genres.Content = AnimeUi.Chips(new[] { "全部", "热血", "青春", "治愈", "奇幻", "科幻", "音乐", "悬疑" }, _genre,
        value => { _genre = value; RenderFilters(); _ = Run(() => Refresh()); });

    private async Task Refresh(bool append = false, bool debounce = false)
    {
        var generation = ++_generation;
        _request?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
        _request = request;
        var keyword = _query.Text;
        int? year = int.TryParse(_years.SelectedItem?.ToString(), out var selectedYear) ? selectedYear : null;
        _more.IsEnabled = false;
        _status.Text = string.IsNullOrWhiteSpace(keyword) ? year is null ? "正在加载近期番剧…" : $"正在加载 {year} 年番剧…" : "正在搜索 Bangumi 公开资料…";
        if (!append)
        {
            _subjects.Clear(); _cards.Content = null; _more.Visibility = Visibility.Collapsed; _visibleCount = 24; _hasMore = false;
            if (string.IsNullOrWhiteSpace(keyword) && year is null && _genre == "全部")
            {
                _subjects.AddRange(Shell.SearchService.GetRecentPreview());
                RenderCards();
                _status.Text = "最近放送快照 · 正在更新 Bangumi…";
            }
        }
        try
        {
            if (debounce) await Task.Delay(350, request.Token);
            var result = await Shell.SearchService.BrowseAsync(new(keyword, year, _genre == "全部" ? null : _genre, append ? _nextOffset : 0), request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (generation != _generation) return;
            if (!append) _subjects.Clear();
            _subjects.AddRange(result.Subjects.Where(s => _subjects.All(existing => existing.BangumiId != s.BangumiId)));
            _nextOffset = result.NextOffset;
            _hasMore = result.HasMore;
            if (append) _visibleCount += result.Subjects.Count;
            _status.Text = $"{result.Status} · 日漫优先 · {AnimeSeriesGrouping.Group(_subjects).Count} 个系列 / {_subjects.Count} 个版本";
            RenderCards();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally
        {
            if (generation == _generation) { _more.IsEnabled = true; _request = null; }
        }
    }

    private void RenderCards()
    {
        IEnumerable<AnimeSubject> sorted = _sortIndex switch
        {
            1 => _subjects.OrderBy(s => s.Year == 0 ? int.MaxValue : s.Year).ThenBy(s => s.AirDate),
            2 => _subjects.OrderBy(s => s.Title),
            _ => _subjects.OrderByDescending(s => s.Year).ThenByDescending(s => s.AirDate)
        };
        var groups = AnimeIndexOrdering.JapaneseFirst(AnimeSeriesGrouping.Group(sorted));
        Shell.Prefetch.Prefetch(groups.Take(3).Select(g => g.Cover));
        var lookup = groups.ToDictionary(g => g.Cover.Id);
        _more.Visibility = _hasMore || groups.Count > _visibleCount ? Visibility.Visible : Visibility.Collapsed;
        _cards.Content = groups.Count == 0 ? AnimeUi.Muted("没有符合条件的番剧，试试其他关键词、题材或年份。") : AnimeUi.SubjectCards(
            groups.Take(_visibleCount).Select(g => g.Cover), subject => Shell.OpenSeries(lookup[subject.Id]), 202, 170,
            actionLabel: s => lookup[s.Id].Subjects.Count > 1 ? $"展开 {lookup[s.Id].Subjects.Count} 个版本" : "季度 / 剧场版",
            caption: s => $"{(s.Year > 0 ? s.Year.ToString() : "年份待定")} · {lookup[s.Id].Subjects.Count} 个已加载版本",
            displayTitle: s => lookup[s.Id].Title);
    }
}
