using Beans.Windows.Rebuild.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace Beans.Windows.Rebuild.Pages.Anime;

internal static class AnimeSongPresentation
{
    public static FrameworkElement Row(AnimeShell shell,AnimeThemeSong song,CancellationToken ct,bool dark=false,bool favorite=true)
    {
        var grid=new Grid {ColumnSpacing=12,Padding=new Thickness(0,10,0,10),MinHeight=76};
        foreach(var width in new[]{new GridLength(48),new GridLength(1,GridUnitType.Star),new GridLength(76),new GridLength(126),new GridLength(108)})grid.ColumnDefinitions.Add(new(){Width=width});
        var result=song.Matches.FirstOrDefault();var art=result?.CoverUri;
        var cover=string.IsNullOrWhiteSpace(art)?new Grid {Height=48,Background=AnimeUi.Brush(117,153,203,25),CornerRadius=new CornerRadius(6),Children={AnimeUi.Icon("\uE8D6",20)}}:AnimeUi.Poster(art,48,true);
        grid.Children.Add(cover);
        var title=AnimeUi.Text(song.Title,14,dark);title.MaxLines=1;title.TextTrimming=TextTrimming.CharacterEllipsis;
        var info=AnimeUi.Stack(title,AnimeUi.Muted(song.Artist,11,dark));info.Spacing=4;info.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(info,1);grid.Children.Add(info);
        var version=AnimeUi.Stack(AnimeUi.Muted($"第{song.Season}季 {AnimeUi.TypeLabel(song.Type)}",11,dark),AnimeUi.Muted(song.Version,10,dark));version.Spacing=4;version.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(version,2);grid.Children.Add(version);
        var sources=AnimeUi.Stack();sources.Spacing=0;sources.VerticalAlignment=VerticalAlignment.Center;
        foreach(var (platform,label) in new[]{(PlatformId.QqMusic,"QQ"),(PlatformId.NetEaseMusic,"网易云")})
        {
            var match=song.PlatformMatches.FirstOrDefault(m=>m.Platform==platform);
            var b=AnimeUi.Button(label+" · "+(match?.MatchState??"待匹配"),()=>_ = Perform(async()=>await shell.Player.PlaySearchResultAsync(match!.Result!,true,ct)));
            b.FontSize=10;b.FontWeight=Microsoft.UI.Text.FontWeights.Normal;b.MinHeight=22;b.Padding=new Thickness(0,2,0,2);
            b.Foreground=match?.Result is not null?(dark?AnimeUi.Mint:AnimeUi.Brush(52,150,144)):dark?AnimeUi.Brush(170,189,214):AnimeUi.Brush(131,158,189);
            b.IsEnabled=match?.Result is not null;ToolTipService.SetToolTip(b,match?.SafeMessage??"正在核验平台录音");sources.Children.Add(b);
        }
        Grid.SetColumn(sources,3);grid.Children.Add(sources);
        var play=AnimeUi.IconButton("播放 "+song.Title,"\uE768",()=>_ = Perform(async()=>await shell.Player.PlaySearchResultAsync(result!,true,ct)),dark);play.IsEnabled=result is not null;
        var queue=AnimeUi.IconButton("加入队列 "+song.Title,"\uE142",()=>_ = Perform(async()=>await shell.Player.QueueSearchResultAsync(result!,false,ct)),dark);queue.IsEnabled=result is not null;
        var more=AnimeUi.IconButton("更多操作 "+song.Title,"\uE712",()=>{},dark);var menu=new MenuFlyout();
        var save=new MenuFlyoutItem {Text=shell.Session.State.Songs.Contains(song.Id)?"取消收藏主题曲":"收藏主题曲",Icon=new FontIcon {Glyph="\uEB51"}};
        save.Click+=async(_,_)=>{try{await shell.Session.ToggleSongAsync(song,ct);save.Text=shell.Session.State.Songs.Contains(song.Id)?"取消收藏主题曲":"收藏主题曲";}catch(OperationCanceledException){}catch(Exception){shell.Report("收藏保存失败。");}};
        if(favorite)menu.Items.Add(save);
        var add=new MenuFlyoutItem {Text="添加到 Beans 歌单",IsEnabled=result is not null};add.Click+=async(_,_)=>{try{await new AnimePlaylistDialog(shell,[song]).ShowAsync();}catch(Exception){shell.Report("请先关闭当前弹窗。");}};menu.Items.Add(add);more.Flyout=menu;
        var actions=AnimeUi.Inline(play,queue,more);actions.Spacing=0;Grid.SetColumn(actions,4);grid.Children.Add(actions);
        grid.SizeChanged+=(_,e)=>{var narrow=e.NewSize.Width<660;version.Visibility=narrow?Visibility.Collapsed:Visibility.Visible;grid.ColumnDefinitions[2].Width=new GridLength(narrow?0:76);grid.ColumnDefinitions[3].Width=new GridLength(narrow?100:126);};
        return new Border {Child=grid,BorderThickness=new Thickness(0,0,0,1),BorderBrush=dark?AnimeUi.Brush(159,191,228,30):AnimeUi.Brush(169,197,225,35)};
        async Task Perform(Func<Task<Services.Playback.PlaybackOperationResult>> work)
        {try{var response=await work();if(!ct.IsCancellationRequested)shell.Report(response.SafeMessage);}catch(OperationCanceledException){}catch(Exception){shell.Report("当前操作暂不可用，请稍后重试。");}}
    }
}
