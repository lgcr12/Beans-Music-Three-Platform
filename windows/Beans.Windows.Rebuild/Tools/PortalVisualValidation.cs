#if DEBUG
using System.Runtime.InteropServices.WindowsRuntime;
using System.Diagnostics;
using System.Text.Json;
using Beans.Windows.Rebuild.Controls;
using Beans.Windows.Rebuild.Infrastructure.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Beans.Windows.Rebuild.Tools;

// Opt-in developer capture. Uses the real control, real page and real Storyboard,
// with fixed times; Release builds contain none of this code.
internal static class PortalVisualValidation
{
    internal static string? DirectoryPath => Environment.GetEnvironmentVariable("BEANS_PORTAL_CAPTURE_DIR");
    internal static bool Enabled { get; private set; } = true;
    private static int _capturedPasses;

    internal static async void Start(Window window)
    {
        if (string.IsNullOrWhiteSpace(DirectoryPath)) return;
        try
        {
            await Task.Delay(2200);
            var portal = FindPortal(window.Content) ?? throw new InvalidOperationException("Portal was not found.");
            var navigation = App.Services.GetRequiredService<INavigationService>();
            navigation.Navigate("anime");
            var repeatedSwitches = 0;
            await portal.TransitionAsync(false, false, () => repeatedSwitches++);
            await WaitUntilAsync(() => _capturedPasses >= 1 && !portal.IsHitTestVisible);
            var entryClean = IsClean(portal);
            navigation.Navigate("discover");
            await WaitUntilAsync(() => _capturedPasses >= 2 && !portal.IsHitTestVisible);
            var returnClean = IsClean(portal);
            Enabled = false;

            var reducedSwitches = 0;
            var watch = Stopwatch.StartNew();
            await portal.TransitionAsync(true, false, () => reducedSwitches++);
            watch.Stop();
            var reducedClean = IsClean(portal);

            // Remove only this validation instance's overlay, then reattach it.
            // This exercises cancellation while an animation awaits completion.
            var parent = (Panel)portal.Parent;
            var index = parent.Children.IndexOf(portal);
            var interruptedSwitches = 0;
            var interrupted = portal.TransitionAsync(true, false, () => interruptedSwitches++);
            parent.Children.Remove(portal);
            await interrupted.WaitAsync(TimeSpan.FromSeconds(3));
            var cancellationClean = IsClean(portal);
            parent.Children.Insert(index, portal);
            await Task.Delay(100);
            var recoveredSwitches = 0;
            await portal.TransitionAsync(true, false, () => recoveredSwitches++);
            var report = new
            {
                entryClean, returnClean, repeatedSwitches, reducedSwitches,
                reducedElapsedMs = watch.ElapsedMilliseconds, reducedClean,
                interruptedSwitches, cancellationClean, recoveredSwitches,
                recoveredClean = IsClean(portal)
            };
            await File.WriteAllTextAsync(Path.Combine(DirectoryPath!, "validation.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            navigation.Navigate("anime");
        }
        catch (Exception error)
        {
            Directory.CreateDirectory(DirectoryPath!);
            await File.WriteAllTextAsync(Path.Combine(DirectoryPath!, "error.txt"), error.ToString());
        }
    }

    private static bool IsClean(AnimePortalTransition portal) => !portal.IsHitTestVisible &&
        portal.Background is null && portal.Children[0] is Canvas scene && scene.Children.Count == 0;

    private static AnimePortalTransition? FindPortal(DependencyObject root)
    {
        if (root is AnimePortalTransition portal) return portal;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindPortal(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(40)) throw new TimeoutException("Transition did not finish.");
            await Task.Delay(50);
        }
    }

    internal static async Task CaptureAsync(Storyboard storyboard, FrameworkElement surface, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(DirectoryPath)) return;
        var passDirectory = _capturedPasses == 0 ? DirectoryPath! : Path.Combine(DirectoryPath!, "return");
        Directory.CreateDirectory(passDirectory);
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetFullPath(passDirectory));
        // Let the destination's local artwork decode before taking deterministic
        // stills. This delay is exclusive to the opt-in validation process.
        await Task.Delay(700, token);
        storyboard.Begin();
        storyboard.Pause();
        try
        {
            foreach (var time in Enumerable.Range(0, (int)storyboard.Duration.TimeSpan.TotalMilliseconds / 50 + 1).Select(i => i * 50))
            {
                token.ThrowIfCancellationRequested();
                storyboard.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(time));
                await Task.Delay(80, token);
                surface.UpdateLayout();
                var bitmap = new RenderTargetBitmap();
                await bitmap.RenderAsync(surface);
                var pixels = (await bitmap.GetPixelsAsync()).ToArray();
                var file = await folder.CreateFileAsync($"frame-{time:D4}.png", CreationCollisionOption.ReplaceExisting);
                using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
                await encoder.FlushAsync();
            }
        }
        finally { storyboard.Stop(); }
        await File.WriteAllTextAsync(Path.Combine(passDirectory, "captured.txt"), $"Real WinUI Storyboard frames at 50 ms intervals through {storyboard.Duration.TimeSpan.TotalMilliseconds} ms.", token);
        _capturedPasses++;
    }
}
#endif
