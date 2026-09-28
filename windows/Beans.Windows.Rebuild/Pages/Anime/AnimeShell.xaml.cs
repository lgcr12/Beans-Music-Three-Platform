using Beans.Windows.Rebuild.Infrastructure.Navigation;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Anime;
using Beans.Windows.Rebuild.Services.BeansPlaylists;
using Beans.Windows.Rebuild.Services.Lyrics;
using Beans.Windows.Rebuild.Services.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace Beans.Windows.Rebuild.Pages.Anime;

public sealed partial class AnimeShell : UserControl
{
    public IAnimeCatalogService Catalog {get;}
    public IAnimeSearchService SearchService {get;}
    public IAnimeSongMatcher Matcher {get;}
    public AnimeThemePrefetch Prefetch {get;}
    public IAnimeExternalLinkService Links {get;}
    public IBeansPlaylistService Playlists {get;}
    public IPlaybackService Player {get;}
    public ILyricsService Lyrics {get;}
    public AnimeSession Session {get;}
    private readonly INavigationService _navigation;
    private bool _seeking;
    private string _route="home";
    public bool IsDark {get;private set;}
    public event Action<bool>? CaptionThemeChanged;
    public UIElement TitleBarDragRegion => TitleBarDrag;
    public FrameworkElement TransitionContent => ViewHost;
    public AnimeShell(IAnimeCatalogService catalog,IAnimeSearchService search,IAnimeSongMatcher matcher,IAnimeExternalLinkService links,IBeansPlaylistService playlists,IPlaybackService player,ILyricsService lyrics,AnimeSession session,INavigationService navigation,AnimeThemePrefetch prefetch,string? subjectId=null)
    {
        Catalog=catalog;SearchService=search;Matcher=matcher;Links=links;Playlists=playlists;Player=player;Lyrics=lyrics;Session=session;_navigation=navigation;Prefetch=prefetch;
        InitializeComponent();
        Loaded+=(_,_)=>{Player.PropertyChanged+=PlayerChanged;UpdatePlayer();};
        Unloaded+=(_,_)=>Player.PropertyChanged-=PlayerChanged;
        SizeChanged+=(_,e)=>{Brand.Visibility=e.NewSize.Width<1050?Visibility.Collapsed:Visibility.Visible;Volume.Visibility=e.NewSize.Width<1000?Visibility.Collapsed:Visibility.Visible;};
        Progress.AddHandler(PointerPressedEvent,new Microsoft.UI.Xaml.Input.PointerEventHandler((_,_)=>{_seeking=true;Player.BeginSeek();}),true);
        Progress.AddHandler(PointerReleasedEvent,new Microsoft.UI.Xaml.Input.PointerEventHandler((_,_)=>{if(_seeking)Player.EndSeek(TimeSpan.FromSeconds(Progress.Value));_seeking=false;}),true);
        Progress.KeyUp+=(_,_)=>Player.Seek(TimeSpan.FromSeconds(Progress.Value));
        Unloaded+=(_,_)=>{if(_seeking)Player.EndSeek(TimeSpan.FromSeconds(Progress.Value));};
        if(subjectId is not null && catalog.GetSubjects().FirstOrDefault(s=>s.Id==subjectId) is {} subject) OpenDetail(subject); else Show("home");
    }
    private void PlayerChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs e)
    {
        if(e.PropertyName is nameof(IPlaybackService.DurationSeconds) or nameof(IPlaybackService.Current)) UpdatePlayer();
    }
    private void UpdatePlayer()
    {
        Progress.Maximum=Math.Max(1,Player.DurationSeconds);
        EmptyArtwork.Visibility=Player.Current is null?Visibility.Visible:Visibility.Collapsed;
        PlayerArtwork.Visibility=Player.Current is null?Visibility.Collapsed:Visibility.Visible;
    }
    private void Show(string route)
    {
        SetTheme(route);

        ViewHost.Content=route switch {"index"=>new AnimeIndexView(this),"favorites"=>new AnimeFavoritesView(this),_=>new AnimeHomeView(this)};
    }
    public void OpenDetail(AnimeSubject subject){SetTheme("detail");Report("");ViewHost.Content=new AnimeDetailView(this,subject);}
    public void OpenSeries(AnimeSeries series){SetTheme("series");Report("");ViewHost.Content=new AnimeSeriesView(this,series);}
    public void OpenEdition(AnimeSubject subject,AnimeSeries series){SetTheme("detail");Report("");ViewHost.Content=new AnimeDetailView(this,subject,()=>OpenSeries(series));}
    public void OpenSearch(string query){SetTheme("search");Report("");ViewHost.Content=new AnimeSearchView(this,query);}
    public void Report(string message){Notice.Text=message;NoticeHost.Visibility=string.IsNullOrWhiteSpace(message)?Visibility.Collapsed:Visibility.Visible;}
    public void CloseLyrics(){LyricsHost.Content=null;LyricsHost.Visibility=Visibility.Collapsed;SetTheme(_route);}
    private void Nav_Click(object sender,RoutedEventArgs e){if(sender is Button {Tag:string route})Show(route);}
    private void Back_Click(object sender,RoutedEventArgs e)=>_navigation.Navigate("home");
    private void OpenPlayer_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => _navigation.Navigate("player", "anime");
    private void Previous_Click(object sender,RoutedEventArgs e)=>Player.Previous();
    private void Next_Click(object sender,RoutedEventArgs e)=>Player.Next();
    private void Toggle_Click(object sender,RoutedEventArgs e)=>Player.TogglePlayPause();
    private void Volume_Changed(object sender,Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)=>Player.SetVolume(e.NewValue);
    private void Lyrics_Click(object sender,RoutedEventArgs e){ApplyPlayerTheme(true);LyricsHost.Content=new AnimeLyricsView(this);LyricsHost.Visibility=Visibility.Visible;}
    public void SetScene(string asset) => Backdrop.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/Home/Anime/"+asset));
    public void BrowseIndex(string? genre=null,string? query=null) {SetTheme("index");ViewHost.Content=new AnimeIndexView(this,genre,query);}
    public void SetTheme(string route)
    {
        _route=route;IsDark=route=="detail";if(route!="home")SetScene("anime-hero-ultrawide-v1.png");NoticeHost.Visibility=Visibility.Collapsed;
        Backdrop.Visibility=IsDark?Visibility.Collapsed:Visibility.Visible;
        LeftWash.Visibility=IsDark?Visibility.Collapsed:Visibility.Visible;
        BackdropWash.Opacity=IsDark?0:route=="home"?.42:.30;
        LeftWash.Opacity=IsDark?0:route=="home"?.48:.36;
        Header.Background=IsDark?AnimeUi.Brush(23,41,62):route=="home"?AnimeUi.Brush(225,241,252,190):AnimeUi.Brush(249,253,255,220);
        Brand.Foreground=IsDark?AnimeUi.Brush(241,246,255):AnimeUi.Ink;
        BackButton.Foreground=MotionButton.Foreground=Brand.Foreground;
        foreach(var button in new[]{HomeNav,IndexNav,FavoritesNav})
        {
            var active=Equals(button.Tag,route)||(route is "detail" or "search" or "series"&&Equals(button.Tag,"index"));
            button.Background=active?(IsDark?AnimeUi.Brush(118,112,238,80):AnimeUi.Brush(102,162,240,65)):AnimeUi.Brush(255,255,255,0);
            button.Foreground=IsDark?AnimeUi.Brush(226,235,250):AnimeUi.Ink;
        }
        Root.Background=IsDark?AnimeUi.Night:AnimeUi.Brush(229,243,255);
        ApplyPlayerTheme(IsDark);
    }
    private void ApplyPlayerTheme(bool dark)
    {
        CaptionThemeChanged?.Invoke(dark);
        PlayerSurface.Background=dark?AnimeUi.Brush(11,25,43,242):new Microsoft.UI.Xaml.Media.LinearGradientBrush
        {
            StartPoint=new global::Windows.Foundation.Point(0,0), EndPoint=new global::Windows.Foundation.Point(1,1),
            GradientStops={new Microsoft.UI.Xaml.Media.GradientStop {Color=Microsoft.UI.ColorHelper.FromArgb(230,138,159,240),Offset=0},new Microsoft.UI.Xaml.Media.GradientStop {Color=Microsoft.UI.ColorHelper.FromArgb(230,117,183,232),Offset=.52},new Microsoft.UI.Xaml.Media.GradientStop {Color=Microsoft.UI.ColorHelper.FromArgb(232,156,141,235),Offset=1}}
        };
        PlayerSurface.BorderBrush=dark?AnimeUi.Brush(117,116,246,65):AnimeUi.Brush(255,255,255,110);
        PlayButton.Background=dark?AnimeUi.Brush(112,103,255):AnimeUi.Brush(246,251,255);
        PlayButton.Foreground=dark?AnimeUi.Brush(255,255,255):AnimeUi.Brush(86,143,211);
        Progress.Foreground=dark?AnimeUi.Brush(128,119,255):AnimeUi.Brush(247,252,255);
        Progress.Background=dark?AnimeUi.Brush(145,174,216,110):AnimeUi.Brush(255,255,255,110);
        Volume.Foreground=Progress.Foreground;
    }
    private void Shuffle_Click(object sender,RoutedEventArgs e){Player.ToggleShuffle();ToolTipService.SetToolTip(ShuffleButton,Player.ShuffleModeText);}
    private void Repeat_Click(object sender,RoutedEventArgs e){Player.CycleRepeatMode();ToolTipService.SetToolTip(RepeatButton,Player.RepeatModeText);}
    private async void Motion_Click(object sender,RoutedEventArgs e)
    {
        try{await Session.UpdateAsync(s=>s.ReduceMotion=!s.ReduceMotion);Report(Session.State.ReduceMotion?"已开启减少动效":"已恢复动效");}catch(Exception){Report("设置保存失败，请重试。");}
    }
    private async void Queue_Click(object sender,RoutedEventArgs e)
    {
        var list=new ListView {ItemsSource=Player.Queue,DisplayMemberPath="Title",MaxHeight=360,SelectionMode=ListViewSelectionMode.None};
        var dialog=new ContentDialog {Title=$"播放队列 · {Player.Queue.Count} 首",Content=list,CloseButtonText="关闭",XamlRoot=XamlRoot,RequestedTheme=ElementTheme.Light};
        try {await dialog.ShowAsync();}catch(Exception){Report("请先关闭当前弹窗。");}
    }
}
