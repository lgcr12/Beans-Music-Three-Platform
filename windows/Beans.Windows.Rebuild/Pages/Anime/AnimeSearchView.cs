using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Anime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace Beans.Windows.Rebuild.Pages.Anime;

public sealed class AnimeSearchView:AnimeView
{
    private readonly StackPanel _subjects=AnimeUi.Stack(),_songs=AnimeUi.Stack();
    private readonly ContentControl _filters=new(){HorizontalContentAlignment=HorizontalAlignment.Stretch};
    private readonly List<AnimeThemeSong> _matches=[];
    private string _type="全部";
    public AnimeSearchView(AnimeShell shell,string query):base(shell)
    {
        var input=new TextBox {Text=query,PlaceholderText="中文名、日文名或罗马音"};var search=AnimeUi.SearchBox(input,()=>Shell.OpenSearch(input.Text));search.MaxWidth=480;
        var heading=new Grid {ColumnDefinitions={new(){Width=new GridLength(1,GridUnitType.Star)},new(){Width=new GridLength(620)}}};heading.Children.Add(AnimeUi.Text("动漫搜索",22));Grid.SetColumn(search,1);heading.Children.Add(search);
        var body=AnimeUi.Stack(heading,AnimeUi.Card(AnimeUi.Stack(AnimeUi.SectionTitle("匹配番剧"),_subjects)),AnimeUi.Card(AnimeUi.Stack(AnimeUi.SectionTitle("相关主题曲"),_filters,_songs)));
        body.Margin=new Thickness(32,12,32,24);_songs.Spacing=0;Content=AnimeUi.Scroll(body);RenderFilters();Loaded+=(_,_)=>_ = Run(()=>Load(query));
    }
    private async Task Load(string query)
    {
        _subjects.Children.Add(AnimeUi.Muted("正在搜索 Bangumi 公开资料…"));
        var result=await Shell.SearchService.BrowseAsync(new(query),Lifetime.Token);Lifetime.Token.ThrowIfCancellationRequested();
        var groups=AnimeSeriesGrouping.Group(result.Subjects);var subjects=result.Subjects;
        _subjects.Children.Clear();_subjects.Children.Add(AnimeUi.Muted(result.Status));
        if(subjects.Count==0){_subjects.Children.Add(AnimeUi.Muted("没有找到该作品，试试其他名称。"));RenderSongs();return;}
        foreach(var series in groups)
        {
            var subject=series.Cover;
            var row=new Grid {ColumnSpacing=24,ColumnDefinitions={new(){Width=new GridLength(150)},new(){Width=new GridLength(1,GridUnitType.Star)}}};row.Children.Add(AnimeUi.Poster(subject.PosterUri,156));
            var summary=AnimeUi.Muted(subject.Synopsis);summary.MaxLines=2;summary.TextTrimming=TextTrimming.CharacterEllipsis;
            var info=AnimeUi.Stack(AnimeUi.Text(series.Title,20),AnimeUi.Muted(subject.JapaneseTitle),AnimeUi.Muted($"{series.Subjects.Count} 个已加载版本 · 季度 / 剧场版"),summary,AnimeUi.Button("展开系列版本",()=>Shell.OpenSeries(series),"primary","\uE76C"));info.Spacing=6;Grid.SetColumn(info,1);row.Children.Add(info);_subjects.Children.Add(row);
        }
        foreach(var subject in subjects)_matches.AddRange(Shell.Catalog.GetThemeSongs(subject.Id));
        if(result.HasMore)_subjects.Children.Add(AnimeUi.Button("在索引中查看并加载更多",()=>Shell.BrowseIndex(query:query)));
        RenderSongs();
        foreach(var subject in subjects)
        {var matched=await Shell.Matcher.MatchAsync(subject,Lifetime.Token);Lifetime.Token.ThrowIfCancellationRequested();Shell.Session.Register(subject,matched);_matches.RemoveAll(s=>s.AnimeId==subject.Id);_matches.AddRange(matched);RenderSongs();}
    }
    private void RenderFilters()=>_filters.Content=AnimeUi.Chips(new[]{"全部","OP","ED","插曲","角色曲"},_type,value=>{_type=value;RenderFilters();RenderSongs();});
    private void RenderSongs(){_songs.Children.Clear();foreach(var song in _matches.Where(s=>_type=="全部"||AnimeUi.TypeLabel(s.Type)==_type))_songs.Children.Add(AnimeSongPresentation.Row(Shell,song,Lifetime.Token));if(_songs.Children.Count==0)_songs.Children.Add(AnimeUi.Muted("暂无已确认的此类主题曲。"));}
}
