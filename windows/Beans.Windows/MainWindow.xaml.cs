using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text.Json;
using Beans.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Beans.Windows;

public sealed partial class MainWindow : Window
{
    private sealed record StoredSession(AccountRecord Account, TokenPair Tokens, byte[] VaultKey);

    private readonly BeansApiClient _api = new(new HttpClient { Timeout = TimeSpan.FromSeconds(25) });
    private readonly PlatformMusicClient _platformClient = new(new HttpClient { Timeout = TimeSpan.FromSeconds(25) });
    private readonly WindowsVaultStore _secureStore = new();
    private readonly SyncOutbox _syncStore = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeansMusic", "sync.sqlite"));
    private readonly ListeningInsightsStore _insights = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeansMusic", "listening.sqlite"));
    private readonly DeviceInput _device;
    private readonly MediaPlayer _mediaPlayer = new();
    private readonly ResumableDownloader _downloader = new(new HttpClient { Timeout = TimeSpan.FromMinutes(10) });
    private IReadOnlyList<LocalMusicTrack> _localTracks = [];
    private readonly List<LocalMusicTrack> _queue = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _lyricTimer;
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
        Typography.PercentChanged += Typography_PercentChanged;
        _mediaPlayer.PlaybackSession.PlaybackStateChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateUniversePlaybackState);
        ApplyReferenceLayout("aurora");
        _ = RefreshMusicUniverseAsync();
        var deviceId = _secureStore.Load<Guid?>("device-id") ?? Guid.NewGuid();
        _secureStore.Save("device-id", deviceId);
        _device = new DeviceInput(deviceId, Environment.MachineName, "windows");
        _platformCredentials = _secureStore.Load<PlatformCredentialBundle>("platform-credentials")
            ?? new PlatformCredentialBundle(null, null, DateTimeOffset.UtcNow);
        _api.TokensChanged += tokens =>
        {
            if (_session is null) return;
            _session = _session with { Tokens = tokens };
            _secureStore.Save("session", _session);
        };
        Root.SizeChanged += (_, _) => UpdateRightRailWidth();
        _session = _secureStore.Load<StoredSession>("session");
        if (_session is not null)
        {
            _api.RestoreTokens(_session.Tokens);
            SetSignedIn(_session.Account);
            _ = RestoreSignedInSessionAsync();
        }
    }

    private void AccountButton_Click(object sender, RoutedEventArgs e) => AccountOverlay.Visibility = Visibility.Visible;
    private void CloseAccount_Click(object sender, RoutedEventArgs e) => AccountOverlay.Visibility = Visibility.Collapsed;

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
        updater.Type = Windows.Media.MediaPlaybackType.Music;
        updater.MusicProperties.Title = track.Title;
        updater.MusicProperties.Artist = "本地音乐";
        updater.Update();
        _mediaPlayer.Play();
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
        if (_mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) _mediaPlayer.Pause();
        else _mediaPlayer.Play();
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
            foreach (var line in _lyrics)
                LyricsList.Items.Add(new ListViewItem { Content = $"{line.Time:mm\\:ss}  {line.Text}", Tag = line });
            LyricsStatus.Text = _lyrics.Count == 0 ? "歌词文件为空" : $"已加载 {_lyrics.Count} 行歌词";
        }
        catch (Exception ex) { LyricsStatus.Text = $"歌词读取失败：{ex.Message}"; }
    }

    private void UpdateCurrentLyric()
    {
        if (_lyrics.Count == 0) return;
        var position = _mediaPlayer.PlaybackSession.Position;
        var low = 0;
        var high = _lyrics.Count - 1;
        var result = -1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (_lyrics[middle].Time <= position) { result = middle; low = middle + 1; }
            else high = middle - 1;
        }
        if (result < 0 || result == _currentLyricIndex) return;
        _currentLyricIndex = result;
        LyricsList.SelectedIndex = result;
        LyricsList.ScrollIntoView(LyricsList.Items[result], ScrollIntoViewAlignment.Leading);
        LyricsStatus.Text = _lyrics[result].Text;
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
                await _sync.EnqueueAsync("preference", "platforms", new PlatformPreferencePayload(["网易云", "QQ音乐"]));
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
    }

    private async Task<bool> ValidateRestoredPlatformsAsync()
    {
        var changed = false;
        var messages = new List<string>();
        if (_platformCredentials.Qq is { } qq)
        {
            try
            {
                var result = await _platformClient.ValidateAndLoadAsync("qq", qq);
                _validatedPlatforms.Add("qq");
                await SavePlatformMirrorAsync("qq", result.Playlists);
            }
            catch (UnauthorizedAccessException)
            {
                _platformCredentials = _platformCredentials with { Qq = null, UpdatedAt = DateTimeOffset.UtcNow };
                _validatedPlatforms.Remove("qq");
                changed = true;
                messages.Add("QQ 音乐需要重新授权");
            }
            catch (Exception) { messages.Add("QQ 音乐暂时无法验证，稍后重试"); }
        }
        if (_platformCredentials.Netease is { } netease)
        {
            try
            {
                var result = await _platformClient.ValidateAndLoadAsync("netease", netease);
                _validatedPlatforms.Add("netease");
                await SavePlatformMirrorAsync("netease", result.Playlists);
            }
            catch (UnauthorizedAccessException)
            {
                _platformCredentials = _platformCredentials with { Netease = null, UpdatedAt = DateTimeOffset.UtcNow };
                _validatedPlatforms.Remove("netease");
                changed = true;
                messages.Add("网易云音乐需要重新授权");
            }
            catch (Exception) { messages.Add("网易云音乐暂时无法验证，稍后重试"); }
        }
        if (changed)
        {
            _secureStore.Save("platform-credentials", _platformCredentials);
            await UploadVaultAsync();
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
