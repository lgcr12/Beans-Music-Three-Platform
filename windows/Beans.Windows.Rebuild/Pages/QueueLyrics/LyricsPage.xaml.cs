using System.Collections.ObjectModel;
using System.ComponentModel;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Lyrics;
using Beans.Windows.Rebuild.Services.Playback;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Beans.Windows.Rebuild.Pages.QueueLyrics;

public sealed class LyricLineRow : INotifyPropertyChanged
{
    public LyricLineRow(TimeSpan timestamp, string text, string translation)
    {
        Timestamp = timestamp;
        Text = text;
        Translation = translation;
    }

    public TimeSpan Timestamp { get; set; }
    public string Text { get; set; }
    public string Translation { get; set; }
    public string TimestampText => Timestamp.ToString(@"mm\:ss");
    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            PropertyChanged?.Invoke(this, new(nameof(IsActive)));
            PropertyChanged?.Invoke(this, new(nameof(ActiveVisibility)));
        }
    }
    public Visibility ActiveVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class LyricsPage : UserControl
{
    private readonly IPlaybackService _player;
    private readonly ILyricsService _lyrics;
    private CancellationTokenSource? _loadCancellation;
    private LyricsRequest? _currentRequest;
    private LyricDocument? _document;
    private int _activeIndex = -1;
    private bool _isSubscribed;

    public ObservableCollection<LyricLineRow> Lines { get; } = [];
    public IPlaybackService Player => _player;

    public LyricsPage() : this(
        App.Services.GetRequiredService<IPlaybackService>(),
        App.Services.GetRequiredService<ILyricsService>())
    {
    }

    public LyricsPage(IPlaybackService player, ILyricsService lyrics)
    {
        _player = player;
        _lyrics = lyrics;
        InitializeComponent();
        Loaded += LyricsPage_Loaded;
        Unloaded += LyricsPage_Unloaded;
    }

    private async void LyricsPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_isSubscribed)
        {
            _player.PropertyChanged += Player_PropertyChanged;
            _isSubscribed = true;
        }
        await LoadCurrentAsync();
    }

    private void LyricsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_isSubscribed)
        {
            _player.PropertyChanged -= Player_PropertyChanged;
            _isSubscribed = false;
        }
        CancelPendingLoad();
    }

    private async void Player_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IPlaybackService.Current)) await LoadCurrentAsync();
        else if (e.PropertyName == nameof(IPlaybackService.PositionSeconds)) UpdateActiveLine();
    }

    private async Task LoadCurrentAsync(bool forceRefresh = false)
    {
        CancelPendingLoad();
        Lines.Clear();
        _document = null;
        _activeIndex = -1;

        var current = _player.Current;
        if (current is null)
        {
            _currentRequest = null;
            TrackContextText.Text = "选择歌曲后显示本地歌词";
            SourceText.Text = "未选择来源";
            ShowState("尚未选择歌曲", "当前歌曲没有可显示的歌词内容。", "\uE8D2");
            return;
        }

        var request = LyricsRequest.FromPlaybackItem(current);
        _currentRequest = request;
        if (forceRefresh) _lyrics.Invalidate(request);
        TrackContextText.Text = string.IsNullOrWhiteSpace(current.Artist) ? current.Title : $"{current.Title} · {current.Artist}";
        SourceText.Text = request.Platform.ToDisplayName();
        ShowLoading();

        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        try
        {
            var result = await _lyrics.GetLyricsAsync(request, cancellation.Token);
            if (cancellation.IsCancellationRequested || _loadCancellation != cancellation) return;
            Render(result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (_loadCancellation == cancellation) _loadCancellation = null;
            cancellation.Dispose();
        }
    }

    private void Render(LyricsResult result)
    {
        if (result.IsSuccess && result.Document is { } document)
        {
            _document = document;
            foreach (var line in document.Lines)
                Lines.Add(new LyricLineRow(line.Timestamp, line.Text, line.Translation ?? string.Empty));
            StatePanel.Visibility = Visibility.Collapsed;
            LyricList.Visibility = Visibility.Visible;
            UpdateActiveLine();
            return;
        }

        var (title, icon, retry) = result.State switch
        {
            LyricsLoadState.Unsupported => ("暂不支持在线歌词", "\uE946", false),
            LyricsLoadState.Error => ("歌词加载失败", "\uE783", true),
            LyricsLoadState.Empty => ("歌词内容为空", "\uE8D2", false),
            _ => ("没有找到歌词", "\uE8D2", false)
        };
        ShowState(title, result.SafeMessage, icon, retry);
    }

    private void UpdateActiveLine()
    {
        if (_document is null || Lines.Count == 0) return;
        var position = TimeSpan.FromSeconds(Math.Max(0, _player.PositionSeconds)) - _document.Offset;
        var index = -1;
        for (var i = 0; i < Lines.Count && Lines[i].Timestamp <= position; i++) index = i;
        if (index == _activeIndex) return;
        if (_activeIndex >= 0 && _activeIndex < Lines.Count) Lines[_activeIndex].IsActive = false;
        _activeIndex = index;
        if (_activeIndex >= 0) Lines[_activeIndex].IsActive = true;
        LyricList.SelectedItem = index >= 0 ? Lines[index] : null;
        if (index >= 0) LyricList.ScrollIntoView(Lines[index], ScrollIntoViewAlignment.Leading);
    }

    private void ShowLoading()
    {
        LyricList.Visibility = Visibility.Collapsed;
        StatePanel.Visibility = Visibility.Visible;
        StateIcon.Visibility = Visibility.Collapsed;
        LoadingRing.Visibility = Visibility.Visible;
        LoadingRing.IsActive = true;
        StateTitle.Text = "正在加载歌词";
        StateMessage.Text = "正在查找当前歌曲的歌词内容…";
        RetryButton.Visibility = Visibility.Collapsed;
    }

    private void ShowState(string title, string message, string glyph, bool canRetry = false)
    {
        LyricList.Visibility = Visibility.Collapsed;
        StatePanel.Visibility = Visibility.Visible;
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        StateIcon.Visibility = Visibility.Visible;
        StateIcon.Glyph = glyph;
        StateTitle.Text = title;
        StateMessage.Text = message;
        RetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await LoadCurrentAsync(forceRefresh: true);

    private void CancelPendingLoad()
    {
        _loadCancellation?.Cancel();
        _loadCancellation = null;
    }
}
