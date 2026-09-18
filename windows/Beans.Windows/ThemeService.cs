using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Beans.Windows;

internal static class ThemeService
{
    public static void Apply(string key, FrameworkElement root)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "design", "tokens.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var theme = document.RootElement.GetProperty("themes").GetProperty(key);
        var colors = theme.GetProperty("colors");
        Set("BeansBackground", colors.GetProperty("background").GetString()!);
        Set("BeansSurface", colors.GetProperty("surface").GetString()!);
        Set("BeansPrimary", colors.GetProperty("primary").GetString()!);
        Set("BeansSecondary", colors.GetProperty("secondary").GetString()!);
        Set("BeansText", colors.GetProperty("text").GetString()!);
        Set("BeansMuted", colors.GetProperty("textMuted").GetString()!);
        root.RequestedTheme = theme.GetProperty("mode").GetString() == "light" ? ElementTheme.Light : ElementTheme.Dark;
    }

    public static Brush MusicUniverseBackground(string key) => key switch
    {
        "paper" => new SolidColorBrush(Parse("#F4F7F5")),
        "midnight" => Gradient("#FF040713", "#FF071B32", "#FF26113E"),
        _ => Gradient("#F20A3932", "#F2071F20", "#DC553A12")
    };

    private static LinearGradientBrush Gradient(params string[] colors)
    {
        var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1) };
        for (var index = 0; index < colors.Length; index++)
        {
            brush.GradientStops.Add(new GradientStop
            {
                Color = Parse(colors[index]),
                Offset = colors.Length == 1 ? 0 : (double)index / (colors.Length - 1)
            });
        }
        return brush;
    }

    private static void Set(string name, string value)
    {
        var color = Parse(value);
        Application.Current.Resources[name + "Color"] = color;
        Application.Current.Resources[name + "Brush"] = new SolidColorBrush(color);
    }

    private static Color Parse(string hex)
    {
        var raw = hex.TrimStart('#');
        if (raw.Length == 6) raw = "FF" + raw;
        return ColorHelper.FromArgb(
            Convert.ToByte(raw[..2], 16),
            Convert.ToByte(raw.Substring(2, 2), 16),
            Convert.ToByte(raw.Substring(4, 2), 16),
            Convert.ToByte(raw.Substring(6, 2), 16));
    }
}
