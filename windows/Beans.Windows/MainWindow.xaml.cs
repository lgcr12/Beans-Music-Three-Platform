using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Beans.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;
using Windows.Storage.Streams;
using Windows.UI.ViewManagement;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace Beans.Windows;

public sealed partial class MainWindow : Window
{
    private sealed record StoredSession(AccountRecord Account, TokenPair Tokens, byte[] VaultKey);
    private sealed record PlayerExperienceSettings(
        string EffectMode = "flow",
        double EffectIntensity = 0.7,
        string LyricStyle = "flow",
        double LyricFontSize = 24,
        double LyricLineSpacing = 8,
        bool LyricTranslation = true,
        string LyricAlignment = "center",
        string LyricColor = "#55E1B2",
        bool LyricGlow = true,
        bool LyricBlur = false,
        bool LyricItalic = false,
        string LyricBackground = "soft",
        double LyricOffsetSeconds = 0);
    private sealed record UpdateNotificationState(
        bool AutomaticEnabled = true,
        DateTimeOffset? LastCheckedAt = null,
        string? ETag = null,
        string? IgnoredVersion = null,
        string? LastPromptedVersion = null,
        bool RemindLater = false,
        ApplicationRelease? CachedRelease = null);
    private enum BannerKind { None, Credential, Update }

    private readonly BeansApiClient _api = new(new HttpClient { Timeout = TimeSpan.FromSeconds(25) });
    private readonly PlatformMusicClient _platformClient = new(new HttpClient { Timeout = TimeSpan.FromSeconds(25) });
    private readonly WindowsVaultStore _secureStore = new();
    private readonly SyncOutbox _syncStore = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeansMusic", "sync.sqlite"));
    private readonly ListeningInsightsStore _insights = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeansMusic", "listening.sqlite"));
    private readonly DeviceInput _device;
    private readonly MediaPlayer _mediaPlayer = new();
    private readonly LocalSpectrumAnalyzer _spectrumAnalyzer = new();
    private readonly ResumableDownloader _downloader = new(new HttpClient { Timeout = TimeSpan.FromMinutes(10) });
    private readonly ApplicationUpdateService _updateService = new(new HttpClient { Timeout = TimeSpan.FromSeconds(20) });
    private readonly CredentialProbeService _credentialProbe;
    private IReadOnlyList<LocalMusicTrack> _localTracks = [];
    private readonly List<LocalMusicTrack> _queue = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _lyricTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _visualTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _maintenanceTimer;
    private IReadOnlyList<LyricLine> _lyrics = [];
    private int _currentLyricIndex = -1;
    private PendingRegistration? _pendingRegistration;
    private byte[]? _pendingVaultKey;
    private StoredSession? _session;
    private PlatformCredentialBundle _platformCredentials = new(null, null, DateTimeOffset.UtcNow);
    private readonly HashSet<string> _validatedPlatforms = new(StringComparer.OrdinalIgnoreCase);
    private long _vaultVersion;
    private CancellationTokenSource? _qrPolling;
    private CancellationTokenSource? _downloadCancellation;
    private BeansSyncCoordinator? _sync;
    private bool _applyingRemoteTheme;
    private string _referenceStyle = "aurora";
    private string _currentPage = "home";
    private LocalMusicTrack? _currentLocalTrack;
    private IReadOnlyList<ListeningInsight> _recentInsights = [];
    private PlayerExperienceSettings _playerSettings = new();
    private UpdateNotificationState _updateState = new();
    private bool _automaticProbeEnabled = true;
    private bool _applyingExperienceSettings;
    private double[] _spectrumValues = new double[24];
    private readonly Dictionary<int, (TextBlock Overlay, RectangleGeometry Clip)> _karaokeLines = [];
    private CancellationTokenSource? _experienceSyncDebounce;
    private BannerKind _bannerKind;
    private ApplicationRelease? _pendingRelease;
    private bool _updateCheckInFlight;
    private readonly bool _animationsEnabled = new UISettings().AnimationsEnabled;
    private TypographyService Typography => (TypographyService)Application.Current.Resources["BeansTypography"];

