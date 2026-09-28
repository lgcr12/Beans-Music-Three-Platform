#if DEBUG
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Beans.Windows.Rebuild.Controls;
using Beans.Windows.Rebuild.Infrastructure.Navigation;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Pages.Player;
using Beans.Windows.Rebuild.Services.Lyrics;
using Beans.Windows.Rebuild.Services.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Beans.Windows.Rebuild.Tools;

// Explicit opt-in integration harness. Separate playback instance, silent local
// WAV, generated test lyrics and queue: no user history, favorites or credentials.
internal static class AnimeSceneValidation
{
    private sealed class PreviewLyrics : ILyricsService
    {
        public void Invalidate(LyricsRequest request) { }
        public Task<LyricsResult> GetLyricsAsync(LyricsRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(LyricsResult.Loaded(new LrcParser().Parse(string.Join('\n', Enumerable.Range(0, 40).Select(i =>
                $"[{i * 4 / 60:00}:{i * 4 % 60:00}.00]" + ((i % 5) switch
                {
                    0 => "风经过放学后的街道", 1 => "把春天的颜色轻轻收藏",
                    2 => "远方的列车正驶向晴朗的明天", 3 => "那些未说出口的心愿会随着花瓣飘向很远很远的地方", _ => "愿此刻的歌声陪你走过漫长夏日"
                }))), PlatformId.Local)));
    }
    internal static async void Start(Window window)
    {
        var directory = Environment.GetEnvironmentVariable("BEANS_ANIME_VALIDATE_DIR");
        var captureFile = Environment.GetEnvironmentVariable("BEANS_ANIME_CAPTURE_FILE");
        if (string.IsNullOrWhiteSpace(directory) && string.IsNullOrWhiteSpace(captureFile)) return;
        directory = string.IsNullOrWhiteSpace(directory)
            ? Path.GetDirectoryName(Path.GetFullPath(captureFile!))!
            : directory;
        Directory.CreateDirectory(directory);
        var report = new List<object>();
        void Trace(string message) => File.AppendAllText(Path.Combine(directory, "steps.log"), $"{DateTimeOffset.Now:O} {message}\n");
        using var player = new PlaybackService(NullLogger<PlaybackService>.Instance);
        try
        {
            var wav = Path.Combine(directory, "silent-validation.wav");
            using (var writer = new BinaryWriter(File.Create(wav)))
            {
                const int bytes = 8000 * 180 * 2;
                writer.Write("RIFF"u8); writer.Write(bytes + 36); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
                writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(bytes); writer.Write(new byte[bytes]);
            }
            player.AttachDispatcherQueue(window.DispatcherQueue); player.SetVolume(0);
            for (var i = 0; i < 4; i++) player.AddToQueue(new PlaybackItem("scene-validation-" + i, new[] { "晴空与风 · 动效验收", "花瓣寄来的信", "沿着河岸走向明天", "放课后的旋律" }[i], "本地静音测试", "动态场景验收", "ms-appx:///Assets/Home/hero-mountain-lake.jpg", wav, TimeSpan.FromSeconds(180)));
            await player.PlayAsync(player.Queue[0]);
            window.ExtendsContentIntoTitleBar = false;
            window.Title = "Beans 动效验收 · 本地静音测试";
            // Match AppShell navigation: keep one XamlRoot, replace hosted pages.
            // Replacing Window.Content itself each pass recreates WinUI roots.
            var previewHost = new Grid();
            window.Content = previewHost;
            PlayerPage Make(string kind)
            {
                Environment.SetEnvironmentVariable("BEANS_ANIME_SCENE_PREVIEW", kind);
                var page = new PlayerPage(player, new NavigationService(), new PreviewLyrics(), animeMode: true);
                page.AttachAnimeWindow(window);
                page.FullscreenRequested += (_, full) => { window.AppWindow.SetPresenter(full ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped); page.SetFullscreenState(full); };
                previewHost.Children.Clear();
                previewHost.Children.Add(page);
                return page;
            }
            if (!string.IsNullOrWhiteSpace(captureFile))
            {
                var captureScene = Environment.GetEnvironmentVariable("BEANS_ANIME_CAPTURE_SCENE") == "summer" ? "summer" : "spring";
                var page = Make(captureScene);
                await Until(() => page.ValidationMotion?.Ready == true);
                await Task.Delay(1800);
                page.UpdateLayout();
                await CaptureAsync(page, Path.GetFullPath(captureFile));
                window.Title = $"Beans {captureScene} 动效验收 · 已完成";
                return;
            }
            foreach (var kind in new[] { "summer", "spring" })
            {
                var page = Make(kind);
                Trace(kind + " created");
                await Until(() => page.ValidationMotion?.Ready == true);
                await Until(() => page.ValidationMotion?.MotionState == AnimeMotionState.Playing);
                window.Title = $"Beans 动效验收 · {kind} · 65 秒播放";
                await File.WriteAllTextAsync(Path.Combine(directory, "stage.txt"), kind + " recording");
                var chosen = page.ValidationMotion!.Scene;
                await Task.Delay(Environment.GetEnvironmentVariable("BEANS_ANIME_VALIDATE_QUICK") == "1" ? 2000 : 65000);
                Trace(kind + " playback complete");
                player.TogglePlayPause(); await Task.Delay(900);
                var paused = page.ValidationMotion.MotionState;
                player.TogglePlayPause(); await Task.Delay(300);
                Trace(kind + " pause resume complete");
                page.ValidationReduceMotion(true); var reduced = page.ValidationMotion.MotionState;
                page.ValidationReduceMotion(false);
                window.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen); page.SetFullscreenState(true); await Task.Delay(1600);
                var fullStable = page.ValidationMotion.Scene == chosen;
                window.AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped); page.SetFullscreenState(false);
                Trace(kind + " fullscreen complete");
                window.AppWindow.Resize(new SizeInt32(980, 680)); await Task.Delay(1600);
                var narrowStable = page.ValidationMotion.Scene == chosen;
                if (window.AppWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.Minimize(); await Task.Delay(800);
                    var hidden = page.ValidationMotion.MotionState;
                    presenter.Restore(); await Task.Delay(400);
                    report.Add(new { kind, paused = paused.ToString(), reduced = reduced.ToString(), hidden = hidden.ToString(), fullStable, narrowStable });
                }
                window.AppWindow.Resize(new SizeInt32(1440, 900));
                Trace(kind + " restore complete");
                await player.PlayAsync(player.Queue[1]); await Task.Delay(400);
                if (page.ValidationMotion.Scene != chosen) throw new InvalidOperationException("Scene changed with track");
                previewHost.Children.Clear();
                await Until(() => page.ValidationDisposed);
                Trace(kind + " unloaded");
                if (!page.ValidationDisposed) throw new InvalidOperationException("Page did not dispose");
            }
            var weak = new List<WeakReference>();
            for (var i = 0; i < 20; i++)
            {
                var page = Make(i % 2 == 0 ? "summer" : "spring");
                Trace("lifecycle " + i + " created");
                await Until(() => page.ValidationMotion?.Ready == true);
                weak.Add(new WeakReference(page));
                previewHost.Children.Clear();
                await Until(() => page.ValidationDisposed);
                if (!page.ValidationDisposed) throw new InvalidOperationException("Repeated page did not dispose");
                Trace("lifecycle " + i + " disposed");
            }
            for (var i = 0; i < 3; i++)
            {
                await Task.Delay(800);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            }
            report.Add(new { lifecyclePasses = 20, activeControllers = AnimeSceneMotionController.ActiveInstances, remainingWeakReferences = weak.Count(reference => reference.IsAlive) });
            if (AnimeSceneMotionController.ActiveInstances != 0) throw new InvalidOperationException("Animation controller leak");
            await File.WriteAllTextAsync(Path.Combine(directory, "validation.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            var finalPage = Make("spring");
            await Until(() => finalPage.ValidationMotion?.Ready == true);
            window.Title = "Beans 动效验收 · 完成 · 本地静音测试";
            // Keep the fixture visible until this dedicated validation process closes.
            await Task.Delay(Timeout.Infinite);
        }
        catch (Exception error) { await File.WriteAllTextAsync(Path.Combine(directory, "error.txt"), error.ToString()); }
    }

    private static async Task CaptureAsync(FrameworkElement surface, string path)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(surface);
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
    }
    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200; i++) { if (condition()) return; await Task.Delay(100); }
        throw new TimeoutException("Anime scene did not become ready/playing within 20 seconds");
    }
}
#endif
