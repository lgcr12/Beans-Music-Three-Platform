using System.ComponentModel;
using Beans.Windows.Rebuild.Services.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace Beans.Windows.Rebuild.Pages.QueueLyrics;
public sealed partial class QueuePage : UserControl, INotifyPropertyChanged
{
    public IPlaybackService Player { get; }
    private string _statusText = "队列为空";
    public string StatusText { get => _statusText; private set { if (_statusText == value) return; _statusText = value; PropertyChanged?.Invoke(this, new(nameof(StatusText))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public QueuePage(IPlaybackService player) { Player = player; InitializeComponent(); Player.PropertyChanged += Player_PropertyChanged; Unloaded += QueuePage_Unloaded; }
    private void Player_PropertyChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName is nameof(IPlaybackService.Current)) StatusText = Player.Queue.Count == 0 ? "队列为空" : $"队列中有 {Player.Queue.Count} 首歌曲"; }
    private void Clear_Click(object sender, RoutedEventArgs e) { Player.ClearQueue(); StatusText = "队列已清空"; }
    private async void Queue_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PlaybackItem item) return;
        await Player.PlayAsync(item);
        StatusText = $"正在播放“{item.Title}”";
    }
    private void QueuePage_Unloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => Player.PropertyChanged -= Player_PropertyChanged;
}
