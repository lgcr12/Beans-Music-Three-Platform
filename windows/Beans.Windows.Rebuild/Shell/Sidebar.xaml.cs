using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Beans.Windows.Rebuild.Shell;

public sealed partial class Sidebar : UserControl
{
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(Sidebar), new PropertyMetadata(false, OnCompactChanged));

    public Sidebar() => InitializeComponent();
    public event EventHandler<string>? NavigationRequested;

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    private static void OnCompactChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is Sidebar sidebar) sidebar.ApplyCompactMode((bool)args.NewValue);
    }

    private void ApplyCompactMode(bool compact)
    {
        var visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        foreach (var label in new[] { BrandLabel, HomeLabel, DiscoverLabel, LibraryLabel, PlaylistsLabel, FavoritesLabel, LocalLabel, SettingsLabel }) label.Visibility = visibility;
    }

    private void Navigation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string route }) NavigationRequested?.Invoke(this, route);
    }
}
