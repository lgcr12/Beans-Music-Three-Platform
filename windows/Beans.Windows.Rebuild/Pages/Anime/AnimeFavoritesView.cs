using Beans.Windows.Rebuild.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace Beans.Windows.Rebuild.Pages.Anime;

public sealed class AnimeFavoritesView:AnimeView
{
    private readonly ContentControl _subjects=new(){HorizontalContentAlignment=HorizontalAlignment.Stretch};
    private readonly StackPanel _songs=AnimeUi.Stack();
    private readonly TextBox _query=new(){PlaceholderText="搜索收藏的番剧或主题曲"};
    public AnimeFavoritesView(AnimeShell shell):base(shell)
    {
        _query.TextChanged+=(_,_)=>Render();
        var body=AnimeUi.Stack(AnimeUi.Text("把喜欢的故事和旋律，留在这里",24),AnimeUi.SearchBox(_query),AnimeUi.SectionTitle("收藏的番剧"),_subjects,AnimeUi.SectionTitle("收藏的主题曲"),_songs);
        var card=AnimeUi.Card(body);card.Margin=new Thickness(48,8,48,24);card.Padding=new Thickness(28);_songs.Spacing=0;Content=AnimeUi.Scroll(card);Render();
    }
    private void Render()
    {
        var q=Services.Anime.AnimeCatalogService.Normalize(_query.Text);
        var subjects=Shell.Catalog.GetSubjects().Where(s=>Shell.Session.State.Subjects.Contains(s.Id)&&Services.Anime.AnimeCatalogService.Normalize(s.Title+s.JapaneseTitle+s.RomajiTitles).Contains(q)).ToArray();
        _subjects.Content=subjects.Length==0?Empty("还没有收藏的番剧","在番剧专题点亮爱心，把喜欢的作品留在这里。"):
            AnimeUi.SubjectCards(subjects,Shell.OpenDetail,202,160,false,s=>_ = Run(async()=>{await Shell.Session.ToggleSubjectAsync(s.Id,Lifetime.Token);Render();}));
        _songs.Children.Clear();
        foreach(var subject in Shell.Catalog.GetSubjects())foreach(var song in Shell.Catalog.GetThemeSongs(subject.Id).Concat(Shell.Session.State.SongDetails.Values.Where(s=>s.AnimeId==subject.Id).Select(s=>s.ToSong())).DistinctBy(s=>s.Id).Where(s=>Shell.Session.State.Songs.Contains(s.Id)&&Services.Anime.AnimeCatalogService.Normalize(s.Title+s.Artist).Contains(q)))
        {
            var row=new Grid {ColumnSpacing=14,Padding=new Thickness(0,10,0,10),ColumnDefinitions={new(){Width=new GridLength(44)},new(){Width=new GridLength(1,GridUnitType.Star)},new(){Width=GridLength.Auto},new(){Width=GridLength.Auto}}};
            row.Children.Add(AnimeUi.Poster(subject.PosterUri,52));var info=AnimeUi.Stack(AnimeUi.Text(song.Title,14),AnimeUi.Muted(song.Artist+" · "+song.Version,11));info.Spacing=5;Grid.SetColumn(info,1);row.Children.Add(info);
            var enter=AnimeUi.Button(subject.Title,()=>Shell.OpenDetail(subject));Grid.SetColumn(enter,2);row.Children.Add(enter);
            var remove=AnimeUi.IconButton("取消收藏 "+song.Title,"\uEB52",()=>_ = Run(async()=>{await Shell.Session.ToggleSongAsync(song,Lifetime.Token);Render();}));Grid.SetColumn(remove,3);row.Children.Add(remove);_songs.Children.Add(row);
        }
        if(_songs.Children.Count==0)_songs.Children.Add(Empty("还没有收藏的主题曲","在曲目右侧的更多菜单中选择收藏。"));
    }
    private static FrameworkElement Empty(string title,string subtitle)
    {
        var icon=AnimeUi.Icon("\uEB51",25);icon.Foreground=AnimeUi.Brush(142,177,215);
        var text=AnimeUi.Stack(AnimeUi.Text(title,14),AnimeUi.Muted(subtitle,12));text.Spacing=5;
        var row=AnimeUi.Inline(icon,text);row.Margin=new Thickness(16,24,16,24);return row;
    }
}
