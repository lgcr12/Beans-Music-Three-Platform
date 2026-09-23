using System.Collections.ObjectModel;
using Beans.Windows.Rebuild.Services.Library;
using Beans.Windows.Rebuild.Services.LocalMusic;
using Beans.Windows.Rebuild.Services.MusicUniverse;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Beans.Windows.Rebuild.Pages.MusicUniverse;

public sealed partial class MusicUniversePage : UserControl
{
    private readonly IMusicUniverseService? _service;
    private bool _loaded;

    public ObservableCollection<MusicUniverseMetric> Metrics { get; } = [];
    public ObservableCollection<MusicUniverseArtistInsight> Artists { get; } = [];
    public ObservableCollection<MusicUniverseAlbumInsight> Albums { get; } = [];
    public ObservableCollection<MusicUniverseSourceInsight> Sources { get; } = [];
    public ObservableCollection<MusicUniverseRelationship> Relationships { get; } = [];

    // Kept only so the page compiles before the shell integration task wires
    // the shared services. It shows an explicit unavailable state, never preview data.
    public MusicUniversePage()
    {
        InitializeComponent();
        Loaded += Page_Loaded;
    }

    public MusicUniversePage(IUserLibraryService userLibrary, ILocalMusicCatalog localMusicCatalog)
        : this(new MusicUniverseService(userLibrary, localMusicCatalog))
    {
    }

    public MusicUniversePage(IMusicUniverseService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        InitializeComponent();
        Loaded += Page_Loaded;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_service is null)
        {
            ShowError("本机音乐数据服务尚未连接。请完成 Shell 依赖接线后重试。");
            RefreshButton.IsEnabled = false;
            return;
        }

        RefreshButton.IsEnabled = false;
        LoadingPanel.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Collapsed;
        ContentPanel.Visibility = Visibility.Collapsed;
        try
        {
            var snapshot = await _service.LoadAsync();
            Replace(Metrics, snapshot.Metrics);
            Replace(Artists, snapshot.Artists);
            Replace(Albums, snapshot.Albums);
            Replace(Sources, snapshot.Sources);
            Replace(Relationships, snapshot.Relationships);
            LoadingPanel.Visibility = Visibility.Collapsed;
            if (!snapshot.HasData)
            {
                EmptyPanel.Visibility = Visibility.Visible;
                return;
            }

            StatusText.Text = snapshot.StatusText;
            ContentPanel.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            ShowError("读取已取消，可以重新刷新统计。");
        }
        catch (Exception)
        {
            ShowError("无法读取本机索引、收藏或播放记录，请稍后重试。");
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Collapsed;
        ContentPanel.Visibility = Visibility.Collapsed;
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }
}
