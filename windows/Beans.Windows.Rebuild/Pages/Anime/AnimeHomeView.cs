using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Anime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
namespace Beans.Windows.Rebuild.Pages.Anime;

public sealed class AnimeHomeView : AnimeView
{
    private readonly DispatcherTimer _carousel=new(){Interval=TimeSpan.FromSeconds(14)};
    private readonly StackPanel _today=AnimeUi.Stack();
    private readonly StackPanel _stories=AnimeUi.Stack();
    private IReadOnlyList<AnimeSubject> _todaySubjects=[];
    private int _renderVersion;
    public AnimeHomeView(AnimeShell shell):base(shell)
    {
        Shell.SetScene("anime-hero-ultrawide-v1.png");
        var title=AnimeUi.Text("在另一个世界，\n遇见你的主题曲",40);title.FontFamily=new FontFamily("STZhongsong, SimSun");title.FontWeight=Microsoft.UI.Text.FontWeights.Bold;title.Foreground=AnimeUi.Brush(61,109,164);title.LineHeight=48;
        var subtitle=AnimeUi.Text("从一部日漫开始，听见它的故事",18);subtitle.Foreground=AnimeUi.Brush(78,128,184);
        var query=new TextBox {PlaceholderText="搜索日漫名称、日文名或罗马音"};var search=AnimeUi.SearchBox(query,()=>Shell.OpenSearch(query.Text));search.MaxWidth=600;search.HorizontalAlignment=HorizontalAlignment.Stretch;
        var categories=AnimeUi.Inline();categories.Spacing=6;
        foreach(var label in new[]{"热血","青春","治愈","奇幻","音乐"}){var b=AnimeUi.Button(label,()=>Shell.BrowseIndex(label));b.Padding=new Thickness(9,3,9,3);b.FontWeight=Microsoft.UI.Text.FontWeights.Normal;categories.Children.Add(b);}
        var hero=AnimeUi.Stack(title,subtitle,search,categories);hero.Spacing=16;hero.Width=600;hero.MaxWidth=640;hero.HorizontalAlignment=HorizontalAlignment.Left;hero.Margin=new Thickness(44,10,0,0);hero.Height=264;
        var stories=AnimeUi.Card(AnimeUi.Stack(AnimeUi.SectionTitle("历史上的今天"),_stories));
        RenderStories(Shell.Prefetch.HomeSubjects.Count > 0 ? Shell.Prefetch.HomeSubjects : Shell.Catalog.GetSubjects().Take(3).ToArray());
        _today.Spacing=10;var todayCard=AnimeUi.Card(AnimeUi.Stack(AnimeUi.SectionTitle("今日主题曲"),_today));
        var highlights=new Grid {ColumnSpacing=14,RowSpacing=14};highlights.ColumnDefinitions.Add(new(){Width=new GridLength(3,GridUnitType.Star)});highlights.ColumnDefinitions.Add(new(){Width=new GridLength(2,GridUnitType.Star)});
        highlights.RowDefinitions.Add(new(){Height=GridLength.Auto});highlights.RowDefinitions.Add(new(){Height=GridLength.Auto});highlights.Children.Add(stories);Grid.SetColumn(todayCard,1);highlights.Children.Add(todayCard);
        highlights.SizeChanged+=(_,e)=>{var narrow=e.NewSize.Width<820;Grid.SetColumnSpan(stories,narrow?2:1);Grid.SetColumn(todayCard,narrow?0:1);Grid.SetRow(todayCard,narrow?1:0);Grid.SetColumnSpan(todayCard,narrow?2:1);};
        var carousel=AnimeUi.Inline();carousel.HorizontalAlignment=HorizontalAlignment.Center;carousel.Spacing=8;
        var slides=new[]{"anime-hero-ultrawide-v1.png"};int slide=0;
        void Advance(int delta){slide=(slide+delta+slides.Length)%slides.Length;Shell.SetScene(slides[slide]);}
        carousel.Children.Add(AnimeUi.IconButton("上一幅","\uE76B",()=>Advance(-1)));carousel.Children.Add(AnimeUi.Muted("—  ·  ·  ·",16));carousel.Children.Add(AnimeUi.IconButton("下一幅","\uE76C",()=>Advance(1)));
        var body=AnimeUi.Stack(hero,highlights,carousel);body.Spacing=10;body.Margin=new Thickness(32,0,32,8);Content=AnimeUi.Scroll(body);
        foreach(var subject in (Shell.Prefetch.HomeSubjects.Count > 0 ? Shell.Prefetch.HomeSubjects : Shell.Catalog.GetSubjects().Take(3).ToArray()))
            if ((Shell.Prefetch.GetSnapshot(subject).FirstOrDefault() ?? Shell.Catalog.GetThemeSongs(subject.Id).FirstOrDefault()) is { } song) AddToday(subject,song);
        _carousel.Tick+=(_,_)=>{if(!Shell.Session.State.ReduceMotion)Advance(1);};
        Loaded+=(_,_)=>{_carousel.Start();_ = Run(LoadToday);};Unloaded+=(_,_)=>_carousel.Stop();
    }
    private async Task LoadToday()
    {
        var initial = Shell.Prefetch.HomeSubjects.Count > 0 ? Shell.Prefetch.HomeSubjects : Shell.Catalog.GetSubjects().Take(3).ToArray();
        _todaySubjects = initial;
        RenderStories(initial);
        var first = MatchAndRenderAsync(initial, ++_renderVersion);
        try
        {
            await Shell.Prefetch.WarmHomeAsync().WaitAsync(Lifetime.Token);
            Lifetime.Token.ThrowIfCancellationRequested();
            var ready = Shell.Prefetch.HomeSubjects;
            if (ready.Count > 0 && !SameSubjects(ready, initial))
            {
                _todaySubjects = ready;
                RenderStories(ready);
                await MatchAndRenderAsync(ready, ++_renderVersion);
            }
        }
        finally { await first; }
    }

