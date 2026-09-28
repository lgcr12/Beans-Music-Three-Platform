using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.BeansPlaylists;
using Beans.Windows.Rebuild.Services.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace Beans.Windows.Rebuild.Pages.Anime;

public sealed class AnimePlaylistDialog : ContentDialog
{
    private readonly AnimeShell _shell;
    private readonly Button _confirm;
    private bool _saving;
    private readonly List<(CheckBox Check,SearchResultItem Track)> _tracks=[];
    private readonly List<(CheckBox Check,BeansPlaylist Playlist)> _playlists=[];
    private readonly StackPanel _playlistPanel=AnimeUi.Stack();
    private readonly TextBlock _count=AnimeUi.Text("选择要添加的主题曲");
    private readonly TextBlock _error=AnimeUi.Text("");
    private readonly TextBox _search=new(){PlaceholderText="搜索 Beans 歌单"};
    private readonly TextBox _newName=new(){PlaceholderText="新歌单名称（可选）",MaxLength=80};
    public AnimePlaylistDialog(AnimeShell shell,IReadOnlyList<AnimeThemeSong> songs)
    {
        _shell=shell;PrimaryButtonText="";CloseButtonText="";XamlRoot=shell.XamlRoot;RequestedTheme=ElementTheme.Light;
        Background=AnimeUi.Brush(248,250,255);Foreground=AnimeUi.Ink;CornerRadius=new CornerRadius(16);
        Resources["ContentDialogMinWidth"]=500d;Resources["ContentDialogMaxWidth"]=520d;
        PrimaryButtonStyle=(Style)AnimeUi.Theme["AnimeVioletButtonStyle"];CloseButtonStyle=(Style)AnimeUi.Theme["AnimeButtonStyle"];
        var icon=new Border {Child=AnimeUi.Icon("\uE8D6",25),Width=42,Height=42,CornerRadius=new CornerRadius(10),Background=AnimeUi.Brush(139,175,218)};
        var labels=AnimeUi.Stack(AnimeUi.Text("添加到 Beans 歌单",18),_count);labels.Spacing=4;_count.FontSize=11;_count.Foreground=AnimeUi.Brush(133,153,180);
        Title=AnimeUi.Inline(icon,labels);
        var songPanel=AnimeUi.Stack();songPanel.Spacing=2;songPanel.Visibility=Visibility.Collapsed;
        foreach(var song in songs)
        {
            var track=song.Matches.FirstOrDefault();if(track is null)continue;
            var check=new CheckBox {Content=AnimeUi.Text(song.Title+" · "+song.Version+" · "+track.Platform.ToDisplayName(),12),IsChecked=true,Style=(Style)AnimeUi.Theme["AnimeChoiceStyle"]};
            check.Checked+=(_,_)=>UpdateCount();check.Unchecked+=(_,_)=>UpdateCount();_tracks.Add((check,track));songPanel.Children.Add(check);
        }
        var adjust=AnimeUi.Button("调整所选歌曲",()=>songPanel.Visibility=songPanel.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible);adjust.FontSize=11;adjust.Padding=new Thickness(0);adjust.MinHeight=24;
        _search.TextChanged+=(_,_)=>{foreach(var p in _playlists)p.Check.Visibility=p.Playlist.Title.Contains(_search.Text,StringComparison.OrdinalIgnoreCase)?Visibility.Visible:Visibility.Collapsed;};
        _playlistPanel.Spacing=2;
        var newInput=AnimeUi.SearchBox(_newName);newInput.Visibility=Visibility.Collapsed;
        var create=AnimeUi.Button("新建 Beans 歌单",()=>{newInput.Visibility=Visibility.Visible;_newName.Focus(FocusState.Programmatic);},glyph:"\uE710");create.HorizontalAlignment=HorizontalAlignment.Stretch;create.Background=AnimeUi.Brush(112,127,237,20);create.Foreground=AnimeUi.Brush(103,113,225);
        _confirm=AnimeUi.Button("添加",()=>_ = ConfirmAsync(),"violet");_confirm.Width=84;
        var cancel=AnimeUi.Button("取消",()=>{if(!_saving)Hide();});cancel.Width=84;
        Closing+=(_,args)=>args.Cancel=_saving;
        var footer=AnimeUi.Inline(cancel,_confirm);footer.HorizontalAlignment=HorizontalAlignment.Right;footer.Margin=new Thickness(0,8,0,0);
        var body=AnimeUi.Stack(AnimeUi.SearchBox(_search),_playlistPanel,create,newInput,adjust,songPanel,AnimeUi.Muted("已存在的同平台歌曲不会重复添加",11),_error);body.Spacing=10;
        var content=new Grid {RowSpacing=12,RowDefinitions={new(){Height=new GridLength(1,GridUnitType.Star)},new(){Height=GridLength.Auto}}};
        var scroll=new ScrollViewer {Content=body,MaxHeight=Math.Clamp(shell.XamlRoot.Size.Height-360,140,400),VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        void Resize(object sender,SizeChangedEventArgs args)=>scroll.MaxHeight=Math.Clamp(shell.XamlRoot.Size.Height-360,140,400);
        shell.SizeChanged+=Resize;Closed+=(_,_)=>shell.SizeChanged-=Resize;content.Children.Add(scroll);
        Grid.SetRow(footer,1);content.Children.Add(footer);Content=content;UpdateCount();Opened+=async(_,_)=>await Load();
    }
    private void UpdateCount(){_count.Text=$"已选 {_tracks.Count(t=>t.Check.IsChecked==true)} 首主题曲";if(_confirm is not null)_confirm.IsEnabled=!_saving&&_tracks.Any(t=>t.Check.IsChecked==true);}
    private async Task Load()
    {
        try{foreach(var playlist in await _shell.Playlists.GetPlaylistsAsync()){var content=new Grid {ColumnSpacing=12,ColumnDefinitions={new(){Width=new GridLength(42)},new(){Width=new GridLength(1,GridUnitType.Star)}}};
            var cover=playlist.Tracks.FirstOrDefault()?.CoverUri;
            if(!string.IsNullOrWhiteSpace(cover))content.Children.Add(AnimeUi.Poster(cover,42,true));else content.Children.Add(new Border {Height=42,CornerRadius=new CornerRadius(6),Background=AnimeUi.Brush(147,182,225,70),Child=AnimeUi.Icon("\uE8D6",20)});
            var info=AnimeUi.Stack(AnimeUi.Text(playlist.Title,13),AnimeUi.Muted(playlist.TrackCountText,10));info.Spacing=3;Grid.SetColumn(info,1);content.Children.Add(info);
            var check=new CheckBox {Content=content,Style=(Style)AnimeUi.Theme["AnimeChoiceStyle"]};_playlists.Add((check,playlist));_playlistPanel.Children.Add(check);}if(_playlists.Count==0)_playlistPanel.Children.Add(AnimeUi.Muted("还没有 Beans 歌单，可以在下方创建。",13));}
        catch(Exception){_error.Text="歌单读取失败，请关闭后重试。";if(_confirm is not null)_confirm.IsEnabled=false;}
    }
    private async Task ConfirmAsync()
    {
        if(_saving)return;_saving=true;if(_confirm is not null)_confirm.IsEnabled=false;
        try
        {
            var selected=_playlists.Where(p=>p.Check.IsChecked==true).Select(p=>p.Playlist).ToList();
            var name=_newName.Text.Trim();
            if(selected.Count==0&&name.Length==0){_error.Text="请选择歌单，或填写一个新歌单名称。";return;}
            var snapshots=_tracks.Where(t=>t.Check.IsChecked==true).Select(t=>LibraryMediaSnapshot.TryCreate(t.Track,out var snapshot)?snapshot:null).Where(s=>s is not null).Cast<LibraryMediaSnapshot>().ToArray();
            if(snapshots.Length==0){_error.Text="请至少选择一首已匹配歌曲。";return;}
            if(name.Length>0)
            {
                var created=await _shell.Playlists.CreateAsync(name);
                selected.Add(created);
                var check=new CheckBox {Content=AnimeUi.Text(created.Title,13),IsChecked=true,Style=(Style)AnimeUi.Theme["AnimeChoiceStyle"]};
                _playlists.Add((check,created));_playlistPanel.Children.Add(check);_newName.Text="";
            }
            var count=0;foreach(var playlist in selected)count+=await _shell.Playlists.AddTracksAsync(playlist.Id,snapshots);
            _shell.Report($"已向 {selected.Count} 个 Beans 歌单添加 {count} 条歌曲记录，重复歌曲已跳过。");_saving=false;Hide();
        }
        catch(Exception){_error.Text="保存暂未完成，请重试；已保存的歌曲会自动去重。";}
        finally{_saving=false;UpdateCount();}
    }
}
