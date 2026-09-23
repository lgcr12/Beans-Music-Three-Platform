namespace Beans.Windows.Rebuild.Infrastructure.Navigation;

public sealed record NavigationRequest(string Route, object? Parameter = null);

public interface INavigationService
{
    string CurrentRoute { get; }
    event EventHandler<NavigationRequest>? NavigationRequested;
    void Navigate(string route, object? parameter = null);
}