    public MainWindow()
    {
        InitializeComponent();
        Player.SetMediaPlayer(_mediaPlayer);
        _mediaPlayer.CommandManager.IsEnabled = true;
        _lyricTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _lyricTimer.Interval = TimeSpan.FromMilliseconds(250);
        _lyricTimer.Tick += (_, _) => UpdateCurrentLyric();
        _lyricTimer.Start();
        _visualTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _visualTimer.Interval = TimeSpan.FromMilliseconds(33);
        _visualTimer.Tick += (_, _) => RenderPlayerEffect();
        _maintenanceTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _maintenanceTimer.Interval = TimeSpan.FromMinutes(30);
        _maintenanceTimer.Tick += async (_, _) => await RunAutomaticMaintenanceAsync();
        _maintenanceTimer.Start();
        Typography.PercentChanged += Typography_PercentChanged;
        _mediaPlayer.PlaybackSession.PlaybackStateChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            UpdateUniversePlaybackState();
            if (_mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) _spectrumAnalyzer.Resume();
            else _spectrumAnalyzer.Pause();
            UpdateVisualTimerState();
        });
        _spectrumAnalyzer.SpectrumAvailable += (_, values) => _spectrumValues = values;
        ApplyReferenceLayout("aurora");
        _ = RefreshMusicUniverseAsync();
        var deviceId = _secureStore.Load<Guid?>("device-id") ?? Guid.NewGuid();
        _secureStore.Save("device-id", deviceId);
        _device = new DeviceInput(deviceId, Environment.MachineName, "windows");
        _platformCredentials = _secureStore.Load<PlatformCredentialBundle>("platform-credentials")
            ?? new PlatformCredentialBundle(null, null, DateTimeOffset.UtcNow);
        _playerSettings = _secureStore.Load<PlayerExperienceSettings>("player-experience") ?? new();
        _updateState = _secureStore.Load<UpdateNotificationState>("update-notifications") ?? new();
        _automaticProbeEnabled = _secureStore.Load<bool?>("credential-probe-automatic") ?? true;
        var restoredProbeResults = _secureStore.Load<List<PlatformCredentialProbeResult>>("credential-probe-results") ?? [];
        _credentialProbe = new CredentialProbeService(
            _platformClient,
            provider => provider == "qq" ? _platformCredentials.Qq : _platformCredentials.Netease,
            restoredProbeResults);
        _credentialProbe.ResultChanged += CredentialProbe_ResultChanged;
        ApplyExperienceSettings();
        AutomaticProbeToggle.IsOn = _automaticProbeEnabled;
        AutomaticUpdateToggle.IsOn = _updateState.AutomaticEnabled;
        RenderCredentialProbeStatus();
        _api.TokensChanged += tokens =>
        {
            if (_session is null) return;
            _session = _session with { Tokens = tokens };
            _secureStore.Save("session", _session);
        };
        Root.SizeChanged += (_, _) => UpdateRightRailWidth();
        Activated += async (_, _) => await RunAutomaticMaintenanceAsync();
        Closed += async (_, _) =>
        {
            _visualTimer.Stop();
            _maintenanceTimer.Stop();
            await _spectrumAnalyzer.DisposeAsync();
        };
        _session = _secureStore.Load<StoredSession>("session");
        if (_session is not null)
        {
            _api.RestoreTokens(_session.Tokens);
            SetSignedIn(_session.Account);
            _ = RestoreSignedInSessionAsync();
        }
        _ = RunAutomaticMaintenanceAsync();
    }

    private void AccountButton_Click(object sender, RoutedEventArgs e)
    {
        RenderCredentialProbeStatus();
        AccountOverlay.Visibility = Visibility.Visible;
    }
    private void CloseAccount_Click(object sender, RoutedEventArgs e) => AccountOverlay.Visibility = Visibility.Collapsed;

    private void CredentialProbe_ResultChanged(object? sender, PlatformCredentialProbeResult result)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _secureStore.Save("credential-probe-results", _credentialProbe.Results.Values.ToList());
            RenderCredentialProbeStatus();
        });
    }

    private async Task<PlatformCredentialProbeResult> RunCredentialProbeAsync(string platform, CredentialProbeMode mode)
    {
        var previous = _credentialProbe.Results.GetValueOrDefault(platform);
        RenderCredentialChecking(platform);
        var result = await _credentialProbe.RunAsync(platform, mode);
        System.Diagnostics.Debug.WriteLine(
            $"CredentialProbe platform={platform} status={result.Status} quality={result.Quality ?? "none"} vkey={result.VkeyCode?.ToString() ?? "none"} http={result.HttpStatus?.ToString() ?? "none"}");
        if (result.Status == CredentialProbeStatus.Valid) _validatedPlatforms.Add(platform);
        else if (result.Status is CredentialProbeStatus.Invalid or CredentialProbeStatus.PlaybackLimited) _validatedPlatforms.Remove(platform);
        RenderCredentialProbeStatus();
        if (mode == CredentialProbeMode.Automatic && previous?.Status == CredentialProbeStatus.Valid &&
            result.Status is CredentialProbeStatus.Invalid or CredentialProbeStatus.PlaybackLimited)
        {
            ShowCredentialBanner(platform, result.Status);
        }
        return result;
    }

    private async Task RunAutomaticProbesAsync()
    {
        if (!_automaticProbeEnabled) return;
        foreach (var platform in new[] { "qq", "netease" })
        {
            var credentials = platform == "qq" ? _platformCredentials.Qq : _platformCredentials.Netease;
            if (credentials is null || !PlatformCredentialPolicy.LooksUsable(platform, credentials) || !_credentialProbe.IsDue(platform)) continue;
            await RunCredentialProbeAsync(platform, CredentialProbeMode.Automatic);
        }
    }

    private async void ProbeQq_Click(object sender, RoutedEventArgs e) => await RunCredentialProbeAsync("qq", CredentialProbeMode.Manual);
    private async void ProbeNetease_Click(object sender, RoutedEventArgs e) => await RunCredentialProbeAsync("netease", CredentialProbeMode.Manual);
    private async void ProbeAll_Click(object sender, RoutedEventArgs e)
    {
        await RunCredentialProbeAsync("qq", CredentialProbeMode.Manual);
        await RunCredentialProbeAsync("netease", CredentialProbeMode.Manual);
    }

    private void AutomaticProbeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _automaticProbeEnabled = AutomaticProbeToggle.IsOn;
        _secureStore.Save("credential-probe-automatic", _automaticProbeEnabled);
        if (_automaticProbeEnabled) _ = RunAutomaticProbesAsync();
    }

    private void RenderCredentialChecking(string platform)
    {
        var text = platform == "qq" ? QqProbeStatus : NeteaseProbeStatus;
        text.Text = "检测中…";
    }

    private void RenderCredentialProbeStatus()
    {
        if (QqProbeStatus is null || NeteaseProbeStatus is null) return;
        QqProbeStatus.Text = CredentialStatusText("qq", _platformCredentials.Qq);
        NeteaseProbeStatus.Text = CredentialStatusText("netease", _platformCredentials.Netease);
        CredentialAttentionDot.Visibility = _credentialProbe.Results.Values.Any(result => result.Status is CredentialProbeStatus.Invalid or CredentialProbeStatus.PlaybackLimited)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private string CredentialStatusText(string platform, IReadOnlyDictionary<string, string>? credentials)
    {
        if (credentials is null || !PlatformCredentialPolicy.LooksUsable(platform, credentials)) return "未授权";
        if (!_credentialProbe.Results.TryGetValue(platform, out var result)) return "未检测";
        var status = result.Status switch
        {
            CredentialProbeStatus.Valid => "有效",
            CredentialProbeStatus.PlaybackLimited => "播放权限受限",
            CredentialProbeStatus.Invalid => "已失效",
            CredentialProbeStatus.NetworkError => "网络异常",
            CredentialProbeStatus.Checking => "检测中",
            CredentialProbeStatus.NotAuthorized => "未授权",
            _ => "未检测"
        };
        var membership = string.IsNullOrWhiteSpace(result.Membership) ? string.Empty : $" · {result.Membership}";
        return $"{status}{membership}\n上次验证 {result.CheckedAt.LocalDateTime:g} · 下次检测 {result.NextCheckAt.LocalDateTime:g}";
    }

    private void ShowCredentialBanner(string platform, CredentialProbeStatus status)
    {
        _bannerKind = BannerKind.Credential;
        TopBannerTitle.Text = platform == "qq" ? "QQ 音乐授权需要处理" : "网易云音乐授权需要处理";
        TopBannerMessage.Text = status == CredentialProbeStatus.Invalid ? "凭证已失效，请重新授权。" : "登录有效，但会员播放权限未通过验证。";
        TopBannerPrimary.Content = "重新授权";
        TopBannerLater.Content = "关闭";
        TopBannerLater.Visibility = Visibility.Visible;
        TopBannerIgnore.Visibility = Visibility.Collapsed;
        TopBanner.Visibility = Visibility.Visible;
    }

    private async Task RunAutomaticMaintenanceAsync()
    {
        await RunAutomaticProbesAsync();
        await CheckForUpdatesAsync(false);
    }

    private void AutomaticUpdateToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _updateState = _updateState with { AutomaticEnabled = AutomaticUpdateToggle.IsOn };
        SaveUpdateState();
        if (_updateState.AutomaticEnabled) _ = CheckForUpdatesAsync(false);
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(true);

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (!manual && !_updateState.AutomaticEnabled) return;
        if (_updateCheckInFlight) return;
        var now = DateTimeOffset.UtcNow;
        if (!manual && _updateState.LastCheckedAt is { } last && now - last < TimeSpan.FromHours(24)) return;
        _updateCheckInFlight = true;
        if (manual) UpdateStatus.Text = "正在检查更新…";
        try
        {
            var response = await _updateService.CheckAsync(_updateState.ETag);
            var release = response.NotModified ? _updateState.CachedRelease : response.Release;
            _updateState = _updateState with { LastCheckedAt = now, ETag = response.ETag, CachedRelease = release ?? _updateState.CachedRelease };
            SaveUpdateState();
            if (release is null || !ApplicationUpdateService.IsNewer(release.Version, CurrentVersion))
            {
                if (manual) UpdateStatus.Text = $"当前已是最新版本 {CurrentVersion}";
                return;
            }
            UpdateStatus.Text = $"发现新版本 {release.Version} · {release.PublishedAt.LocalDateTime:d}";
            if (!manual && (_updateState.IgnoredVersion == release.Version ||
                (_updateState.LastPromptedVersion == release.Version && !_updateState.RemindLater))) return;
            _updateState = _updateState with { LastPromptedVersion = release.Version, RemindLater = false };
            SaveUpdateState();
            ShowUpdateBanner(release);
        }
        catch (Exception)
        {
            if (manual) UpdateStatus.Text = "检查失败，请确认网络后重试";
        }
        finally
        {
            _updateCheckInFlight = false;
        }
    }

    private void ShowUpdateBanner(ApplicationRelease release)
    {
        _bannerKind = BannerKind.Update;
        _pendingRelease = release;
        TopBannerTitle.Text = $"Beans Music {release.Version} 可更新";
        TopBannerMessage.Text = string.IsNullOrWhiteSpace(release.Notes) ? $"发布于 {release.PublishedAt.LocalDateTime:g}" : release.Notes.Split('\n').FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? "查看本次更新内容";
        TopBannerPrimary.Content = "查看更新";
        TopBannerLater.Content = "稍后提醒";
        TopBannerLater.Visibility = Visibility.Visible;
        TopBannerIgnore.Content = "忽略此版本";
        TopBannerIgnore.Visibility = Visibility.Visible;
        TopBanner.Visibility = Visibility.Visible;
    }

    private async void TopBannerPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (_bannerKind == BannerKind.Credential)
        {
            TopBanner.Visibility = Visibility.Collapsed;
            AccountOverlay.Visibility = Visibility.Visible;
            return;
        }
        if (_pendingRelease is null) return;
        var asset = ApplicationUpdateService.SelectWindowsAsset(_pendingRelease, RuntimeInformation.ProcessArchitecture);
        await Launcher.LaunchUriAsync(asset?.DownloadUrl ?? _pendingRelease.PageUrl);
        TopBanner.Visibility = Visibility.Collapsed;
    }

    private void TopBannerLater_Click(object sender, RoutedEventArgs e)
    {
        if (_bannerKind == BannerKind.Update)
        {
            _updateState = _updateState with { RemindLater = true };
            SaveUpdateState();
        }
        TopBanner.Visibility = Visibility.Collapsed;
    }

    private void TopBannerIgnore_Click(object sender, RoutedEventArgs e)
    {
        if (_bannerKind == BannerKind.Update && _pendingRelease is not null)
        {
            _updateState = _updateState with { IgnoredVersion = _pendingRelease.Version, RemindLater = false };
            SaveUpdateState();
        }
        TopBanner.Visibility = Visibility.Collapsed;
    }

    private void SaveUpdateState() => _secureStore.Save("update-notifications", _updateState);

    private static string CurrentVersion
    {
        get
        {
            try
            {
                var version = Package.Current.Id.Version;
                return $"{version.Major}.{version.Minor}.{version.Build}";
            }
            catch
            {
                return typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
            }
        }
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItemContainer as NavigationViewItem)?.Tag?.ToString() ?? "settings";
        _currentPage = tag;
        var values = tag switch
        {
            "search" => ("搜索", "同时搜索 QQ 音乐、网易云音乐与本地音乐"),
            "library" => ("音乐库", "自建歌单可编辑，平台歌单为只读镜像"),
            "downloads" => ("下载", "断点续传和离线播放仅保存在本机"),
            "local" => ("本地音乐", "扫描并管理 Windows 音乐文件夹"),
            "queue" => ("播放列表", "管理队列、歌词与播放顺序"),
            "account" => ("账号中心", "管理 Beans 账号、平台授权、设备与同步"),
            "settings" => ("设置", "调整主题、音质、缓存与无障碍选项"),
            _ => ("晚上好", "从三个平台继续你的音乐旅程")
        };
        PageTitle.Text = values.Item1;
        PageSubtitle.Text = values.Item2;
        var profile = tag == "account";
        var settings = tag == "settings";
        MainContentPanel.Visibility = profile || settings ? Visibility.Collapsed : Visibility.Visible;
        MusicUniversePanel.Visibility = profile ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        SearchPanel.Visibility = !profile && !settings && (tag is "search" or "local") ? Visibility.Visible : Visibility.Collapsed;
        DownloadPanel.Visibility = !profile && !settings && tag == "downloads" ? Visibility.Visible : Visibility.Collapsed;
        if (profile) _ = RefreshMusicUniverseAsync();
        if (tag == "local") _ = LoadLocalLibraryAsync();
        if (tag == "search") _ = LoadLocalLibraryAsync(false);
        if (tag == "library") _ = LoadMirroredLibraryAsync();
        if (tag == "queue") RenderQueue();
        if (tag == "downloads") RenderDownloadPanel();
        UpdateRightRailWidth();
    }

    private async Task LoadLocalLibraryAsync(bool showStatus = true)
    {
        try
        {
            if (showStatus)
            {
                ContentList.Items.Clear();
                ContentList.Items.Add(new ListViewItem { Content = "正在扫描音乐文件夹…", IsEnabled = false });
            }
            var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            _localTracks = await LocalMusicScanner.ScanAsync([music]);
            RenderTracks(_localTracks);
            if (_localTracks.Count == 0) ContentList.Items.Add(new ListViewItem { Content = "音乐文件夹中没有支持的音频", IsEnabled = false });
        }
        catch (Exception ex) { ContentList.Items.Clear(); ContentList.Items.Add($"扫描失败：{ex.Message}"); }
    }

    private void RenderTracks(IEnumerable<LocalMusicTrack> tracks)
    {
            ContentList.Items.Clear();
            foreach (var track in tracks)
                ContentList.Items.Add(new ListViewItem { Tag = track, Content = $"{track.Title}  ·  {track.Extension.TrimStart('.').ToUpperInvariant()}" });
            if (!ContentList.Items.Any()) ContentList.Items.Add(new ListViewItem { Content = "没有匹配的本地音乐", IsEnabled = false });
    }

    private async Task LoadMirroredLibraryAsync()
    {
        ContentList.Items.Clear();
        try
        {
            if (_session is null) throw new InvalidOperationException("请先登录 Beans 账号");
            var mirrors = await _syncStore.ReadMirrorAsync(_session.Account.Id, "platformMirror");
            var count = 0;
            foreach (var mirror in mirrors.Where(x => !x.Deleted))
            {
                var plaintext = AccountCrypto.Decrypt(Convert.FromBase64String(mirror.Ciphertext), _session.VaultKey);
                try
                {
                    var payload = JsonSerializer.Deserialize<PlatformMirrorPayload>(plaintext, JsonOptions.Default);
                    if (payload is null) continue;
                    foreach (var playlist in payload.Playlists)
                    {
                        ContentList.Items.Add(new ListViewItem { Content = $"{playlist.Name}  ·  {payload.Platform}  ·  {playlist.TrackCount} 首" });
                        count++;
                    }
                }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
            }
            if (count == 0) ContentList.Items.Add(new ListViewItem { Content = "登录并授权后，平台歌单会显示在这里", IsEnabled = false });
        }
        catch (Exception ex) { ContentList.Items.Add(new ListViewItem { Content = $"歌单镜像暂不可用：{ex.Message}", IsEnabled = false }); }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        RenderTracks(string.IsNullOrEmpty(query) ? _localTracks : _localTracks.Where(x => x.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)));
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e) => SearchBox.Text = string.Empty;

    private void RenderQueue()
    {
        ContentList.Items.Clear();
        foreach (var track in _queue) ContentList.Items.Add(new ListViewItem { Tag = track, Content = $"{_queue.IndexOf(track) + 1}. {track.Title}" });
        if (_queue.Count == 0) ContentList.Items.Add(new ListViewItem { Content = "队列为空，双击本地音乐即可播放", IsEnabled = false });
        QueueStatus.Text = _queue.Count == 0 ? "队列为空" : $"队列中有 {_queue.Count} 首本地音乐";
    }

    private void RenderDownloadPanel()
    {
        ContentList.Items.Clear();
        ContentList.Items.Add(new ListViewItem { Content = "下载任务保存在本机，不会同步到其他设备。", IsEnabled = false });
        DownloadStatus.Text = "暂无下载任务";
        DownloadProgress.Value = 0;
    }

    private async void StartDownload_Click(object sender, RoutedEventArgs e)
    {
        CancellationTokenSource? downloadCancellation = null;
        try
        {
            if (!Uri.TryCreate(DownloadUrlBox.Text.Trim(), UriKind.Absolute, out var source) || source.Scheme is not ("http" or "https"))
                throw new InvalidOperationException("请输入合法的 HTTP 音频地址");
            var fileName = Path.GetFileName(DownloadNameBox.Text.Trim());
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "Beans Music 下载.mp3";
            foreach (var invalid in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(invalid, '_');
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Beans Music");
            var destination = Path.Combine(directory, fileName);
            _downloadCancellation?.Cancel();
            downloadCancellation = new CancellationTokenSource();
            _downloadCancellation = downloadCancellation;
            DownloadProgress.Value = 0;
            DownloadStatus.Text = "正在下载…";
            var progress = new Progress<double>(value => DownloadProgress.Value = value);
            await _downloader.DownloadAsync(source, destination, progress, downloadCancellation.Token);
            DownloadStatus.Text = $"下载完成：{fileName}";
            DownloadUrlBox.Text = string.Empty;
        }
        catch (OperationCanceledException) { DownloadStatus.Text = "下载已取消"; }
        catch (Exception ex) { DownloadStatus.Text = $"下载失败：{ex.Message}"; }
        finally
        {
            if (ReferenceEquals(_downloadCancellation, downloadCancellation)) _downloadCancellation = null;
            downloadCancellation?.Dispose();
        }
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e)
    {
        _downloadCancellation?.Cancel();
        DownloadStatus.Text = "正在取消下载…";
    }

    private void ShowLyrics_Click(object sender, RoutedEventArgs e)
    {
        LyricsList.Focus(FocusState.Programmatic);
        PageTitle.Text = "歌词";
        PageSubtitle.Text = "当前音频的本地 .lrc 歌词";
    }

    private void ShowQueue_Click(object sender, RoutedEventArgs e)
    {
        RenderQueue();
        PageTitle.Text = "播放列表";
        PageSubtitle.Text = "管理当前播放队列";
    }

    private async void ContentList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (ContentList.SelectedItem is not ListViewItem { Tag: LocalMusicTrack track }) return;
        await PlayLocalTrackAsync(track);
    }

    private async Task PlayLocalTrackAsync(LocalMusicTrack track)
    {
        if (!_queue.Any(x => x.Id == track.Id)) _queue.Add(track);
        _currentLocalTrack = track;
        QueueStatus.Text = $"正在播放：{track.Title} · 队列 {_queue.Count} 首";
        PlayerTitle.Text = track.Title;
        PlayerArtist.Text = "本地音乐";
        UniverseCurrentTitle.Text = track.Title;
        UniverseCurrentArtist.Text = $"本地音乐 · {track.Extension.TrimStart('.').ToUpperInvariant()}";
        _mediaPlayer.Source = MediaSource.CreateFromUri(new Uri(Path.GetFullPath(track.Path)));
        var updater = _mediaPlayer.SystemMediaTransportControls.DisplayUpdater;
        updater.Type = global::Windows.Media.MediaPlaybackType.Music;
        updater.MusicProperties.Title = track.Title;
        updater.MusicProperties.Artist = "本地音乐";
        updater.Update();
        _mediaPlayer.Play();
        await _spectrumAnalyzer.StartAsync(Path.GetFullPath(track.Path));
        _ = LoadLyricsAsync(track);
        await _insights.RecordPlayAsync(track);
        await RefreshMusicUniverseAsync();
    }

    private async void UniverseRecentList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (UniverseRecentList.SelectedItem is ListViewItem { Tag: LocalMusicTrack track }) await PlayLocalTrackAsync(track);
    }

    private void ToggleUniversePlayback_Click(object sender, RoutedEventArgs e)
    {
        if (_currentLocalTrack is null) { OpenLocalFromProfile_Click(sender, e); return; }
        if (_mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            _mediaPlayer.Pause();
            _spectrumAnalyzer.Pause();
        }
        else
        {
            _mediaPlayer.Play();
            _spectrumAnalyzer.Resume();
        }
    }

    private async void FavoriteCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_currentLocalTrack is null) return;
        var favorite = await _insights.ToggleFavoriteAsync(_currentLocalTrack.Id);
        FavoriteCurrentButton.Content = favorite ? "已收藏" : "收藏当前";
        await RefreshMusicUniverseAsync();
    }

    private void OpenAccountFromProfile_Click(object sender, RoutedEventArgs e) => AccountOverlay.Visibility = Visibility.Visible;

    private void OpenLocalFromProfile_Click(object sender, RoutedEventArgs e)
    {
        var item = Navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(x => x.Tag?.ToString() == "local");
        if (item is not null) Navigation.SelectedItem = item;
    }

    private async Task RefreshMusicUniverseAsync()
    {
        try
        {
            _recentInsights = await _insights.SnapshotAsync();
            UniverseRecentList.Items.Clear();
            foreach (var insight in _recentInsights)
            {
                UniverseRecentList.Items.Add(new ListViewItem
                {
                    Tag = insight.Track,
                    Content = $"{insight.Track.Title}  ·  播放 {insight.PlayCount} 次{(insight.IsFavorite ? "  ·  已收藏" : string.Empty)}"
                });
            }
            if (_recentInsights.Count == 0)
                UniverseRecentList.Items.Add(new ListViewItem { Content = "还没有播放记录，去听一首歌吧", IsEnabled = false });
            UniversePlayCount.Text = _recentInsights.Sum(x => x.PlayCount).ToString();
            UniverseFavoriteCount.Text = _recentInsights.Count(x => x.IsFavorite).ToString();
            UniverseTopTrack.Text = _recentInsights.OrderByDescending(x => x.PlayCount).FirstOrDefault()?.Track.Title ?? "暂无";
            if (_currentLocalTrack is not null)
            {
                var current = _recentInsights.FirstOrDefault(x => x.Track.Id == _currentLocalTrack.Id);
                FavoriteCurrentButton.Content = current?.IsFavorite == true ? "已收藏" : "收藏当前";
            }
            UpdateUniversePlaybackState();
        }
        catch (Exception ex)
        {
            UniverseRecentList.Items.Clear();
            UniverseRecentList.Items.Add(new ListViewItem { Content = $"听歌记录暂不可用：{ex.Message}", IsEnabled = false });
        }
    }

    private void UpdateUniversePlaybackState()
    {
        UniversePlayingState.Text = _currentLocalTrack is null
            ? "等待第一首歌"
            : _mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing ? "正在播放" : "已暂停";
    }

    private async Task LoadLyricsAsync(LocalMusicTrack track)
    {
        LyricsList.Items.Clear();
        _karaokeLines.Clear();
        _lyrics = [];
        _currentLyricIndex = -1;
        var path = Path.ChangeExtension(track.Path, ".lrc");
        if (!File.Exists(path))
        {
            LyricsStatus.Text = "未找到同名 .lrc 歌词文件";
            return;
        }
        try
        {
            _lyrics = LrcParser.Parse(await File.ReadAllTextAsync(path));
            for (var index = 0; index < _lyrics.Count; index++)
                LyricsList.Items.Add(CreateLyricItem(_lyrics[index], index));
            LyricsStatus.Text = _lyrics.Count == 0 ? "歌词文件为空" : $"已加载 {_lyrics.Count} 行歌词";
        }
        catch (Exception ex) { LyricsStatus.Text = $"歌词读取失败：{ex.Message}"; }
    }

    private void UpdateCurrentLyric()
    {
        if (_lyrics.Count == 0) return;
        var position = _mediaPlayer.PlaybackSession.Position + TimeSpan.FromSeconds(_playerSettings.LyricOffsetSeconds);
        var low = 0;
        var high = _lyrics.Count - 1;
        var result = -1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (_lyrics[middle].Time <= position) { result = middle; low = middle + 1; }
            else high = middle - 1;
        }
        if (result < 0) return;
        if (result != _currentLyricIndex)
        {
            _currentLyricIndex = result;
            LyricsList.SelectedIndex = result;
            LyricsList.ScrollIntoView(LyricsList.Items[result], ScrollIntoViewAlignment.Leading);
            LyricsStatus.Text = _lyrics[result].Text;
        }
        ApplyLyricHighlight(result, position);
    }

    private ListViewItem CreateLyricItem(LyricLine line, int index)
    {
        var translation = _playerSettings.LyricTranslation && !string.IsNullOrWhiteSpace(line.Translation)
            ? $"\n{line.Translation}"
            : string.Empty;
        var text = $"{line.Time:mm\\:ss}  {line.Text}{translation}";
        var alignment = _playerSettings.LyricAlignment == "center" ? TextAlignment.Center : TextAlignment.Left;
        var item = new ListViewItem
        {
            Tag = line,
            Padding = new Thickness(4, _playerSettings.LyricLineSpacing / 2, 4, _playerSettings.LyricLineSpacing / 2),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        if (_playerSettings.LyricStyle != "karaoke")
        {
            item.Content = new TextBlock
            {
                Text = text,
                FontSize = _playerSettings.LyricFontSize,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = alignment,
                FontStyle = _playerSettings.LyricItalic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
                Foreground = BrushResource("BeansMutedBrush")
            };
            return item;
        }

        var grid = new Grid();
        var baseLine = new TextBlock
        {
            Text = text,
            FontSize = _playerSettings.LyricFontSize,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = alignment,
            FontStyle = _playerSettings.LyricItalic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            Foreground = BrushResource("BeansMutedBrush")
        };
        var clip = new RectangleGeometry { Rect = new Rect(0, 0, 0, 0) };
        var overlay = new TextBlock
        {
            Text = text,
            FontSize = _playerSettings.LyricFontSize,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = alignment,
            FontStyle = _playerSettings.LyricItalic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            Foreground = LyricHighlightBrush(),
            Clip = clip
        };
        grid.Children.Add(baseLine);
        grid.Children.Add(overlay);
        item.Content = grid;
        _karaokeLines[index] = (overlay, clip);
        return item;
    }

    private void ApplyLyricHighlight(int current, TimeSpan position)
    {
        for (var index = 0; index < LyricsList.Items.Count; index++)
        {
            if (LyricsList.Items[index] is not ListViewItem item) continue;
            item.Background = _playerSettings.LyricStyle == "contrast" && index == current
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(220, 0, 0, 0))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            item.Opacity = index == current ? 1 : 0.62;
            if (_playerSettings.LyricBlur && index != current) item.Opacity = 0.38;
            if (item.Content is TextBlock text)
            {
                text.Foreground = index == current
                    ? (_playerSettings.LyricStyle == "contrast" ? new SolidColorBrush(Windows.UI.Colors.White) : LyricHighlightBrush())
                    : BrushResource("BeansMutedBrush");
            }
        }

        if (_playerSettings.LyricStyle != "karaoke" || !_karaokeLines.TryGetValue(current, out var karaoke)) return;
        var nextTime = current + 1 < _lyrics.Count ? _lyrics[current + 1].Time : _lyrics[current].Time.Add(TimeSpan.FromSeconds(4));
        var duration = Math.Max(0.1, (nextTime - _lyrics[current].Time).TotalSeconds);
        var progress = Math.Clamp((position - _lyrics[current].Time).TotalSeconds / duration, 0, 1);
        var width = Math.Max(karaoke.Overlay.ActualWidth, LyricsList.ActualWidth - 20);
        karaoke.Clip.Rect = new Rect(0, 0, width * progress, Math.Max(1, karaoke.Overlay.ActualHeight));
    }

    private Brush BrushResource(string key) => (Brush)Application.Current.Resources[key];

    private Brush LyricHighlightBrush()
    {
        var value = _playerSettings.LyricColor.TrimStart('#');
        if (uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        return BrushResource("BeansPrimaryBrush");
    }

    private async void ThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Root is null || ThemePicker.SelectedItem is not ComboBoxItem item) return;
        var key = item.Tag?.ToString() ?? "aurora";
        ThemeService.Apply(key, Root);
        ApplyReferenceLayout(key);
        if (!_applyingRemoteTheme && _sync is not null)
        {
            await RunAccountAction(async () =>
            {
                await _sync.EnqueueAsync("theme", "current", ThemePayloadFor(key));
                await _sync.SyncAsync();
                AccountStatus.Text = "主题已加密同步";
            });
        }
    }

    private void ApplyReferenceLayout(string key)
    {
        if (AuroraHome is null || PaperHome is null || MidnightHome is null) return;
        _referenceStyle = key;
        AuroraHome.Visibility = key == "aurora" ? Visibility.Visible : Visibility.Collapsed;
        PaperHome.Visibility = key == "paper" ? Visibility.Visible : Visibility.Collapsed;
        MidnightHome.Visibility = key == "midnight" ? Visibility.Visible : Visibility.Collapsed;
        UniverseHero.Background = ThemeService.MusicUniverseBackground(key);

        Navigation.OpenPaneLength = key == "paper" ? 236 : 220;
        UpdateRightRailWidth();
        PageEyebrow.Text = key switch
        {
            "paper" => "BEANS MUSIC · 清透纸感",
            "midnight" => "BEANS MUSIC · 午夜霓虹",
            _ => "BEANS MUSIC · 青碧玻璃"
        };
    }

    private void UpdateRightRailWidth()
    {
        RightRailColumn.Width = Root.ActualWidth < 1000 || _currentPage is "account" or "settings"
            ? new GridLength(0)
            : new GridLength(_referenceStyle == "midnight" ? 336 : 320);
    }

    private async void Typography_PercentChanged(object? sender, EventArgs e)
    {
        if (_applyingRemoteTheme || _sync is null) return;
        await RunAccountAction(async () =>
        {
            await _sync.EnqueueAsync("theme", "current", ThemePayloadFor(_referenceStyle));
            await _sync.SyncAsync();
            AccountStatus.Text = "字号设置已加密同步";
        });
    }

    private void FontScaleSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (Typography is not null) Typography.Percent = e.NewValue;
    }

    private void ResetFontScale_Click(object sender, RoutedEventArgs e) => Typography.Reset();

    private void ApplyExperienceSettings()
    {
        _applyingExperienceSettings = true;
        PlayerEffectPicker.SelectedItem = PlayerEffectPicker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == _playerSettings.EffectMode) ?? PlayerEffectPicker.Items[1];
        PlayerEffectIntensitySlider.Value = Math.Clamp(_playerSettings.EffectIntensity, 0.25, 1);
        var customStyle = LyricStylePicker.Items.OfType<ComboBoxItem>().First(item => item.Tag?.ToString() == "custom");
        customStyle.Visibility = _playerSettings.LyricStyle == "custom" ? Visibility.Visible : Visibility.Collapsed;
        LyricStylePicker.SelectedItem = LyricStylePicker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == _playerSettings.LyricStyle) ?? LyricStylePicker.Items[1];
        LyricFontSizeSlider.Value = Math.Clamp(_playerSettings.LyricFontSize, 12, 40);
        LyricLineSpacingSlider.Value = Math.Clamp(_playerSettings.LyricLineSpacing, 0, 20);
        LyricTranslationToggle.IsOn = _playerSettings.LyricTranslation;
        LyricAlignmentPicker.SelectedItem = LyricAlignmentPicker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == _playerSettings.LyricAlignment) ?? LyricAlignmentPicker.Items[1];
        LyricColorPicker.Color = ((SolidColorBrush)LyricHighlightBrush()).Color;
        LyricGlowToggle.IsOn = _playerSettings.LyricGlow;
        LyricBlurToggle.IsOn = _playerSettings.LyricBlur;
        LyricItalicToggle.IsOn = _playerSettings.LyricItalic;
        LyricBackgroundPicker.SelectedItem = LyricBackgroundPicker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == _playerSettings.LyricBackground) ?? LyricBackgroundPicker.Items[1];
        LyricOffsetBox.Value = Math.Clamp(_playerSettings.LyricOffsetSeconds, -5, 5);
        PlayerEffectIntensityText.Text = $"{PlayerEffectIntensitySlider.Value:P0}";
        LyricFontSizeText.Text = $"{LyricFontSizeSlider.Value:0} pt";
        _applyingExperienceSettings = false;
        ApplyLyricBackground();
        RenderPlayerEffect();
    }

    private void PlayerEffectPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingExperienceSettings || PlayerEffectPicker.SelectedItem is not ComboBoxItem item) return;
        _playerSettings = _playerSettings with { EffectMode = item.Tag?.ToString() ?? "flow" };
        SaveExperienceSettings();
        UpdateVisualTimerState();
    }

    private void PlayerEffectIntensity_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (PlayerEffectIntensityText is not null) PlayerEffectIntensityText.Text = $"{e.NewValue:P0}";
        if (_applyingExperienceSettings) return;
        _playerSettings = _playerSettings with { EffectIntensity = Math.Clamp(e.NewValue, 0.25, 1) };
        SaveExperienceSettings();
    }

    private void LyricStylePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingExperienceSettings || LyricStylePicker.SelectedItem is not ComboBoxItem item) return;
        var style = item.Tag?.ToString() ?? "flow";
        if (style == "custom") return;
        _playerSettings = style switch
        {
            "minimal" => _playerSettings with { LyricStyle = style, LyricLineSpacing = 4, LyricGlow = false, LyricBlur = false, LyricItalic = false, LyricBackground = "clear" },
            "karaoke" => _playerSettings with { LyricStyle = style, LyricLineSpacing = 10, LyricGlow = true, LyricBlur = false, LyricItalic = false, LyricBackground = "soft" },
            "contrast" => _playerSettings with { LyricStyle = style, LyricLineSpacing = 12, LyricGlow = false, LyricBlur = false, LyricItalic = false, LyricBackground = "contrast" },
            _ => _playerSettings with { LyricStyle = "flow", LyricLineSpacing = 8, LyricGlow = true, LyricBlur = false, LyricItalic = false, LyricBackground = "soft" }
        };
        _applyingExperienceSettings = true;
        LyricLineSpacingSlider.Value = _playerSettings.LyricLineSpacing;
        LyricGlowToggle.IsOn = _playerSettings.LyricGlow;
        LyricBlurToggle.IsOn = _playerSettings.LyricBlur;
        LyricItalicToggle.IsOn = _playerSettings.LyricItalic;
        LyricBackgroundPicker.SelectedItem = LyricBackgroundPicker.Items.OfType<ComboBoxItem>().FirstOrDefault(value => value.Tag?.ToString() == _playerSettings.LyricBackground);
        _applyingExperienceSettings = false;
        ApplyLyricBackground();
        SaveExperienceSettings();
        RebuildLyrics();
    }

    private void LyricSetting_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (LyricFontSizeText is not null) LyricFontSizeText.Text = $"{LyricFontSizeSlider.Value:0} pt";
        if (_applyingExperienceSettings || LyricFontSizeSlider is null || LyricLineSpacingSlider is null) return;
        _playerSettings = _playerSettings with
        {
            LyricFontSize = Math.Clamp(LyricFontSizeSlider.Value, 12, 40),
            LyricLineSpacing = Math.Clamp(LyricLineSpacingSlider.Value, 0, 20)
        };
        SaveExperienceSettings();
        RebuildLyrics();
    }

    private void LyricSetting_Toggled(object sender, RoutedEventArgs e)
    {
        if (_applyingExperienceSettings) return;
        _playerSettings = _playerSettings with { LyricTranslation = LyricTranslationToggle.IsOn };
        SaveExperienceSettings();
        RebuildLyrics();
    }

    private void LyricSetting_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingExperienceSettings || LyricAlignmentPicker.SelectedItem is not ComboBoxItem item) return;
        _playerSettings = _playerSettings with { LyricAlignment = item.Tag?.ToString() ?? "center" };
        SaveExperienceSettings();
        RebuildLyrics();
    }

    private void LyricAdvancedColor_Changed(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_applyingExperienceSettings) return;
        _playerSettings = _playerSettings with { LyricColor = $"#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}" };
        MarkLyricsCustomAndSave();
    }

    private void LyricAdvanced_Toggled(object sender, RoutedEventArgs e)
    {
        if (_applyingExperienceSettings) return;
        _playerSettings = _playerSettings with { LyricGlow = LyricGlowToggle.IsOn, LyricBlur = LyricBlurToggle.IsOn, LyricItalic = LyricItalicToggle.IsOn };
        MarkLyricsCustomAndSave();
    }

    private void LyricAdvanced_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingExperienceSettings || LyricBackgroundPicker.SelectedItem is not ComboBoxItem item) return;
        _playerSettings = _playerSettings with { LyricBackground = item.Tag?.ToString() ?? "soft" };
        MarkLyricsCustomAndSave();
    }

    private void LyricAdvancedOffset_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_applyingExperienceSettings || double.IsNaN(args.NewValue)) return;
        _playerSettings = _playerSettings with { LyricOffsetSeconds = Math.Clamp(args.NewValue, -5, 5) };
        MarkLyricsCustomAndSave();
    }

    private void MarkLyricsCustomAndSave()
    {
        _playerSettings = _playerSettings with { LyricStyle = "custom" };
        _applyingExperienceSettings = true;
        var custom = LyricStylePicker.Items.OfType<ComboBoxItem>().First(item => item.Tag?.ToString() == "custom");
        custom.Visibility = Visibility.Visible;
        LyricStylePicker.SelectedItem = custom;
        _applyingExperienceSettings = false;
        ApplyLyricBackground();
        SaveExperienceSettings();
        RebuildLyrics();
    }

    private void ApplyLyricBackground()
    {
        LyricsList.Background = _playerSettings.LyricBackground switch
        {
            "contrast" => new SolidColorBrush(Windows.UI.Color.FromArgb(205, 0, 0, 0)),
            "soft" => new SolidColorBrush(Windows.UI.Color.FromArgb(70, 0, 0, 0)),
            _ => new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0))
        };
    }

    private void RebuildLyrics()
    {
        if (_lyrics.Count == 0) return;
        LyricsList.Items.Clear();
        _karaokeLines.Clear();
        for (var index = 0; index < _lyrics.Count; index++) LyricsList.Items.Add(CreateLyricItem(_lyrics[index], index));
        _currentLyricIndex = -1;
        UpdateCurrentLyric();
    }

    private void SaveExperienceSettings()
    {
        _secureStore.Save("player-experience", _playerSettings);
        QueueExperienceSync();
    }

    private void QueueExperienceSync()
    {
        _experienceSyncDebounce?.Cancel();
        var cancellation = new CancellationTokenSource();
        _experienceSyncDebounce = cancellation;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(700, cancellation.Token);
                if (_sync is null) return;
                await _sync.EnqueueAsync("preference", "platforms", PlayerPreferencePayload(), ct: cancellation.Token);
                await _sync.SyncAsync(cancellation.Token);
            }
            catch (OperationCanceledException) { }
        });
    }

    private PlatformPreferencePayload PlayerPreferencePayload() => new(
        ["网易云", "QQ音乐"],
        _playerSettings.EffectMode,
        _playerSettings.EffectIntensity,
        _playerSettings.LyricStyle,
        _playerSettings.LyricFontSize,
        _playerSettings.LyricLineSpacing,
        _playerSettings.LyricTranslation,
        _playerSettings.LyricAlignment);

    private void UpdateVisualTimerState()
    {
        var playing = _mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
        if (playing && _animationsEnabled && _playerSettings.EffectMode != "quiet")
        {
            _visualTimer.Interval = TimeSpan.FromMilliseconds(Windows.System.Power.PowerManager.EnergySaverStatus == Windows.System.Power.EnergySaverStatus.On ? 50 : 33);
            _visualTimer.Start();
        }
        else
        {
            _visualTimer.Stop();
            RenderPlayerEffect();
        }
    }

    private void RenderPlayerEffect()
    {
        if (PlayerEffectCanvas is null) return;
        PlayerEffectCanvas.Children.Clear();
        var mode = _playerSettings.EffectMode;
        if (mode == "quiet") return;
        var width = Math.Max(320, PlayerEffectCanvas.ActualWidth);
        var height = Math.Max(60, PlayerEffectCanvas.ActualHeight);
        var playing = _mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
        var time = playing && _animationsEnabled ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 : 0.35;
        var primary = BrushResource("BeansPrimaryBrush");
        var secondary = BrushResource("BeansSecondaryBrush");
        var intensity = _playerSettings.EffectIntensity;

        if (mode == "spectrum")
        {
            for (var index = 0; index < 24; index++)
            {
                var value = _spectrumValues.Length > index && _spectrumValues[index] > 0.01
                    ? _spectrumValues[index]
                    : 0.18 + 0.55 * Math.Abs(Math.Sin(time * (1.5 + index % 4 * 0.13) + index * 0.65));
                var barHeight = Math.Max(3, height * 0.58 * value * intensity);
                var bar = new Rectangle { Width = 5, Height = barHeight, RadiusX = 2.5, RadiusY = 2.5, Fill = index % 3 == 0 ? secondary : primary, Opacity = 0.45 };
                Canvas.SetLeft(bar, width * 0.28 + index * Math.Max(7, width * 0.44 / 24));
                Canvas.SetTop(bar, height - barHeight);
                PlayerEffectCanvas.Children.Add(bar);
            }
            return;
        }

        if (mode == "vinyl")
        {
            for (var index = 0; index < 6; index++)
            {
                var size = 24 + index * 10;
                var ring = new Ellipse { Width = size, Height = size, Stroke = index % 2 == 0 ? primary : secondary, StrokeThickness = 1, Opacity = 0.08 + intensity * 0.08 };
                Canvas.SetLeft(ring, 28 - size / 2);
                Canvas.SetTop(ring, height / 2 - size / 2);
                PlayerEffectCanvas.Children.Add(ring);
            }
            return;
        }

        var count = mode == "stardust" ? 18 : 4;
        for (var index = 0; index < count; index++)
        {
            var phase = (time * (mode == "stardust" ? 0.12 : 0.04) + index * 0.17) % 1;
            var size = mode == "stardust" ? 2 + index % 4 : 28 + index * 22;
            var particle = new Ellipse { Width = size, Height = size, Fill = index % 2 == 0 ? primary : secondary, Opacity = (mode == "stardust" ? 0.12 : 0.05) * intensity };
            Canvas.SetLeft(particle, mode == "stardust" ? phase * width : width * (0.3 + index * 0.12) - size / 2);
            Canvas.SetTop(particle, mode == "stardust" ? (index * 29 % (int)height) : height / 2 - size / 2);
            PlayerEffectCanvas.Children.Add(particle);
        }
    }

    private async void Register_Click(object sender, RoutedEventArgs e)
    {
        await RunAccountAction(async () =>
        {
            ConfigureApi();
            if (NicknameBox.Text.Trim().Length < 2 || PasswordBox.Password.Length < 8) throw new InvalidOperationException("昵称至少 2 个字符，密码至少 8 位");
            var profile = new CryptoProfile("argon2id-v1", Convert.ToBase64String(AccountCrypto.RandomBytes(16)), 65_536, 3, 2);
            var keys = await AccountCrypto.DeriveAsync(PasswordBox.Password, profile);
            _pendingVaultKey = AccountCrypto.RandomBytes(32);
            _pendingRegistration = await _api.StartRegistrationAsync(NicknameBox.Text.Trim(), EmailBox.Text.Trim().ToLowerInvariant(), keys.AuthSecret, profile, AccountCrypto.WrapVaultKey(_pendingVaultKey, keys.VaultWrappingKey), _device);
            AccountStatus.Text = _pendingRegistration.DevelopmentCode is null ? "验证码已发送，请检查邮箱。" : $"开发环境验证码：{_pendingRegistration.DevelopmentCode}";
        });
    }

    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        await RunAccountAction(async () =>
        {
            if (_pendingRegistration is null || _pendingVaultKey is null) throw new InvalidOperationException("请先提交注册信息");
            var result = await _api.VerifyEmailAsync(_pendingRegistration.RegistrationId, VerificationBox.Text.Trim());
            await CompleteLoginAsync(result.Account, result.Tokens, _pendingVaultKey, uploadEmptyVault: true);
        });
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        await RunAccountAction(async () =>
        {
            ConfigureApi();
            var email = EmailBox.Text.Trim().ToLowerInvariant();
            var challenge = await _api.LoginChallengeAsync(email);
            var keys = await AccountCrypto.DeriveAsync(PasswordBox.Password, challenge.CryptoProfile);
            var result = await _api.LoginAsync(email, keys.AuthSecret, _device);
            var vaultKey = AccountCrypto.UnwrapVaultKey(Convert.FromBase64String(result.WrappedVaultKey), keys.VaultWrappingKey);
            await CompleteLoginAsync(result.Account, result.Tokens, vaultKey, uploadEmptyVault: false);
        });
    }

    private async void CreateQr_Click(object sender, RoutedEventArgs e)
    {
        await RunAccountAction(async () =>
        {
            ConfigureApi();
            _qrPolling?.Cancel();
            _qrPolling = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var keyPair = AccountCrypto.MakeQrKeyPair();
            var secret = AccountCrypto.RandomBytes(32);
            var qr = await _api.CreateQrSessionAsync(_device, keyPair.PublicKey, secret, _qrPolling.Token);
            var context = new QrLoginContext(qr, secret, keyPair.PrivateKey);
            await RenderQrAsync(context.Payload);
            QrCodeText.Text = $"设备校验码：{qr.VerificationCode}";
            _ = PollQrAsync(context, _qrPolling.Token);
        });
    }

    private async Task PollQrAsync(QrLoginContext context, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var state = await _api.GetQrSessionAsync(context.Session.Id, ct);
                if (state.Status == "approved")
                {
                    var result = await _api.ExchangeQrSessionAsync(context, _device, ct);
                    var vaultKey = AccountCrypto.DecryptVaultKeyFromQr(Convert.FromBase64String(result.EncryptedVaultKey), context.PrivateKey);
                    await CompleteLoginAsync(result.Account, result.Tokens, vaultKey, uploadEmptyVault: false);
                    return;
                }
                if (state.Status == "expired") throw new InvalidOperationException("登录二维码已过期");
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AccountStatus.Text = ex.Message; }
    }

    private async void AuthorizeQq_Click(object sender, RoutedEventArgs e) => await RunAccountAction(() => AuthorizeProviderAsync("qq", "https://y.qq.com/", "QQ 音乐"));
    private async void AuthorizeNetease_Click(object sender, RoutedEventArgs e) => await RunAccountAction(() => AuthorizeProviderAsync("netease", "https://music.163.com/", "网易云音乐"));

    private async void ApproveQr_Click(object sender, RoutedEventArgs e)
    {
        await RunAccountAction(async () =>
        {
            if (_session is null) throw new InvalidOperationException("请先登录 Beans 账号");
            ConfigureApi();
            if (!Uri.TryCreate(ApprovalPayloadBox.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "beans") throw new InvalidOperationException("二维码内容无效");
            var rawSession = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .FirstOrDefault(part => part.Length == 2 && part[0] == "session")?[1];
            if (!Guid.TryParse(Uri.UnescapeDataString(rawSession ?? ""), out var id)) throw new InvalidOperationException("二维码会话无效");
            var target = await _api.GetQrSessionAsync(id);
            if (target.VerificationCode != ApprovalCodeBox.Text.Trim()) throw new InvalidOperationException("校验码不一致");
            var encrypted = AccountCrypto.EncryptVaultKeyForQr(_session.VaultKey, Convert.FromBase64String(target.EphemeralPublicKey));
            await _api.ApproveQrSessionAsync(id, target.VerificationCode, encrypted);
            AccountStatus.Text = $"已允许 {target.Device.Name} 登录";
        });
    }

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e) => await LoadDevicesAsync();

    private async void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        await RunAccountAction(async () =>
        {
            if (_sync is null) throw new InvalidOperationException("请先登录 Beans 账号");
            await _sync.SyncAsync();
            AccountStatus.Text = $"同步完成 · {DateTime.Now:g}";
        });
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        try { ConfigureApi(); await _api.LogoutAsync(); } catch { }
        _qrPolling?.Cancel();
        _secureStore.Remove("session");
        _session = null;
        _sync = null;
        _api.ClearTokens();
        AccountButton.Content = "登录 Beans 账号";
        HeroStatus.Text = "已退出 Beans 账号；本机 QQ 音乐与网易云授权仍然保留。";
        AccountStatus.Text = "已退出 Beans 账号，平台授权可继续使用";
        DevicesList.Items.Clear();
    }

    private async Task LoadDevicesAsync()
    {
        await RunAccountAction(async () =>
        {
            if (_session is null) throw new InvalidOperationException("请先登录 Beans 账号");
            ConfigureApi();
            var devices = await _api.ListDevicesAsync();
            DevicesList.Items.Clear();
            foreach (var device in devices)
            {
                DevicesList.Items.Add(new ListViewItem
                {
                    Tag = device.Id,
                    Content = $"{device.Name} · {PlatformName(device.Platform)} · {(device.Revoked ? "已撤销" : device.Id == _device.Id ? "本机" : device.LastSeenAt.LocalDateTime.ToString("g"))}"
                });
            }
            AccountStatus.Text = $"已加载 {devices.Count} 台设备";
        });
    }

    private async void RevokeDevice_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesList.SelectedItem is not ListViewItem item || item.Tag is not Guid id) { AccountStatus.Text = "请先选择设备"; return; }
        await RunAccountAction(async () =>
        {
            ConfigureApi();
            await _api.RevokeDeviceAsync(id);
            if (id == _device.Id)
            {
                _secureStore.Remove("session");
                _session = null;
                _validatedPlatforms.Clear();
                _api.ClearTokens();
                AccountButton.Content = "登录 Beans 账号";
                HeroStatus.Text = "本机已退出，保险库密钥已移除。";
                DevicesList.Items.Clear();
                return;
            }
            await LoadDevicesAsync();
        });
    }

    private async void StartReset_Click(object sender, RoutedEventArgs e)
    {
        await RunAccountAction(async () =>
        {
            ConfigureApi();
            var result = await _api.StartPasswordResetAsync(ResetEmailBox.Text.Trim().ToLowerInvariant());
            AccountStatus.Text = result.DevelopmentCode is null
                ? "如果账号存在，重置验证码已发送，10 分钟内有效。"
                : $"开发环境重置验证码：{result.DevelopmentCode}";
        });
    }

    private async void CompleteReset_Click(object sender, RoutedEventArgs e)
    {
        await RunAccountAction(async () =>
        {
            ConfigureApi();
            if (ResetPasswordBox.Password.Length < 8) throw new InvalidOperationException("新密码至少 8 位");
            var profile = new CryptoProfile("argon2id-v1", Convert.ToBase64String(AccountCrypto.RandomBytes(16)), 65_536, 3, 2);
            var keys = await AccountCrypto.DeriveAsync(ResetPasswordBox.Password, profile);
            var newVault = AccountCrypto.RandomBytes(32);
            await _api.CompletePasswordResetAsync(
                ResetEmailBox.Text.Trim().ToLowerInvariant(), ResetCodeBox.Text.Trim(), keys.AuthSecret,
                profile, AccountCrypto.WrapVaultKey(newVault, keys.VaultWrappingKey));
            _secureStore.Remove("session");
            _session = null;
            _sync = null;
            _api.ClearTokens();
            AccountButton.Content = "登录 Beans 账号";
            HeroStatus.Text = "密码已重置；本机 QQ 音乐与网易云授权仍然保留。";
            AccountStatus.Text = "密码已重置，全部旧 Beans 会话和旧跨端保险库已撤销。";
        });
    }

    private async Task AuthorizeProviderAsync(string provider, string address, string title)
    {
        var web = new WebView2 { Width = 840, Height = 620 };
        web.Loaded += async (_, _) =>
        {
            await web.EnsureCoreWebView2Async();
            var restored = provider == "qq" ? _platformCredentials.Qq : _platformCredentials.Netease;
            var domain = provider == "qq" ? ".qq.com" : ".music.163.com";
            foreach (var pair in restored ?? new Dictionary<string, string>())
            {
                var cookie = web.CoreWebView2.CookieManager.CreateCookie(pair.Key, pair.Value, domain, "/");
                cookie.IsSecure = true;
                web.CoreWebView2.CookieManager.AddOrUpdateCookie(cookie);
            }
            web.Source = new Uri(address);
        };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = $"授权{title}", Content = web, PrimaryButtonText = "完成授权", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await web.EnsureCoreWebView2Async();
        var cookies = await web.CoreWebView2.CookieManager.GetCookiesAsync(address);
        var values = cookies.Where(x => !string.IsNullOrEmpty(x.Value)).ToDictionary(x => x.Name, x => x.Value);
        if (!PlatformCredentialPolicy.LooksUsable(provider, values)) { AccountStatus.Text = $"未检测到有效的{title}会话，请完成登录后再确认"; return; }
        AccountStatus.Text = $"正在验证{title}并读取歌单…";
        var platform = await _platformClient.ValidateAndLoadAsync(provider, values);
        _platformCredentials = provider == "qq"
            ? _platformCredentials with { Qq = values, UpdatedAt = DateTimeOffset.UtcNow }
            : _platformCredentials with { Netease = values, UpdatedAt = DateTimeOffset.UtcNow };
        _secureStore.Save("platform-credentials", _platformCredentials);
        await UploadVaultAsync();
        _validatedPlatforms.Add(provider);
        _credentialProbe.Clear(provider);
        await RunCredentialProbeAsync(provider, CredentialProbeMode.Manual);
        await SavePlatformMirrorAsync(provider, platform.Playlists);
        AccountStatus.Text = _session is null
            ? $"{title}已验证：{platform.Profile.Nickname} · 授权已安全保存在本机"
            : $"{title}已验证：{platform.Profile.Nickname} · {platform.Playlists.Count} 个歌单已加密同步";
    }

    private async Task CompleteLoginAsync(AccountRecord account, TokenPair tokens, byte[] vaultKey, bool uploadEmptyVault)
    {
        _api.RestoreTokens(tokens);
        _session = new StoredSession(account, tokens, vaultKey);
        _secureStore.Save("session", _session);
        if (uploadEmptyVault) await UploadVaultAsync();
        else await RestoreVaultAsync();
        SetSignedIn(account);
        await InitializeSyncAsync();
        var platformIssues = !uploadEmptyVault && await ValidateRestoredPlatformsAsync();
        SetSignedIn(account);
        AccountStatus.Text = platformIssues ? "登录成功；部分平台需要重新授权" : "登录成功，授权保险库与歌单已同步";
    }

    private async Task UploadVaultAsync()
    {
        if (_session is null) return;
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(_platformCredentials, JsonOptions.Default);
        var encrypted = AccountCrypto.Encrypt(plaintext, _session.VaultKey);
        CryptographicOperations.ZeroMemory(plaintext);
        _vaultVersion = Math.Max(1, _vaultVersion + 1);
        await _api.PutVaultAsync(new VaultEnvelope(_vaultVersion, Convert.ToBase64String(encrypted), DateTimeOffset.UtcNow));
    }

    private async Task RestoreVaultAsync()
    {
        if (_session is null) return;
        try
        {
            var envelope = await _api.GetVaultAsync();
            var plaintext = AccountCrypto.Decrypt(Convert.FromBase64String(envelope.Ciphertext), _session.VaultKey);
            try
            {
                var remote = JsonSerializer.Deserialize<PlatformCredentialBundle>(plaintext, JsonOptions.Default);
                if (remote is not null)
                {
                    _platformCredentials = new PlatformCredentialBundle(
                        remote.Qq ?? _platformCredentials.Qq,
                        remote.Netease ?? _platformCredentials.Netease,
                        remote.UpdatedAt > _platformCredentials.UpdatedAt ? remote.UpdatedAt : _platformCredentials.UpdatedAt);
                    _secureStore.Save("platform-credentials", _platformCredentials);
                }
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            _vaultVersion = envelope.Version;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { await UploadVaultAsync(); }
    }

    private void SetSignedIn(AccountRecord account)
    {
        AccountButton.Content = account.Nickname;
        var restored = new List<string>();
        if (_platformCredentials.Qq is { } qq && PlatformCredentialPolicy.LooksUsable("qq", qq)) restored.Add(_validatedPlatforms.Contains("qq") ? "QQ 音乐已验证" : "QQ 音乐待验证");
        if (_platformCredentials.Netease is { } netease && PlatformCredentialPolicy.LooksUsable("netease", netease)) restored.Add(_validatedPlatforms.Contains("netease") ? "网易云已验证" : "网易云待验证");
        var status = restored.Count == 0 ? "尚未恢复平台授权" : string.Join("、", restored);
        HeroStatus.Text = $"{account.Nickname}，保险库已在本机安全解锁；{status}。凭证只在客户端使用。";
        AccountStatus.Text = $"已登录：{account.Email}";
    }

    private async Task RestoreSignedInSessionAsync()
    {
        try
        {
            ConfigureApi();
            await RestoreVaultAsync();
            if (_session is not null) SetSignedIn(_session.Account);
            await InitializeSyncAsync();
            var platformIssues = await ValidateRestoredPlatformsAsync();
            if (_session is not null) SetSignedIn(_session.Account);
            AccountStatus.Text = platformIssues ? "已恢复 Beans 会话；部分平台需要重新授权" : "已恢复 Beans 会话、授权保险库与同步数据";
        }
        catch (Exception ex) { AccountStatus.Text = $"会话待恢复：{ex.Message}"; }
    }

    private async Task InitializeSyncAsync()
    {
        if (_session is null) return;
        _sync = new BeansSyncCoordinator(_api, _syncStore, _session.Account.Id, _device.Id, _session.VaultKey);
        await _syncStore.InitializeAsync();
        var state = await _syncStore.AccountStateAsync(_session.Account.Id);
        if (!state.Seeded)
        {
            if (state.Cursor == 0)
            {
                var currentTheme = (ThemePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "aurora";
                await _sync.EnqueueAsync("theme", "current", ThemePayloadFor(currentTheme));
                await _sync.EnqueueAsync("preference", "platforms", PlayerPreferencePayload());
            }
            await _syncStore.MarkSeededAsync(_session.Account.Id);
        }
        await _sync.SyncAsync();
        var remoteTheme = await _sync.ReadAsync<ThemePayload>("theme", "current");
        if (remoteTheme is not null)
        {
            var item = ThemePicker.Items.OfType<ComboBoxItem>().FirstOrDefault(x => x.Tag?.ToString() == remoteTheme.ReferenceStyle);
            if (item is not null)
            {
                _applyingRemoteTheme = true;
                ThemePicker.SelectedItem = item;
                ThemeService.Apply(remoteTheme.ReferenceStyle, Root);
                ApplyReferenceLayout(remoteTheme.ReferenceStyle);
                Typography.Percent = remoteTheme.FontScalePercent ?? 100;
                _applyingRemoteTheme = false;
            }
        }
        var remotePlayer = await _sync.ReadAsync<PlatformPreferencePayload>("preference", "platforms")
            ?? await _sync.ReadAsync<PlatformPreferencePayload>("preference", "player");
        if (remotePlayer is not null)
        {
            _playerSettings = _playerSettings with
            {
                EffectMode = remotePlayer.PlayerEffectMode is "quiet" or "flow" or "stardust" or "spectrum" or "vinyl" ? remotePlayer.PlayerEffectMode : _playerSettings.EffectMode,
                EffectIntensity = Math.Clamp(remotePlayer.PlayerEffectIntensity ?? _playerSettings.EffectIntensity, 0.25, 1),
                LyricStyle = remotePlayer.LyricStylePreset is "minimal" or "flow" or "karaoke" or "contrast" or "custom" ? remotePlayer.LyricStylePreset : _playerSettings.LyricStyle,
                LyricFontSize = Math.Clamp(remotePlayer.LyricFontSize ?? _playerSettings.LyricFontSize, 12, 40),
                LyricLineSpacing = Math.Clamp(remotePlayer.LyricLineSpacing ?? _playerSettings.LyricLineSpacing, 0, 20),
                LyricTranslation = remotePlayer.LyricTranslation ?? _playerSettings.LyricTranslation,
                LyricAlignment = remotePlayer.LyricAlignment is "leading" or "center" ? remotePlayer.LyricAlignment : _playerSettings.LyricAlignment
            };
            _secureStore.Save("player-experience", _playerSettings);
            ApplyExperienceSettings();
            RebuildLyrics();
        }
    }

    private async Task<bool> ValidateRestoredPlatformsAsync()
    {
        var messages = new List<string>();
        if (_platformCredentials.Qq is { } qq)
        {
            var probe = await RunCredentialProbeAsync("qq", CredentialProbeMode.Automatic);
            if (probe.Status == CredentialProbeStatus.Valid)
                try
                {
                    var result = await _platformClient.ValidateAndLoadAsync("qq", qq);
                    await SavePlatformMirrorAsync("qq", result.Playlists);
                }
                catch (Exception) { messages.Add("QQ 音乐歌单暂时无法刷新"); }
            else if (probe.Status is CredentialProbeStatus.Invalid or CredentialProbeStatus.PlaybackLimited)
                messages.Add("QQ 音乐需要重新授权");
            else if (probe.Status == CredentialProbeStatus.NetworkError)
                messages.Add("QQ 音乐暂时无法验证，稍后重试");
        }
        if (_platformCredentials.Netease is { } netease)
        {
            var probe = await RunCredentialProbeAsync("netease", CredentialProbeMode.Automatic);
            if (probe.Status == CredentialProbeStatus.Valid)
                try
                {
                    var result = await _platformClient.ValidateAndLoadAsync("netease", netease);
                    await SavePlatformMirrorAsync("netease", result.Playlists);
                }
                catch (Exception) { messages.Add("网易云音乐歌单暂时无法刷新"); }
            else if (probe.Status == CredentialProbeStatus.Invalid)
                messages.Add("网易云音乐需要重新授权");
            else if (probe.Status == CredentialProbeStatus.NetworkError)
                messages.Add("网易云音乐暂时无法验证，稍后重试");
        }
        if (messages.Count > 0) AccountStatus.Text = string.Join("；", messages);
        return messages.Count > 0;
    }

    private async Task SavePlatformMirrorAsync(string provider, IReadOnlyList<MirrorPlaylist> playlists)
    {
        if (_sync is null) return;
        await _sync.EnqueueAsync("platformMirror", provider, new PlatformMirrorPayload(provider, playlists, DateTimeOffset.UtcNow));
        await _sync.SyncAsync();
    }

    private ThemePayload ThemePayloadFor(string key) => key switch
    {
        "paper" => new("paper", "emerald", null, "#F5F7F5", true, "clear", (int)Typography.Percent),
        "midnight" => new("midnight", "cyber", null, "#070B14", true, "outline", (int)Typography.Percent),
        _ => new("aurora", "mint", null, "#071F1C", true, "liquid", (int)Typography.Percent)
    };

    private static string PlatformName(string platform) => platform switch { "ios" => "iOS", "macos" => "macOS", "windows" => "Windows", _ => platform };

    private void ConfigureApi()
    {
        if (!Uri.TryCreate(ServerBox.Text.Trim(), UriKind.Absolute, out var server) || (server.Scheme != "http" && server.Scheme != "https"))
            throw new InvalidOperationException("请输入有效的账号服务地址");
        if (!server.AbsoluteUri.EndsWith('/')) server = new Uri(server.AbsoluteUri + "/");
        _api.Configure(server);
    }

    private async Task RunAccountAction(Func<Task> action)
    {
        try { AccountStatus.Text = "正在处理…"; await action(); }
        catch (Exception ex) { AccountStatus.Text = ex.Message; }
    }

    private async Task RenderQrAsync(string text)
    {
        using var data = QRCodeGenerator.GenerateQrCode(text, QRCodeGenerator.ECCLevel.Q);
        using var code = new PngByteQRCode(data);
        var bytes = code.GetGraphic(12);
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        QrImage.Source = bitmap;
        QrImage.Visibility = Visibility.Visible;
    }
}
