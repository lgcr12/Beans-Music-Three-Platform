namespace Beans.Windows.Rebuild.Infrastructure.Navigation;

public sealed class NavigationService : INavigationService
{
    public string CurrentRoute { get; private set; } = "home";
    public event EventHandler<NavigationRequest>? NavigationRequested;

    public void Navigate(string route, object? parameter = null)
    {
        if (string.IsNullOrWhiteSpace(route)) return;
        CurrentRoute = route.Trim().ToLowerInvariant();
        NavigationRequested?.Invoke(this, new NavigationRequest(CurrentRoute, parameter));
    }
}
