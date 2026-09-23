using Beans.Windows.Rebuild.Infrastructure.Navigation;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.Platforms;
using Beans.Windows.Rebuild.Services.Platforms.NetEase;
using Beans.Windows.Rebuild.Services.Platforms.QQ;
using Beans.Windows.Rebuild.Services.Playback;
using Beans.Windows.Rebuild.Services.Security;
using Beans.Windows.Rebuild.Services.Accounts;
using Beans.Windows.Rebuild.ViewModels;
using Beans.Windows.Rebuild.Services.Search;
using Beans.Windows.Rebuild.Services.LocalMusic;
using Beans.Windows.Rebuild.Services.Lyrics;
using Beans.Windows.Rebuild.Services.Downloads;
using Beans.Windows.Rebuild.Services.Details;
using Beans.Windows.Rebuild.Services.Library;
using Beans.Windows.Rebuild.Services.MusicUniverse;
using Beans.Windows.Rebuild.Services.PlatformLibrary;
using Beans.Windows.Rebuild.Services.BeansAccount;
using Beans.Windows.Rebuild.Services.BeansPlaylists;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace Beans.Windows.Rebuild;

public partial class App : Application
{
    private readonly ServiceProvider _services;

    public App()
    {
        InitializeComponent();
        UnhandledException += App_UnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddDebug().SetMinimumLevel(LogLevel.Information));
        services.AddSingleton<ISensitiveDataRedactor, SensitiveDataRedactor>();
        services.AddSingleton<ISafeLogger, SafePlatformLogger>();
        services.AddSingleton<IPlatformErrorMapper, PlatformErrorMapper>();
        services.AddSingleton<IPlatformJsonSerializer, PlatformJsonSerializer>();
        services.AddSingleton<IPlatformHttpClientFactory, PlatformHttpClientFactory>();
        services.AddSingleton<IPreviewModePolicy, BuildPreviewModePolicy>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IUserLibraryService, UserLibraryService>();
        services.AddSingleton<IBeansPlaylistService, JsonBeansPlaylistService>();
        services.AddSingleton<IMusicUniverseService, MusicUniverseService>();
        services.AddSingleton<IPlaybackService, PlaybackService>();
        services.AddSingleton<ISecureCredentialStore, CredentialLockerStore>();
        services.AddSingleton<IPlatformLibraryAdapter, QqPlatformLibraryAdapter>();
        services.AddSingleton<IPlatformLibraryAdapter, NetEasePlatformLibraryAdapter>();
        services.AddSingleton<IPlatformLibraryService>(provider => new PlatformLibraryService(
            provider.GetServices<IPlatformLibraryAdapter>(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BeansMusic", "Rebuild", "platform-library-cache.json")));
        services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(25) });
        services.AddSingleton<IBeansAccountMetadataStore, JsonBeansAccountMetadataStore>();
        services.AddSingleton<IBeansEncryptedSyncStore, JsonBeansEncryptedSyncStore>();
        services.AddSingleton<IBeansAccountCryptography, BeansAccountCryptography>();
        services.AddSingleton<BeansAccountService>();
        services.AddSingleton<IBeansAccountService>(provider => provider.GetRequiredService<BeansAccountService>());
        services.AddSingleton<IBeansSyncService>(provider => provider.GetRequiredService<BeansAccountService>());
        services.AddSingleton<IBeansVaultService>(provider => provider.GetRequiredService<BeansAccountService>());
        services.AddSingleton<ProviderAuthorizationBrowserHost>(_ => new ProviderAuthorizationBrowserHost(
            () => (Current as App)?._services.GetService<MainWindow>()?.Content?.XamlRoot));
        services.AddSingleton<IQqAuthorizationBrowser>(provider => provider.GetRequiredService<ProviderAuthorizationBrowserHost>());
        services.AddSingleton<INetEaseAuthorizationBrowser>(provider => provider.GetRequiredService<ProviderAuthorizationBrowserHost>());
        services.AddSingleton<IQqAuthorizationProbe>(provider => provider.GetRequiredService<QqAuthorizationProbe>());
        services.AddSingleton<INetEaseAuthorizationProbe>(provider => provider.GetRequiredService<NetEaseAuthorizationProbe>());
        services.AddSingleton<IQqAuthorizationFlow, QqAuthorizationFlow>();
        services.AddSingleton<INetEaseAuthorizationFlow, NetEaseAuthorizationFlow>();
        services.AddSingleton<IPlatformAuthAdapter, QqMusicAuthAdapter>();
        services.AddSingleton<IPlatformAuthAdapter, NetEaseMusicAuthAdapter>();
        services.AddSingleton<QqAuthorizationProbe>();
        services.AddSingleton<NetEaseAuthorizationProbe>();
        services.AddSingleton<IPlatformAuthService, CredentialBackedPlatformAuthService>();
        services.AddSingleton<QqPlaybackSourceResolver>();
        services.AddSingleton<NetEasePlaybackSourceResolver>();
        services.AddSingleton<IPlaybackSourceResolverRouter>(provider =>
            new PlaybackSourceResolverRouter([
                provider.GetRequiredService<QqPlaybackSourceResolver>(),
                provider.GetRequiredService<NetEasePlaybackSourceResolver>()]));
        services.AddSingleton<IPlatformPreferenceStore, LocalSettingsPlatformPreferenceStore>();
        services.AddSingleton<ILocalMusicCatalog, LocalMusicCatalog>();
        services.AddSingleton<ILocalMusicSearchCatalog>(provider => provider.GetRequiredService<ILocalMusicCatalog>());
        services.AddSingleton<IAudioMetadataReader, BasicAudioMetadataReader>();
        services.AddSingleton<ILrcFileResolver, LocalLrcFileResolver>();
        services.AddSingleton<ILocalPlaybackSourceFactory, LocalPlaybackSourceFactory>();
        services.AddSingleton<ILrcParser, LrcParser>();
        services.AddSingleton<ILyricsSourceAdapter>(provider => new LocalLyricsSourceAdapter(
            provider.GetRequiredService<ILocalMusicCatalog>(),
            provider.GetRequiredService<ILrcFileResolver>(),
            provider.GetRequiredService<ILrcParser>()));
        services.AddSingleton<ILyricsSourceAdapter, QqLyricsSourceAdapter>();
        services.AddSingleton<ILyricsSourceAdapter, NetEaseLyricsSourceAdapter>();
        services.AddSingleton<ILyricsService, LyricsService>();
        services.AddSingleton(new DownloadManagerOptions());
        services.AddSingleton<IDownloadSourceResolver, UnsupportedDownloadSourceResolver>();
        services.AddSingleton<IDownloadTransport, HttpDownloadTransport>();
        services.AddSingleton<IDownloadStorageProbe, SystemDownloadStorageProbe>();
        services.AddSingleton<IDownloadManager, DownloadManager>();
        services.AddSingleton<IOnlineMusicDetailAdapter, QqMusicDetailAdapter>();
        services.AddSingleton<IOnlineMusicDetailAdapter, NetEaseMusicDetailAdapter>();
        services.AddSingleton<IOnlineMusicDetailService, OnlineMusicDetailService>();
        services.AddSingleton<IPlatformSearchAdapter, QqMusicSearchAdapter>();
        services.AddSingleton<IPlatformSearchAdapter, NetEaseMusicSearchAdapter>();
        services.AddSingleton<IPlatformSearchAdapter, LocalMusicSearchAdapter>();
        services.AddSingleton<IMusicSearchService, MusicSearchService>();
        services.AddSingleton<IMusicPlatformService>(_ => new PreviewMusicPlatformService(new MusicPlatformDescriptor(
            PlatformId.QqMusic, "QQ 音乐", "QQ", "Q", "#2DB55E", true, true, AuthorizationState.SignedOut,
            true, false, true, true, true, "可浏览公开内容")));
        services.AddSingleton<IMusicPlatformService>(_ => new PreviewMusicPlatformService(new MusicPlatformDescriptor(
            PlatformId.NetEaseMusic, "网易云音乐", "网易云", "云", "#DA322F", true, true, AuthorizationState.SignedOut,
            true, true, true, true, true, "每日推荐需要登录")));
        services.AddSingleton<IMusicPlatformService>(_ => new PreviewMusicPlatformService(new MusicPlatformDescriptor(
            PlatformId.KuGouMusic, "酷狗音乐", "酷狗", "K", "#26A2E8", false, false, AuthorizationState.SignedOut,
            false, false, false, false, false, "当前版本暂不支持酷狗音乐")));
        services.AddSingleton<IMusicPlatformRegistry, MusicPlatformRegistry>();
        services.AddSingleton<IPlatformDiscoveryAdapter, QqMusicDiscoveryAdapter>();
        services.AddSingleton<IPlatformDiscoveryAdapter, NetEaseMusicDiscoveryAdapter>();
        services.AddSingleton<IPlatformDiscoveryAdapter>(provider => new StubPlatformDiscoveryAdapter(
            "kugou", new DiscoveryAdapterCapabilities(false, false, false, false, false, false),
            provider.GetRequiredService<IPlatformErrorMapper>()));
        services.AddSingleton<IDiscoveryCache, DiscoveryCache>();
        services.AddSingleton<PreviewDiscoveryService>();
        services.AddSingleton<IMusicDiscoveryService, QqPlatformDiscoveryService>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<DiscoverViewModel>();
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<MainWindow>();
        _services = services.BuildServiceProvider();
    }

    private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BeansMusic", "Rebuild", "ui-errors.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} UI: {args.Exception}\n");
        }
        catch { }
        args.Handled = true;
    }

    private static void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs args)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BeansMusic", "Rebuild", "ui-errors.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} Domain: {args.ExceptionObject}\n");
        }
        catch { }
    }

    public static IServiceProvider Services => ((App)Current)._services;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = _services.GetRequiredService<MainWindow>();
        _services.GetRequiredService<IPlaybackService>().AttachDispatcherQueue(window.DispatcherQueue);
        window.Activate();
    }
}
