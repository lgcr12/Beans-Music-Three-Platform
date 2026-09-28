using Beans.Windows.Rebuild.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
namespace Beans.Windows.Rebuild.Pages.Anime;

public sealed class AnimeDetailView:AnimeView
{
    private readonly AnimeSubject _subject;
    private IReadOnlyList<AnimeThemeSong> _songs=[];
    private readonly ContentControl _hero=new(){HorizontalContentAlignment=HorizontalAlignment.Stretch};
    private readonly Image _backdrop=new(){Stretch=Stretch.UniformToFill,Opacity=.9,IsHitTestVisible=false};
    private string _backdropUri="";
    private readonly StackPanel _list=AnimeUi.Stack();
    private readonly TextBlock _status=AnimeUi.Muted("正在分别核验 QQ 音乐与网易云…",11);
    private readonly ContentControl _filters=new(){HorizontalContentAlignment=HorizontalAlignment.Stretch};
    private string _type="全部";private bool _busy;private bool _loading = true;
    public AnimeDetailView(AnimeShell shell,AnimeSubject subject,Action? back=null):base(shell)
    {
        _subject=subject;_songs=Shell.Prefetch.GetSnapshot(subject);if(_songs.Count==0)_songs=Shell.Catalog.GetThemeSongs(subject.Id);_list.Spacing=0;
        var root=new Grid {Background=AnimeUi.Sky};
        root.Children.Add(_backdrop);
        // Protect the copy on the left while retaining the poster on the right.
        root.Children.Add(new Border {IsHitTestVisible=false,Background=new LinearGradientBrush
        {
            StartPoint=new(0,0),EndPoint=new(1,0),GradientStops=
            {
                new(){Color=Microsoft.UI.ColorHelper.FromArgb(246,237,246,255),Offset=0},
                new(){Color=Microsoft.UI.ColorHelper.FromArgb(235,237,246,255),Offset=.42},
                new(){Color=Microsoft.UI.ColorHelper.FromArgb(80,237,246,255),Offset=.70},
                new(){Color=Microsoft.UI.ColorHelper.FromArgb(20,237,246,255),Offset=1}
            }
        }});
        root.Children.Add(new Border {IsHitTestVisible=false,Background=new LinearGradientBrush
        {
            StartPoint=new(0,0),EndPoint=new(0,1),GradientStops=
            {
                new(){Color=Microsoft.UI.ColorHelper.FromArgb(0,237,246,255),Offset=0},
                new(){Color=Microsoft.UI.ColorHelper.FromArgb(0,237,246,255),Offset=.40},
                new(){Color=Microsoft.UI.ColorHelper.FromArgb(205,237,246,255),Offset=1}
            }
        }});
        var table=AnimeUi.Card(AnimeUi.Stack(AnimeUi.SectionTitle("主题曲"),_filters,_list,_status));
        var rail=BuildRail();var lower=new Grid {ColumnSpacing=16,RowSpacing=16};lower.ColumnDefinitions.Add(new(){Width=new GridLength(2.2,GridUnitType.Star)});lower.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});lower.RowDefinitions.Add(new(){Height=GridLength.Auto});lower.RowDefinitions.Add(new(){Height=GridLength.Auto});lower.Children.Add(table);Grid.SetColumn(rail,1);lower.Children.Add(rail);
        lower.SizeChanged+=(_,e)=>{var narrow=e.NewSize.Width<1000;Grid.SetColumnSpan(table,narrow?2:1);Grid.SetColumn(rail,narrow?0:1);Grid.SetRow(rail,narrow?1:0);Grid.SetColumnSpan(rail,narrow?2:1);};
        var body=AnimeUi.Stack(_hero,lower);body.Spacing=20;body.Margin=new Thickness(48,10,48,24);body.MaxWidth=1480;
        if(back is not null)body.Children.Insert(0,AnimeUi.Button("返回系列版本",back));
        SizeChanged+=(_,e)=>body.Margin=new Thickness(e.NewSize.Width<1100?24:48,10,e.NewSize.Width<1100?24:48,24);
        root.Children.Add(AnimeUi.Scroll(body));Content=root;RenderHero();RenderSongs();RenderFilters();Loaded+=(_,_)=>_ = Run(Load);
    }
    private async Task Load()
    {
        async Task Metadata()
        {
            await Shell.Catalog.RefreshMetadataAsync(_subject, Lifetime.Token);
            Lifetime.Token.ThrowIfCancellationRequested();
            RenderHero();
        }
        async Task Themes()
        {
            void Show(IReadOnlyList<AnimeThemeSong> songs)
            {
                if (Lifetime.IsCancellationRequested || songs.Count == 0) return;
                _songs = songs;
                _status.Text = $"{_songs.Count} 个主题曲版本 · 已匹配 {_songs.Count(s => s.Matches.Count > 0)} 个";
                RenderSongs();
            }
            var progress = new Progress<IReadOnlyList<AnimeThemeSong>>(Show);
            Show(Shell.Prefetch.GetSnapshot(_subject));
            var songs = await Shell.Prefetch.ObserveAsync(_subject, Lifetime.Token, progress);
            Lifetime.Token.ThrowIfCancellationRequested();
            _songs = songs;
            Shell.Session.Register(_subject, songs);
            _loading = false;
            _status.Text = songs.Count == 0 ? "暂未取得主题曲资料，请稍后重试。" : $"{songs.Count} 个曲目版本 · 已匹配 {songs.Count(s => s.Matches.Count > 0)} 个 · 播放时验证平台权限";
            RenderSongs();
        }
        await Task.WhenAll(Metadata(), Themes());
    }
    private void RenderHero()
    {
        if(_backdropUri!=_subject.PosterUri&&Uri.TryCreate(_subject.PosterUri,UriKind.Absolute,out var backdropUri))
        {_backdrop.Source=new BitmapImage(backdropUri);_backdropUri=_subject.PosterUri;}
        var content=new Grid {ColumnSpacing=30,Padding=new Thickness(8,8,8,0)};content.ColumnDefinitions.Add(new(){Width=new GridLength(205)});content.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        var poster=AnimeUi.Poster(_subject.PosterUri,290);content.Children.Add(poster);
        var title=AnimeUi.Text(_subject.Title,30);var info=AnimeUi.Stack(title,AnimeUi.Muted(_subject.JapaneseTitle,16),AnimeUi.Muted(_subject.YearSeasonText,13));info.Spacing=10;info.VerticalAlignment=VerticalAlignment.Center;
        var synopsis=AnimeUi.Text(_subject.Synopsis,13);synopsis.MaxLines=2;synopsis.TextTrimming=TextTrimming.CharacterEllipsis;synopsis.LineHeight=23;synopsis.Margin=new Thickness(0,4,0,0);info.Children.Add(synopsis);
        var links=AnimeUi.Inline();links.Spacing=14;
        void Link(string label,string? url){if(!Uri.TryCreate(url,UriKind.Absolute,out var address)||address.Scheme!="https")return;var b=AnimeUi.Button(label,()=>_ = Run(()=>Shell.Links.OpenAsync(url,Lifetime.Token)),glyph:"\uE8A7");b.Foreground=AnimeUi.Ink;b.Padding=new Thickness(0,3,0,3);b.MinHeight=28;b.FontSize=12;links.Children.Add(b);}
        Link("观看番剧",_subject.ExternalLinks.WatchUrl);Link("Bangumi 资料",_subject.ExternalLinks.BangumiUrl);Link("动画官网",_subject.ExternalLinks.OfficialUrl);info.Children.Add(links);
        var play=AnimeUi.Button("播放本季主题曲",()=>_ = Run(()=>Operate(true)),"violet","\uE768");
        var queue=AnimeUi.Button("加入队列",()=>_ = Run(()=>Operate(false)),"ghost","\uE142");
        var add=AnimeUi.Button("添加到 Beans 歌单",()=>_ = Run(async()=>await new AnimePlaylistDialog(Shell,_songs).ShowAsync()),"ghost","\uE710");
        var favorite=AnimeUi.IconButton("收藏番剧","\uEB51",()=>_ = Run(async()=>{await Shell.Session.ToggleSubjectAsync(_subject.Id,Lifetime.Token);RenderHero();}));favorite.Background=AnimeUi.Brush(98,162,240,35);favorite.Width=favorite.Height=42;
        if(Shell.Session.State.Subjects.Contains(_subject.Id))favorite.Foreground=AnimeUi.Mint;
        var actions=AnimeUi.Inline(play,queue,add,favorite);actions.Spacing=10;actions.Margin=new Thickness(0,8,0,0);info.Children.Add(actions);
        info.SizeChanged+=(_,e)=>{var compact=e.NewSize.Width<680;play.Padding=queue.Padding=add.Padding=new Thickness(compact?12:20,9,compact?12:20,9);play.FontSize=queue.FontSize=add.FontSize=compact?12:14;};
        Grid.SetColumn(info,1);content.Children.Add(info);_hero.Content=content;
    }
    private FrameworkElement BuildRail()
    {
        var info=AnimeUi.Stack(AnimeUi.SectionTitle("作品资料"));info.Spacing=12;
        foreach(var (label,value) in new[]{("中文名",_subject.Title),("原名",_subject.JapaneseTitle),("首播年份",_subject.Year>0?_subject.Year.ToString():"待定"),("版本",string.IsNullOrWhiteSpace(_subject.Format)?"以作品标题为准":_subject.Format),("类型",string.Join(" / ",_subject.Genres))})
        {var row=new Grid {ColumnDefinitions={new(){Width=new GridLength(74)},new(){Width=new GridLength(1,GridUnitType.Star)}}};row.Children.Add(AnimeUi.Muted(label,11));var text=AnimeUi.Text(value,11);Grid.SetColumn(text,1);row.Children.Add(text);info.Children.Add(row);}
        var more=AnimeUi.Button("阅读完整简介",()=>_ = Run(async()=>{await new ContentDialog {Title=_subject.Title,Content=AnimeUi.Scroll(AnimeUi.Text(_subject.Synopsis)),CloseButtonText="关闭",XamlRoot=XamlRoot,RequestedTheme=ElementTheme.Light}.ShowAsync();}));more.Foreground=AnimeUi.Brush(169,192,220);more.Padding=new Thickness(0,2,0,2);more.FontSize=11;info.Children.Add(more);
        info.Children.Add(AnimeUi.SectionTitle("继续探索"));
        foreach(var subject in Shell.Catalog.GetSubjects().Where(s=>s.Id!=_subject.Id).OrderByDescending(s=>s.Genres.Intersect(_subject.Genres).Any()).Take(3))
        {
            var row=new Grid {ColumnSpacing=12,ColumnDefinitions={new(){Width=new GridLength(40)},new(){Width=new GridLength(1,GridUnitType.Star)},new(){Width=GridLength.Auto}}};row.Children.Add(AnimeUi.Poster(subject.PosterUri,50));var title=AnimeUi.Stack(AnimeUi.Text(subject.Title,12),AnimeUi.Muted(string.Join(" / ",subject.Genres),10));title.Spacing=5;Grid.SetColumn(title,1);row.Children.Add(title);var b=AnimeUi.IconButton("打开 "+subject.Title,"\uE76C",()=>Shell.OpenDetail(subject));Grid.SetColumn(b,2);row.Children.Add(b);info.Children.Add(row);
        }
        return AnimeUi.Card(info);
    }
    private void RenderFilters()=>_filters.Content=AnimeUi.Chips(new[]{"全部","OP","ED","插曲","角色曲"},_type,value=>{_type=value;RenderFilters();RenderSongs();});
    private void RenderSongs(){_list.Children.Clear();foreach(var song in _songs.Where(s=>_type=="全部"||AnimeUi.TypeLabel(s.Type)==_type))_list.Children.Add(AnimeSongPresentation.Row(Shell,song,Lifetime.Token));if(_list.Children.Count==0)_list.Children.Add(AnimeUi.Muted(_songs.Count==0?(_loading?"正在读取主题曲资料…":"暂无已核验的主题曲资料。"):"暂无此类型主题曲。",13));}
    private async Task Operate(bool play)
    {
        if(_busy)return;var items=_songs.Where(s=>s.Version=="完整版").Select(s=>s.Matches.FirstOrDefault()).Where(i=>i is not null).Cast<SearchResultItem>().DistinctBy(i=>(i.Platform,i.NativeId)).ToArray();
        if(items.Length==0){Shell.Report("尚无可靠的平台匹配，请等待核验或稍后重试。");return;}_busy=true;
        try{if(play){var r=await Shell.Player.PlaySearchResultsAsync(items,Lifetime.Token);Shell.Report(r.SafeMessage);}else{var count=0;foreach(var item in items){var r=await Shell.Player.QueueSearchResultAsync(item,false,Lifetime.Token);if(r.IsSuccess)count++;}Shell.Report($"已加入队列 {count} / {items.Length} 首；受限曲目未加入。");}}finally{_busy=false;}
    }
}