    private async Task MatchAndRenderAsync(IReadOnlyList<AnimeSubject> subjects, int version)
    {
        _today.Children.Clear();
        var slots = subjects.ToDictionary(s => s.Id, s => new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = AnimeUi.Muted($"{s.Title} · 正在准备主题曲…", 12)
        });
        foreach (var slot in slots.Values) _today.Children.Add(slot);
        void Show(AnimeSubject subject, IReadOnlyList<AnimeThemeSong> songs)
        {
            if (Lifetime.IsCancellationRequested || version != _renderVersion) return;
            var song = songs.FirstOrDefault(s => s.Version == "完整版") ?? songs.FirstOrDefault();
            if (song is not null) slots[subject.Id].Content = TodayRow(subject, song);
        }
        await Task.WhenAll(subjects.Select(async subject =>
        {
            Show(subject, Shell.Prefetch.GetSnapshot(subject));
            var progress = new Progress<IReadOnlyList<AnimeThemeSong>>(songs => Show(subject, songs));
            var songs = await Shell.Prefetch.ObserveAsync(subject, Lifetime.Token, progress);
            Lifetime.Token.ThrowIfCancellationRequested();
            Shell.Session.Register(subject, songs);
            Show(subject, songs);
            if (songs.Count == 0 && version == _renderVersion)
                slots[subject.Id].Content = AnimeUi.Muted($"{subject.Title} · 暂无主题曲资料", 12);
        }));
    }
    private static bool SameSubjects(IReadOnlyList<AnimeSubject> left, IReadOnlyList<AnimeSubject> right) =>
        left.Count == right.Count && left.Select(item => item.Id).SequenceEqual(right.Select(item => item.Id));
    private void RenderStories(IReadOnlyList<AnimeSubject> subjects)
    {
        _stories.Children.Clear();
        _stories.Children.Add(AnimeUi.SubjectCards(subjects,Shell.OpenDetail,164,145,true));
    }
    private void AddToday(AnimeSubject subject,AnimeThemeSong song)
        => _today.Children.Add(TodayRow(subject, song));
    private FrameworkElement TodayRow(AnimeSubject subject,AnimeThemeSong song)
    {
        var row=new Grid {ColumnSpacing=14,Padding=new Thickness(0,6,0,6)};row.ColumnDefinitions.Add(new(){Width=new GridLength(56)});row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var result=song.Matches.FirstOrDefault();var cover=AnimeUi.Poster(result?.CoverUri is {Length:>0} c?c:subject.PosterUri,64,true);row.Children.Add(cover);
        var text=AnimeUi.Stack(AnimeUi.Text(song.Title,14),AnimeUi.Muted(song.Artist),AnimeUi.Muted($"{subject.Title} · 第{song.Season}季 {AnimeUi.TypeLabel(song.Type)}",11));text.Spacing=5;text.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(text,1);row.Children.Add(text);
        var play=AnimeUi.IconButton(result is null?"查看主题曲":"播放 "+song.Title,"\uE768",()=>{if(result is null)Shell.OpenDetail(subject);else _ = Run(async()=>{var r=await Shell.Player.PlaySearchResultAsync(result,true,Lifetime.Token);Shell.Report(r.SafeMessage);});});play.Foreground=AnimeUi.Brush(62,142,237);Grid.SetColumn(play,2);row.Children.Add(play);return row;
    }
}
