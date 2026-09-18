using System.ComponentModel;
using System.Runtime.CompilerServices;
using Windows.Storage;

namespace Beans.Windows;

public sealed class TypographyService : INotifyPropertyChanged
{
    private const string StorageKey = "beans.textScalePercent";
    private double _percent;

    public TypographyService()
    {
        var stored = ApplicationData.Current.LocalSettings.Values[StorageKey];
        _percent = Clamp(stored is IConvertible value ? Convert.ToDouble(value) : 100);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? PercentChanged;

    public double Percent
    {
        get => _percent;
        set
        {
            var next = Clamp(Math.Round(value / 5) * 5);
            if (Math.Abs(next - _percent) < 0.01) return;
            _percent = next;
            ApplicationData.Current.LocalSettings.Values[StorageKey] = next;
            Raise();
            Raise(nameof(DisplayPercent));
            Raise(nameof(CaptionSize));
            Raise(nameof(BodySize));
            Raise(nameof(SubtitleSize));
            Raise(nameof(HeadingSize));
            Raise(nameof(TitleSize));
            PercentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string DisplayPercent => $"{Percent:0}%";
    public double CaptionSize => Scaled(11);
    public double BodySize => Scaled(14);
    public double SubtitleSize => Scaled(16);
    public double HeadingSize => Scaled(20);
    public double TitleSize => Scaled(32);

    public void Reset() => Percent = 100;

    private double Scaled(double size) => Math.Round(size * Percent / 100, 1);
    private static double Clamp(double value) => Math.Clamp(value, 85, 140);

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
