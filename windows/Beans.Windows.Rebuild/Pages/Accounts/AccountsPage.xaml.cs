using System.ComponentModel;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Accounts;
using Beans.Windows.Rebuild.Services.BeansAccount;
using Beans.Windows.Rebuild.Services.PlatformLibrary;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Beans.Windows.Rebuild.Pages.Accounts;

public sealed partial class AccountsPage : UserControl, INotifyPropertyChanged
{
    private readonly IPlatformAuthService? _auth;
    private readonly IPlatformLibraryService? _platformLibrary;
    private readonly IBeansAccountService? _beansAccount;
    private CancellationTokenSource? _refreshCancellation;
    private string _statusText = "准备检查账号状态";
    private string _qqStatus = "正在读取登录状态…";
    private string _netEaseStatus = "正在读取登录状态…";
    private string _qqProbeDetail = "探针尚未运行";
    private string _netEaseProbeDetail = "探针尚未运行";
    private string _beansStatus = "尚未登录 Beans 账号";

    public AccountsPage() : this(null, null, null) { }

    public AccountsPage(
        IPlatformAuthService? auth,
        IPlatformLibraryService? platformLibrary = null,
        IBeansAccountService? beansAccount = null)
    {
        _auth = auth;
        _platformLibrary = platformLibrary;
        _beansAccount = beansAccount;
        InitializeComponent();
        Loaded += async (_, _) => await RefreshStatesAsync(false);
        Unloaded += (_, _) => _refreshCancellation?.Cancel();
    }

