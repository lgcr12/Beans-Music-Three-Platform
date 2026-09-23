using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Beans.Windows.Rebuild.Models;
using Windows.System;

namespace Beans.Windows.Rebuild.Shell;

public sealed partial class TopBar : UserControl
{
    public TopBar() => InitializeComponent();
    public Grid DragRegion => TitleBarDragRegion;
    public event EventHandler<string>? SearchSubmitted;
    public event EventHandler<string>? SearchTextChanged;
    public event EventHandler<SearchSuggestion>? SuggestionChosen;
    public event EventHandler<string>? RouteRequested;

    public void SetSearchText(string text)
    {
        if (SearchTextBox.Text == text) return;
        SearchTextBox.Text = text;
    }

    public void SetSuggestions(IReadOnlyList<SearchSuggestion> suggestions)
    {
        SuggestionList.ItemsSource = suggestions;
        SuggestionPopup.IsOpen = suggestions.Count > 0 && SearchTextBox.Text.Trim().Length >= 2;
    }

    public void HideSuggestions() => SuggestionPopup.IsOpen = false;

    public void ApplyResponsiveState(double windowWidth)
    {
        var compact = windowWidth < 1024;
        NotificationButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ProfileButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        NotificationColumn.Width = compact ? new GridLength(0) : new GridLength(40);
        ProfileColumn.Width = compact ? new GridLength(0) : new GridLength(40);
        ActionsGrid.ColumnSpacing = compact ? 8 : 16;
    }

    private void SearchTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || string.IsNullOrWhiteSpace(SearchTextBox.Text)) return;
        SearchSubmitted?.Invoke(this, SearchTextBox.Text.Trim());
        e.Handled = true;
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => SearchTextChanged?.Invoke(this, SearchTextBox.Text);

    private void SuggestionList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchSuggestion suggestion) SuggestionChosen?.Invoke(this, suggestion);
        SuggestionPopup.IsOpen = false;
    }

    private void Theme_Click(object sender, RoutedEventArgs e) => RouteRequested?.Invoke(this, "settings");
    private void Notification_Click(object sender, RoutedEventArgs e) => RouteRequested?.Invoke(this, "notifications");
    private void Profile_Click(object sender, RoutedEventArgs e) => RouteRequested?.Invoke(this, "accounts");
}
