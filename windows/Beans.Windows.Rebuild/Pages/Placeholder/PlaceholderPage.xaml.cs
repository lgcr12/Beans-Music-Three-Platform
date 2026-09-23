using Beans.Windows.Rebuild.Infrastructure.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Beans.Windows.Rebuild.Pages.Placeholder;

public sealed partial class PlaceholderPage : UserControl
{
    private readonly INavigationService _navigation;
    public string PageTitle { get; }

    public PlaceholderPage(string pageTitle, string? detail, INavigationService navigation)
    {
        PageTitle = pageTitle;
        _navigation = navigation;
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(detail)) DetailText.Text = $"“{detail}” 的相关详情将在后续阶段完成。当前路由已携带平台与原生内容标识。";
    }

    private void BackHome_Click(object sender, RoutedEventArgs e) => _navigation.Navigate("home");
}