    public PlatformId QqPlatform => PlatformId.QqMusic;
    public PlatformId NetEasePlatform => PlatformId.NetEaseMusic;
    public PlatformId LocalPlatform => PlatformId.Local;
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value, nameof(StatusText)); }
    public string QqStatus { get => _qqStatus; private set => Set(ref _qqStatus, value, nameof(QqStatus)); }
    public string NetEaseStatus { get => _netEaseStatus; private set => Set(ref _netEaseStatus, value, nameof(NetEaseStatus)); }
    public string QqProbeDetail { get => _qqProbeDetail; private set => Set(ref _qqProbeDetail, value, nameof(QqProbeDetail)); }
    public string NetEaseProbeDetail { get => _netEaseProbeDetail; private set => Set(ref _netEaseProbeDetail, value, nameof(NetEaseProbeDetail)); }
    public string BeansStatus { get => _beansStatus; private set => Set(ref _beansStatus, value, nameof(BeansStatus)); }
    public event PropertyChangedEventHandler? PropertyChanged;

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        var platform = ParsePlatform((sender as Button)?.Tag?.ToString());
        if (_auth is null) { StatusText = "在线授权服务不可用"; return; }
        try
        {
            StatusText = $"正在打开{platform.ToDisplayName()}官方登录…";
            var result = await _auth.AuthorizeAsync(platform, CancellationToken.None);
            StatusText = result.SafeMessage;
            await RefreshStatesAsync(true);
        }
        catch (OperationCanceledException) { StatusText = "登录已取消"; }
        catch { StatusText = "登录暂时无法完成"; }
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        var platform = ParsePlatform((sender as Button)?.Tag?.ToString());
        if (_auth is null) return;
        try
        {
            await _auth.LogoutAsync(platform, CancellationToken.None);
            StatusText = $"已退出{platform.ToDisplayName()}";
            await RefreshStatesAsync(true);
        }
        catch { StatusText = $"退出{platform.ToDisplayName()}失败，请稍后重试"; }
    }

    private async void RefreshProbe_Click(object sender, RoutedEventArgs e) => await RefreshStatesAsync(true);

    private async Task RefreshStatesAsync(bool forceRefresh)
    {
        _refreshCancellation?.Cancel();
        _refreshCancellation = new CancellationTokenSource();
        var token = _refreshCancellation.Token;
        StatusText = "正在运行平台账号探针…";
        try
        {
            if (_auth is null)
            {
                QqStatus = NetEaseStatus = "授权服务不可用";
            }
            else
            {
                var states = await Task.WhenAll(
                    _auth.GetStateAsync(PlatformId.QqMusic, token),
                    _auth.GetStateAsync(PlatformId.NetEaseMusic, token));
                QqStatus = states[0].SafeMessage;
                NetEaseStatus = states[1].SafeMessage;
            }

            if (_platformLibrary is not null)
            {
                var snapshots = await _platformLibrary.LoadAllAsync(forceRefresh, token);
                ApplyProbe(snapshots.FirstOrDefault(item => item.Platform == PlatformId.QqMusic));
                ApplyProbe(snapshots.FirstOrDefault(item => item.Platform == PlatformId.NetEaseMusic));
            }

            if (_beansAccount is not null)
            {
                var beans = await _beansAccount.GetStateAsync(token);
                BeansStatus = FormatBeans(beans);
            }
            StatusText = $"探针检查完成 · {DateTime.Now:t}";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch
        {
            StatusText = "账号状态暂时无法刷新";
        }
    }

    private void ApplyProbe(PlatformLibrarySnapshot? snapshot)
    {
        if (snapshot is null) return;
        var line = $"{snapshot.Probe.DisplayName} · {snapshot.Probe.MembershipLabel} · 检查于 {snapshot.Probe.CheckedAt.LocalDateTime:t}";
        if (snapshot.Platform == PlatformId.QqMusic)
        {
            QqStatus = snapshot.Probe.SafeMessage;
            QqProbeDetail = line;
        }
        else
        {
            NetEaseStatus = snapshot.Probe.SafeMessage;
            NetEaseProbeDetail = line;
        }
    }

    private async void RegisterBeans_Click(object sender, RoutedEventArgs e)
    {
        if (_beansAccount is null) { BeansStatus = "Beans 账号服务未配置"; return; }
        if (!TryServer(out var server)) return;
        try
        {
            BeansStatus = "正在提交注册…";
            var result = await _beansAccount.StartRegistrationAsync(new BeansRegistrationRequest(
                server!, NicknameBox.Text.Trim(), EmailBox.Text.Trim(), PasswordBox.Password, Environment.MachineName));
            BeansStatus = result.DevelopmentVerificationCode is { Length: > 0 } code
                ? $"{result.SafeMessage} · 开发验证码 {code}"
                : result.SafeMessage;
        }
        catch (Exception exception) { BeansStatus = SafeBeansError(exception); }
    }

    private async void VerifyBeans_Click(object sender, RoutedEventArgs e)
    {
        if (_beansAccount is null) { BeansStatus = "Beans 账号服务未配置"; return; }
        try
        {
            BeansStatus = "正在验证邮箱…";
            var result = await _beansAccount.VerifyEmailAsync(VerificationBox.Text.Trim());
            BeansStatus = result.SafeMessage;
        }
        catch (Exception exception) { BeansStatus = SafeBeansError(exception); }
    }

    private async void LoginBeans_Click(object sender, RoutedEventArgs e)
    {
        if (_beansAccount is null) { BeansStatus = "Beans 账号服务未配置"; return; }
        if (!TryServer(out var server)) return;
        try
        {
            BeansStatus = "正在登录 Beans 账号…";
            var result = await _beansAccount.LoginAsync(new BeansLoginRequest(
                server!, EmailBox.Text.Trim(), PasswordBox.Password, Environment.MachineName));
            BeansStatus = result.SafeMessage;
        }
        catch (Exception exception) { BeansStatus = SafeBeansError(exception); }
    }

    private async void SyncBeans_Click(object sender, RoutedEventArgs e)
    {
        if (_beansAccount is null) { BeansStatus = "Beans 账号服务未配置"; return; }
        try
        {
            BeansStatus = "正在进行端到端加密同步…";
            var result = await _beansAccount.SyncAsync();
            BeansStatus = result.SafeMessage;
        }
        catch (Exception exception) { BeansStatus = SafeBeansError(exception); }
    }

    private async void LogoutBeans_Click(object sender, RoutedEventArgs e)
    {
        if (_beansAccount is null) return;
        try
        {
            await _beansAccount.LogoutAsync();
            BeansStatus = "已退出 Beans 账号；平台授权仍保留在本机";
        }
        catch (Exception exception) { BeansStatus = SafeBeansError(exception); }
    }

    private bool TryServer(out Uri? server)
    {
        server = null;
        if (!Uri.TryCreate(ServerBox.Text.Trim(), UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https"))
        {
            BeansStatus = "请输入有效的账号服务地址";
            return false;
        }
        server = parsed.AbsoluteUri.EndsWith('/') ? parsed : new Uri(parsed.AbsoluteUri + "/");
        return true;
    }

    private static string FormatBeans(BeansAccountSnapshot snapshot) => snapshot.State switch
    {
        BeansAccountState.SignedIn => $"{snapshot.DisplayName} · {snapshot.Email} · {snapshot.SafeMessage}",
        BeansAccountState.PendingVerification => snapshot.SafeMessage,
        _ => snapshot.SafeMessage
    };

    private static PlatformId ParsePlatform(string? value) =>
        value == "netease" ? PlatformId.NetEaseMusic : PlatformId.QqMusic;

    private static string SafeBeansError(Exception exception) =>
        exception switch
        {
            HttpRequestException => "无法连接 Beans 服务，请确认服务已启动并检查服务地址",
            TimeoutException => "Beans 服务响应超时，请稍后重试",
            _ => "Beans 操作未完成，请检查输入和服务状态后重试"
        };

    private void Set(ref string field, string value, string propertyName)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new(propertyName));
    }
}
