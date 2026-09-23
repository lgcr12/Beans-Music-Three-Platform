using Beans.Windows.Rebuild.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Beans.Windows.Rebuild.Controls.TrackRow;

public sealed partial class TrackRow : UserControl
{
    public TrackRow()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyData();
    }

    public int Index { get => _index; set { _index = value; ApplyData(); } }
    public string Title { get => _title; set { _title = value; ApplyData(); } }
    public string Artist { get => _artist; set { _artist = value; ApplyData(); } }
    public string Album { get => _album; set { _album = value; ApplyData(); } }
    public string Duration { get => _duration; set { _duration = value; ApplyData(); } }
    public string ImageUri { get => _imageUri; set { _imageUri = value; ApplyData(); } }
    public PlatformId SourcePlatform { get => _sourcePlatform; set { _sourcePlatform = value; ApplyData(); } }

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            _isCurrent = value;
            ApplyData();
        }
    }

    private int _index;
    private string _title = string.Empty;
    private string _artist = string.Empty;
    private string _album = string.Empty;
    private string _duration = string.Empty;
    private string _imageUri = string.Empty;
    private PlatformId _sourcePlatform = PlatformId.Local;
    private bool _isCurrent;

    private void ApplyData()
    {
        if (IndexText is null) return;
        IndexText.Text = Index.ToString();
        TitleText.Text = Title;
        ArtistText.Text = Artist;
        AlbumText.Text = Album;
        DurationText.Text = Duration;
        SourceBadge.Platform = SourcePlatform;
        CurrentBars.Visibility = IsCurrent ? Visibility.Visible : Visibility.Collapsed;
        IndexText.Visibility = IsCurrent ? Visibility.Collapsed : Visibility.Visible;
        if (Uri.TryCreate(ImageUri, UriKind.Absolute, out var uri)) CoverImage.Source = new BitmapImage(uri) { DecodePixelWidth = 80 };
    }
}
