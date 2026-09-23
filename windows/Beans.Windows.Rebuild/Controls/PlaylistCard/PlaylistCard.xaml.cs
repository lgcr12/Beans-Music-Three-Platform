using Beans.Windows.Rebuild.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Beans.Windows.Rebuild.Controls.PlaylistCard;

public sealed partial class PlaylistCard : UserControl
{
    public PlaylistCard()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyData();
    }

    public event EventHandler? PlayRequested;

    public string Title { get => _title; set { _title = value; ApplyData(); } }
    public string Subtitle { get => _subtitle; set { _subtitle = value; ApplyData(); } }
    public string ImageUri { get => _imageUri; set { _imageUri = value; ApplyData(); } }
    public string PlayCount { get => _playCount; set { _playCount = value; ApplyData(); } }
    public PlatformId SourcePlatform { get => _sourcePlatform; set { _sourcePlatform = value; ApplyData(); } }
    public string AutomationName => $"播放歌单 {Title}";

    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private string _imageUri = string.Empty;
    private string _playCount = string.Empty;
    private PlatformId _sourcePlatform = PlatformId.Local;

    private void ApplyData()
    {
        if (TitleText is null) return;
        TitleText.Text = Title;
        SubtitleText.Text = Subtitle;
        PlayCountText.Text = PlayCount;
        SourceBadge.Platform = SourcePlatform;
        CardButton.SetValue(AutomationProperties.NameProperty, AutomationName);
        if (Uri.TryCreate(ImageUri, UriKind.Absolute, out var uri)) CoverImage.Source = new BitmapImage(uri) { DecodePixelWidth = 420 };
    }

    private void CardButton_Click(object sender, RoutedEventArgs e) => PlayRequested?.Invoke(this, EventArgs.Empty);
    private void CoverBorder_SizeChanged(object sender, SizeChangedEventArgs e) => CoverBorder.Height = e.NewSize.Width;
    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e) => PlayOverlay.Opacity = 1;
    private void Card_PointerExited(object sender, PointerRoutedEventArgs e) => PlayOverlay.Opacity = 0;
}
