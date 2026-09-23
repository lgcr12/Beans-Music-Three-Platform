using System.ComponentModel;
using System.Globalization;
using Beans.Windows.Rebuild.Services.Playback;
using Beans.Windows.Rebuild.Services.Platforms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Beans.Windows.Rebuild.Pages.Settings;

public sealed partial class SettingsPage : UserControl, INotifyPropertyChanged
{
    private const string VolumeSettingKey = "settings.default-volume";
    private const string DarkAppearanceSettingKey = "settings.dark-appearance";
    private readonly IPlaybackService _player;
    private readonly IPlatformPreferenceStore _preferences;
    private bool _initializing = true;
    private string _statusText = "音量偏好保存在本机";
    public string StatusText { get => _statusText; private set { if (_statusText == value) return; _statusText = value; PropertyChanged?.Invoke(this, new(nameof(StatusText))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public SettingsPage() : this(
        App.Services.GetRequiredService<IPlaybackService>(),
        App.Services.GetRequiredService<IPlatformPreferenceStore>()) { }

    public SettingsPage(IPlaybackService player, IPlatformPreferenceStore preferences)
    {
        _player = player;
        _preferences = preferences;
        InitializeComponent();
        var volume = TryReadVolume(_preferences.GetString(VolumeSettingKey), out var saved)
            ? saved
            : _player.VolumePercent;
        DefaultVolumeSlider.Value = Math.Clamp(volume, 0, 100);
        _player.SetVolume(DefaultVolumeSlider.Value);
        AppearanceToggle.IsOn = string.Equals(_preferences.GetString(DarkAppearanceSettingKey), "true", StringComparison.OrdinalIgnoreCase);
        ApplyAppearance(AppearanceToggle.IsOn);
        _initializing = false;
    }

    private void Appearance_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        var dark = AppearanceToggle.IsOn;
        try
        {
            _preferences.SetString(DarkAppearanceSettingKey, dark ? "true" : "false");
            ApplyAppearance(dark);
            StatusText = dark ? "深色外观已启用 · 已保存在本机" : "浅色外观已启用 · 已保存在本机";
        }
        catch { StatusText = "外观已切换，但暂时无法保存偏好"; }
    }

    private void ApplyAppearance(bool dark)
    {
        SetBrush("ContentBackgroundBrush", dark ? "#14221F" : "#F8FBFA");
        SetBrush("AppBackgroundBrush", dark ? "#10201C" : "#F3F8F6");
        SetBrush("SurfacePrimaryBrush", dark ? "#1B2C28" : "#FFFFFF");
        SetBrush("SurfaceSecondaryBrush", dark ? "#223732" : "#F4F8F7");
        SetBrush("TextPrimaryBrush", dark ? "#E8F5EF" : "#103C33");
        SetBrush("TextSecondaryBrush", dark ? "#B4CEC4" : "#4F6F68");
        SetBrush("TextTertiaryBrush", dark ? "#8DAAA0" : "#819B95");
        SetBrush("BorderDefaultBrush", dark ? "#553B5C56" : "#1A103C33");
        SetBrush("DividerBrush", dark ? "#443B5C56" : "#10103C33");
        SetBrush("Primary050Brush", dark ? "#243C34" : "#EFFAF6");
        if (XamlRoot?.Content is FrameworkElement root)
            root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
    }

    private static void SetBrush(string key, string hex)
    {
        if (Application.Current.Resources[key] is SolidColorBrush brush)
            brush.Color = ParseColor(hex);
    }

    private static Color ParseColor(string hex)
    {
        var value = hex.TrimStart('#');
        if (value.Length == 6) value = "FF" + value;
        return Color.FromArgb(Convert.ToByte(value[..2], 16), Convert.ToByte(value[2..4], 16),
            Convert.ToByte(value[4..6], 16), Convert.ToByte(value[6..8], 16));
    }

    private void Volume_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_initializing) return;
        var volume = Math.Clamp(e.NewValue, 0, 100);
        _player.SetVolume(volume);
        try
        {
            _preferences.SetString(VolumeSettingKey, volume.ToString("R", CultureInfo.InvariantCulture));
            StatusText = $"音量已设为 {volume:0}% · 已保存在本机";
        }
        catch (Exception)
        {
            StatusText = $"音量已设为 {volume:0}%，但暂时无法保存偏好";
        }
    }

    internal static bool TryReadVolume(string? value, out double volume) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out volume) &&
        double.IsFinite(volume) && volume is >= 0 and <= 100;
}
