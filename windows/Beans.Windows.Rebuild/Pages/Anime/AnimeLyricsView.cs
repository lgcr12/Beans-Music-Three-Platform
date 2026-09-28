using System.ComponentModel;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Lyrics;
using Beans.Windows.Rebuild.Services.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
namespace Beans.Windows.Rebuild.Pages.Anime;

public sealed class AnimeLyricsView : AnimeView
{
    private readonly Image _poster=new(){Stretch=Stretch.Uniform,HorizontalAlignment=HorizontalAlignment.Right};
    private readonly Image _ambient=new(){Stretch=Stretch.UniformToFill,Opacity=.28};
    private readonly StackPanel _lines=AnimeUi.Stack();
    private readonly TextBlock _title=AnimeUi.Text("",22,true);
    private readonly TextBlock _context=AnimeUi.Text("",12,true);
    private readonly Slider _seek=new(){Minimum=0,HorizontalAlignment=HorizontalAlignment.Stretch};
    private LyricDocument? _document;
    private CancellationTokenSource? _load;
    private RectangleGeometry? _highlight;
    private TextBlock? _activeText;
    private int _active=-2;
    private bool _seeking;
    public AnimeLyricsView(AnimeShell shell):base(shell)
    {
        Background=AnimeUi.Night;
        var root=new Grid {Background=AnimeUi.Night};root.Children.Add(_ambient);root.Children.Add(_poster);
        root.Children.Add(new Border {Background=new LinearGradientBrush {StartPoint=new(0,0),EndPoint=new(1,0),GradientStops={new(){Color=Microsoft.UI.ColorHelper.FromArgb(255,12,22,40),Offset=0},new(){Color=Microsoft.UI.ColorHelper.FromArgb(240,12,22,40),Offset=.46},new(){Color=Microsoft.UI.ColorHelper.FromArgb(20,12,22,40),Offset=.75}}}});
        var area=new Grid {Padding=new Thickness(40,42,40,28),RowSpacing=16};area.RowDefinitions.Add(new(){Height=GridLength.Auto});area.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});area.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var heading=new Grid {ColumnDefinitions={new(){Width=new GridLength(1,GridUnitType.Star)},new(){Width=GridLength.Auto}}};
        var back=AnimeUi.Button("返回专题",Shell.CloseLyrics,"dark","\uE72B");back.HorizontalAlignment=HorizontalAlignment.Left;heading.Children.Add(back);
        var settings=AnimeUi.Button("歌词设置",()=>_ = Run(Settings),"dark","\uE713");Grid.SetColumn(settings,1);heading.Children.Add(settings);area.Children.Add(heading);
        var lyricGrid=new Grid();lyricGrid.ColumnDefinitions.Add(new(){Width=new GridLength(3,GridUnitType.Star)});lyricGrid.ColumnDefinitions.Add(new(){Width=new GridLength(2,GridUnitType.Star)});
        _lines.Margin=new Thickness(64,0,36,0);_lines.VerticalAlignment=VerticalAlignment.Center;lyricGrid.Children.Add(_lines);Grid.SetRow(lyricGrid,1);area.Children.Add(lyricGrid);
        var footer=AnimeUi.Stack(_title,_context);footer.HorizontalAlignment=HorizontalAlignment.Right;footer.MaxWidth=430;Grid.SetRow(footer,2);area.Children.Add(footer);root.Children.Add(area);Content=root;
        _seek.AddHandler(PointerPressedEvent,new Microsoft.UI.Xaml.Input.PointerEventHandler((_,_)=>{_seeking=true;Shell.Player.BeginSeek();}),true);
        _seek.AddHandler(PointerReleasedEvent,new Microsoft.UI.Xaml.Input.PointerEventHandler((_,_)=>{if(_seeking)Shell.Player.EndSeek(TimeSpan.FromSeconds(_seek.Value));_seeking=false;}),true);
        _seek.KeyUp+=(_,_)=>Shell.Player.Seek(TimeSpan.FromSeconds(_seek.Value));
        Loaded+=(_,_)=>{Shell.Player.PropertyChanged+=Changed;_ = Run(Load);};
        Unloaded+=(_,_)=>{Shell.Player.PropertyChanged-=Changed;_load?.Cancel();if(_seeking)Shell.Player.EndSeek(TimeSpan.FromSeconds(_seek.Value));};
    }
    private void Changed(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName==nameof(IPlaybackService.Current))_ = Run(Load);
        else if(e.PropertyName==nameof(IPlaybackService.PositionSeconds))Update();
    }
    private async Task Load()
    {
        _load?.Cancel();var load=CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);_load=load;
        _document=null;_active=-2;_lines.Children.Clear();_lines.Children.Add(AnimeUi.Text("正在加载歌词…",22,true));
        var current=Shell.Player.Current;_title.Text=Shell.Player.Title+" · "+Shell.Player.Artist;
        var subject=Shell.Session.SubjectFor(current);_context.Text=subject is null?"当前曲目尚未关联已确认的番剧":"当前番剧 · "+subject.Title;
        _poster.Source=subject is not null&&Uri.TryCreate(subject.PosterUri,UriKind.Absolute,out var uri)?new BitmapImage(uri):null;
