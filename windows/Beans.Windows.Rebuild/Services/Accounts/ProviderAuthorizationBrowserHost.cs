using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Beans.Windows.Rebuild.Services.Platforms.NetEase;
using Beans.Windows.Rebuild.Services.Platforms.QQ;

namespace Beans.Windows.Rebuild.Services.Accounts;

/// <summary>
/// Hosts provider-owned login pages in isolated WebView2 data folders. The
/// host never interprets or persists credentials; it returns only allowlisted
/// cookies to the platform flow and clears the browser session afterwards.
/// </summary>
public sealed class ProviderAuthorizationBrowserHost : IQqAuthorizationBrowser, INetEaseAuthorizationBrowser
{
    private const double MaximumContentWidth = 1120;
    private const double MinimumProviderViewportWidth = 980;
    private const double MaximumContentHeight = 620;

    private readonly Func<XamlRoot?> _xamlRoot;
    private readonly SemaphoreSlim _dialogGate = new(1, 1);

    public ProviderAuthorizationBrowserHost(Func<XamlRoot?> xamlRoot) =>
        _xamlRoot = xamlRoot ?? throw new ArgumentNullException(nameof(xamlRoot));

    public async Task<QqAuthorizationBrowserCapture> CaptureAsync(
        QqAuthorizationBrowserRequest request,
        CancellationToken cancellationToken)
    {
        var capture = await CaptureCoreAsync(
            "qq",
            "登录 QQ 音乐",
            "请仅在 QQ 音乐官方页面中完成登录，完成后选择“验证登录”。",
            request.LoginUri,
            request.AllowedHosts,
            request.AllowedCookieNames,
            cancellationToken);
        return new QqAuthorizationBrowserCapture(capture.CompletedUri, capture.Cookies, capture.IsCancelled);
    }

    public async Task<NetEaseAuthorizationBrowserCapture> CaptureAsync(
        NetEaseAuthorizationBrowserRequest request,
        CancellationToken cancellationToken)
    {
        var capture = await CaptureCoreAsync(
            "netease",
            "登录网易云音乐",
            "请仅在网易云音乐官方页面中完成登录，完成后选择“验证登录”。",
            request.LoginUri,
            request.AllowedHosts,
            request.AllowedCookieNames,
            cancellationToken);
        return new NetEaseAuthorizationBrowserCapture(capture.CompletedUri, capture.Cookies, capture.IsCancelled);
    }

