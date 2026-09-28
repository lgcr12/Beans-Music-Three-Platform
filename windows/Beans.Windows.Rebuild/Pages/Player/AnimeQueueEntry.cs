using Beans.Windows.Rebuild.Services.Playback;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Beans.Windows.Rebuild.Pages.Player;

public sealed class AnimeQueueEntry(PlaybackItem item, bool isCurrent, bool isPlaying)
{
    public PlaybackItem Item { get; } = item;
    public string Title => Item.Title;
    public string Artist => Item.Artist;
    public string ArtworkUri => Item.ArtworkUri;
    public string DurationText => Item.DurationText;
    public string PlayGlyph => isCurrent && isPlaying ? "\uE769" : "\uE768";
    public double GlowOpacity => isCurrent ? 1 : 0;
    public Brush TitleBrush => new SolidColorBrush(isCurrent ? ColorHelper.FromArgb(255, 16, 52, 91) : ColorHelper.FromArgb(255, 18, 34, 54));
    public Brush BorderBrush => new SolidColorBrush(ColorHelper.FromArgb(isCurrent ? (byte)235 : (byte)70, 255, 255, 255));
    public Brush CardBrush => new LinearGradientBrush
    {
        StartPoint = new(0, 0), EndPoint = new(1, 0),
        GradientStops =
        {
            new() { Color = ColorHelper.FromArgb(isCurrent ? (byte)100 : (byte)42, 245, 250, 255), Offset = 0 },
            new() { Color = ColorHelper.FromArgb(isCurrent ? (byte)100 : (byte)42, 245, 250, 255), Offset = .23 },
            new() { Color = ColorHelper.FromArgb(isCurrent ? (byte)205 : (byte)180, 245, 250, 255), Offset = .34 },
            new() { Color = ColorHelper.FromArgb(isCurrent ? (byte)195 : (byte)170, 240, 247, 255), Offset = 1 }
        }
    };
}
