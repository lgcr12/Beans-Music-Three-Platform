using Beans.Windows.Rebuild.Models;
using Microsoft.UI.Xaml.Controls;

namespace Beans.Windows.Rebuild.Controls.PlatformSelector;

public sealed partial class PlatformSelector : UserControl
{
    private bool _suppressSelection;

    public PlatformSelector() => InitializeComponent();
    public event EventHandler<string>? PlatformChanged;

    public void SetPlatforms(IReadOnlyList<MusicPlatformDescriptor> platforms, string? selectedPlatformId)
    {
        _suppressSelection = true;
        PlatformList.ItemsSource = platforms;
        PlatformList.SelectedItem = platforms.FirstOrDefault(platform => platform.StableId == selectedPlatformId && platform.IsEnabled);
        _suppressSelection = false;
    }

    public void Select(string platformId)
    {
        if (PlatformList.ItemsSource is not IEnumerable<MusicPlatformDescriptor> platforms) return;
        _suppressSelection = true;
        PlatformList.SelectedItem = platforms.FirstOrDefault(platform => platform.StableId == platformId);
        _suppressSelection = false;
    }

    private void PlatformList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || PlatformList.SelectedItem is not MusicPlatformDescriptor { IsEnabled: true } platform) return;
        PlatformChanged?.Invoke(this, platform.StableId);
    }
}
