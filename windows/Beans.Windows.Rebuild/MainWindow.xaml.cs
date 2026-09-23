using Beans.Windows.Rebuild.Shell;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace Beans.Windows.Rebuild;

public sealed partial class MainWindow : Window
{
    private readonly AppShell _shell;

    public MainWindow(IServiceProvider services)
    {
        InitializeComponent();
        Title = "Beans Music";

        _shell = new AppShell(services, this);
        RootHost.Children.Add(_shell);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(_shell.TitleBarDragRegion);
        ConfigureWindow();
    }

    private void ConfigureWindow()
    {
        var width = ReadPreviewDimension("BEANS_PREVIEW_WIDTH", 1440, 900, 2560);
        var height = ReadPreviewDimension("BEANS_PREVIEW_HEIGHT", 900, 600, 1600);
        var scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96d;
        var physicalWidth = (int)Math.Round(width * scale);
        var physicalHeight = (int)Math.Round(height * scale);
        AppWindow.Resize(new SizeInt32(physicalWidth, physicalHeight));
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        if (display is not null)
        {
            var x = display.WorkArea.X + Math.Max(0, (display.WorkArea.Width - physicalWidth) / 2);
            var y = display.WorkArea.Y + Math.Max(0, (display.WorkArea.Height - physicalHeight) / 2);
            AppWindow.Move(new PointInt32(x, y));
        }

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var titleBar = AppWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = ColorHelper.FromArgb(255, 16, 60, 51);
            titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(24, 8, 122, 89);
            titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(38, 8, 122, 89);
        }
    }

    private static int ReadPreviewDimension(string name, int fallback, int minimum, int maximum) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);
}