_ambient.Source=_poster.Source??new BitmapImage(new Uri("ms-appx:///Assets/Home/Anime/fantasy-forest.png"));
        if(current is null){_lines.Children.Clear();_lines.Children.Add(AnimeUi.Text("选择歌曲后，\n在这里听见故事。",30,true));_load=null;load.Dispose();return;}
        try
        {
            var result=await Shell.Lyrics.GetLyricsAsync(LyricsRequest.FromPlaybackItem(current),load.Token);
            if(load.IsCancellationRequested||_load!=load)return;
            _document=result.Document;
            if(_document is null||_document.Lines.Count==0){_lines.Children.Clear();_lines.Children.Add(AnimeUi.Text(result.SafeMessage,22,true));}else Update();
        }
        catch(OperationCanceledException) when(load.IsCancellationRequested){}
        finally {if(_load==load)_load=null;load.Dispose();}
    }
    private void Update()
    {
        _seek.Maximum=Math.Max(1,Shell.Player.DurationSeconds);if(!_seeking)_seek.Value=Shell.Player.PositionSeconds;
        if(_document is null||_document.Lines.Count==0)return;
        var position=TimeSpan.FromSeconds(Shell.Player.PositionSeconds)-_document.Offset;
        var index=-1;for(var i=0;i<_document.Lines.Count;i++){if(_document.Lines[i].Timestamp<=position)index=i;else break;}
        if(index!=_active){_active=index;Render();}
        if(index>=0&&_highlight is not null&&_activeText is not null)
        {
            var end=index+1<_document.Lines.Count?_document.Lines[index+1].Timestamp:TimeSpan.FromSeconds(Shell.Player.DurationSeconds)-_document.Offset;
            var duration=Math.Max(.1,(end-_document.Lines[index].Timestamp).TotalSeconds);
            var fraction=Shell.Session.State.ReduceMotion?1:Math.Clamp((position-_document.Lines[index].Timestamp).TotalSeconds/duration,0,1);
            _highlight.Rect=new(0,0,_activeText.ActualWidth*fraction,Math.Max(100,_activeText.ActualHeight));
        }
    }
    private void Render()
    {
        _lines.Children.Clear();_highlight=null;_activeText=null;
        if(_document is null)return;
        var state=Shell.Session.State;_lines.Spacing=state.LineSpacing;
        for(var i=Math.Max(0,_active-2);i<Math.Min(_document.Lines.Count,Math.Max(0,_active)+3);i++)
        {
            var line=_document.Lines[i];var distance=Math.Abs(i-_active);var text=AnimeUi.Text(line.Text,state.FontSize*(distance==0?1:distance==1?.84:.72),true);
            text.MaxLines=2;var group=AnimeUi.Stack();group.Spacing=4;group.Opacity=distance==0?1:distance==1?.58:.28;
            if(i==_active)
            {
                var activeGrid=new Grid();activeGrid.Children.Add(text);
                var highlight=AnimeUi.Text(line.Text,state.FontSize,true);highlight.MaxLines=2;highlight.Foreground=AnimeUi.Mint;_highlight=new();highlight.Clip=_highlight;activeGrid.Children.Add(highlight);_activeText=highlight;group.Children.Add(activeGrid);
            }
            else group.Children.Add(text);
            if(state.Translation&&!string.IsNullOrWhiteSpace(line.Translation))group.Children.Add(AnimeUi.Text(line.Translation,Math.Max(12,state.FontSize*.55),true));
            _lines.Children.Add(group);
        }
    }
    private async Task Settings()
    {
        var state=Shell.Session.State;var font=new Slider {Style=(Style)AnimeUi.Theme["AnimeSliderStyle"],Header="字号",Minimum=20,Maximum=36,Value=state.FontSize};var spacing=new Slider {Style=(Style)AnimeUi.Theme["AnimeSliderStyle"],Header="行距",Minimum=8,Maximum=32,Value=state.LineSpacing};
        var translation=new CheckBox {Content="显示翻译歌词",IsChecked=state.Translation};var motion=new CheckBox {Content="减少动效",IsChecked=state.ReduceMotion};
        var dialog=new ContentDialog {Title="歌词设置",Content=AnimeUi.Stack(AnimeUi.Text("字号",13),font,AnimeUi.Text("行距",13),spacing,translation,motion),PrimaryButtonText="保存",CloseButtonText="取消",XamlRoot=XamlRoot,RequestedTheme=ElementTheme.Light};
        if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return;
        await Shell.Session.UpdateAsync(s=>{s.FontSize=font.Value;s.LineSpacing=spacing.Value;s.Translation=translation.IsChecked==true;s.ReduceMotion=motion.IsChecked==true;},Lifetime.Token);Render();Update();
    }
}