    private async Task<BrowserCapture> CaptureCoreAsync(
        string platform,
        string title,
        string hint,
        Uri loginUri,
        IReadOnlyCollection<string> allowedHosts,
        IReadOnlyCollection<string> allowedCookieNames,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _dialogGate.WaitAsync(cancellationToken);
        WebView2? web = null;
        CoreWebView2Environment? environment = null;
        CoreWebView2? core = null;
        CancellationTokenSource? browserLifetime = null;
        TaskCompletionSource? initializationCompletion = null;
        RoutedEventHandler? loadedHandler = null;
        var initializationStarted = 0;
        try
        {
            var xamlRoot = _xamlRoot();
            if (xamlRoot is null)
                return BrowserCapture.Cancelled;

            web = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            var blockedNavigation = false;
            var status = new TextBlock
            {
                Text = hint,
                TextWrapping = TextWrapping.Wrap,
                Style = Application.Current.Resources["SecondaryTextStyle"] as Style
            };
            var rootElement = xamlRoot.Content as FrameworkElement;
            var availableWidth = rootElement?.ActualWidth > 0 ? rootElement.ActualWidth : 1000;
            var availableHeight = rootElement?.ActualHeight > 0 ? rootElement.ActualHeight : 760;
            var layout = CalculateLayout(availableWidth, availableHeight);
            var content = new Grid
            {
                Width = layout.ContentViewportWidth,
                Height = layout.ContentHeight,
                RowSpacing = 12
            };
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.Children.Add(status);
            var browserScroller = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Enabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollMode = ScrollMode.Disabled,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Content = web
            };
            web.Width = layout.BrowserContentWidth;
            Grid.SetRow(browserScroller, 1);
            content.Children.Add(browserScroller);

            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = title,
                Content = content,
                PrimaryButtonText = "验证登录",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false
            };
            // ContentDialog defaults to roughly 548 px, which clips provider desktop
            // login pages even when the app window has ample room.
            dialog.Resources["ContentDialogMaxWidth"] = layout.DialogMaxWidth;
            browserLifetime = new CancellationTokenSource();
            initializationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            loadedHandler = async (_, _) =>
            {
                if (Interlocked.CompareExchange(ref initializationStarted, 1, 0) != 0)
                    return;

                try
                {
                    environment = await CoreWebView2Environment.CreateAsync();
                    browserLifetime.Token.ThrowIfCancellationRequested();
                    var controllerOptions = environment.CreateCoreWebView2ControllerOptions();
                    controllerOptions.ProfileName = $"beans-{platform}";
                    controllerOptions.IsInPrivateModeEnabled = true;
                    await web.EnsureCoreWebView2Async(environment, controllerOptions);
                    core = web.CoreWebView2;
                    browserLifetime.Token.ThrowIfCancellationRequested();
                    if (core is null)
                    {
                        status.Text = "安全登录窗口初始化失败，请稍后重试。";
                        return;
                    }

                    core.Settings.AreDevToolsEnabled = false;
                    core.Settings.IsStatusBarEnabled = false;
                    core.Settings.IsPasswordAutosaveEnabled = false;
                    core.Settings.IsGeneralAutofillEnabled = false;
                    core.CookieManager.DeleteAllCookies();
                    core.NavigationStarting += (_, args) =>
                    {
                        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || !IsAllowedHttps(uri, allowedHosts))
                        {
                            args.Cancel = true;
                            blockedNavigation = true;
                            status.Text = "已阻止离开官方登录域名的页面。";
                        }
                    };
                    core.NewWindowRequested += (_, args) =>
                    {
                        args.Handled = true;
                        if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && IsAllowedHttps(uri, allowedHosts))
                            core.Navigate(uri.AbsoluteUri);
                        else
                        {
                            blockedNavigation = true;
                            status.Text = "已阻止非官方登录窗口。";
                        }
                    };
                    web.Source = loginUri;
                    dialog.IsPrimaryButtonEnabled = true;
                }
                catch (OperationCanceledException) when (browserLifetime.IsCancellationRequested)
                {
                    // The dialog was closed while WebView2 was still initializing.
                }
                catch
                {
                    status.Text = "安全登录窗口初始化失败，请检查 WebView2 Runtime 后重试。";
                }
                finally
                {
                    initializationCompletion.TrySetResult();
                }
            };
            web.Loaded += loadedHandler;
            using var registration = cancellationToken.Register(() =>
                _ = dialog.DispatcherQueue.TryEnqueue(dialog.Hide));

            var result = await dialog.ShowAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (result != ContentDialogResult.Primary || core is null)
                return BrowserCapture.Cancelled;
            if (blockedNavigation && !TryGetTrustedCurrentUri(core, allowedHosts, out _))
                return BrowserCapture.Cancelled;
            if (!TryGetTrustedCurrentUri(core, allowedHosts, out var completedUri))
                return BrowserCapture.Cancelled;

            var captured = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var host in allowedHosts.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var cookies = await core.CookieManager.GetCookiesAsync($"https://{host}/");
                foreach (var cookie in cookies)
                {
                    if (!allowedCookieNames.Contains(cookie.Name, StringComparer.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(cookie.Value))
                        continue;
                    captured[cookie.Name] = cookie.Value;
                }
            }

            return new BrowserCapture(completedUri, captured, false);
        }
        finally
        {
            browserLifetime?.Cancel();
            if (web is not null && loadedHandler is not null)
                web.Loaded -= loadedHandler;
            if (Volatile.Read(ref initializationStarted) != 0 && initializationCompletion is not null)
                await initializationCompletion.Task;
            try { core?.CookieManager.DeleteAllCookies(); }
            catch { }
            web?.Close();
            browserLifetime?.Dispose();
            _dialogGate.Release();
        }
    }

    private static bool TryGetTrustedCurrentUri(
        CoreWebView2 core,
        IReadOnlyCollection<string> allowedHosts,
        out Uri? uri)
    {
        if (Uri.TryCreate(core.Source, UriKind.Absolute, out var current) && IsAllowedHttps(current, allowedHosts))
        {
            uri = current;
            return true;
        }
        uri = null;
        return false;
    }

    internal static bool IsAllowedHttps(Uri uri, IReadOnlyCollection<string> allowedHosts) =>
        uri.IsAbsoluteUri &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        uri.Port is -1 or 443 &&
        allowedHosts.Contains(uri.DnsSafeHost, StringComparer.OrdinalIgnoreCase);

    internal static BrowserHostLayout CalculateLayout(double availableWidth, double availableHeight)
    {
        var safeWidth = double.IsFinite(availableWidth) && availableWidth > 0 ? availableWidth : 1000;
        var safeHeight = double.IsFinite(availableHeight) && availableHeight > 0 ? availableHeight : 760;
        var dialogMaxWidth = Math.Min(1200, Math.Max(320, safeWidth - 32));
        var contentViewportWidth = Math.Min(
            MaximumContentWidth,
            Math.Max(280, dialogMaxWidth - 64));
        var contentHeight = Math.Min(
            MaximumContentHeight,
            Math.Max(320, safeHeight - 140));

        return new BrowserHostLayout(
            contentViewportWidth,
            contentHeight,
            Math.Max(MinimumProviderViewportWidth, contentViewportWidth),
            dialogMaxWidth);
    }

    internal readonly record struct BrowserHostLayout(
        double ContentViewportWidth,
        double ContentHeight,
        double BrowserContentWidth,
        double DialogMaxWidth);

    private sealed record BrowserCapture(
        Uri? CompletedUri,
        IReadOnlyDictionary<string, string>? Cookies,
        bool IsCancelled)
    {
        public static BrowserCapture Cancelled { get; } = new(null, null, true);
    }
}
