using Beans.Windows.Rebuild.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Beans.Windows.Rebuild.Controls.PlatformBadge;

public sealed partial class PlatformBadge : UserControl
{
    public static readonly DependencyProperty PlatformProperty = DependencyProperty.Register(
        nameof(Platform), typeof(PlatformId), typeof(PlatformBadge), new PropertyMetadata(PlatformId.Local, OnPlatformChanged));

    public PlatformBadge()
    {
        InitializeComponent();
        ApplyPlatform(Platform);
    }

    public PlatformId Platform
    {
        get => (PlatformId)GetValue(PlatformProperty);
        set => SetValue(PlatformProperty, value);
    }

    private static void OnPlatformChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is PlatformBadge badge) badge.ApplyPlatform((PlatformId)args.NewValue);
    }

    private void ApplyPlatform(PlatformId platform)
    {
        var (glyph, label, color) = platform switch
        {
            PlatformId.QqMusic => ("Q", platform.ToDisplayName(), ColorHelper.FromArgb(255, 45, 181, 94)),
            PlatformId.NetEaseMusic => ("云", platform.ToDisplayName(), ColorHelper.FromArgb(255, 218, 50, 47)),
            PlatformId.KuGouMusic => ("K", platform.ToDisplayName(), ColorHelper.FromArgb(255, 38, 162, 232)),
            PlatformId.Beans => ("B", platform.ToDisplayName(), ColorHelper.FromArgb(255, 10, 143, 102)),
            PlatformId.Local => ("L", platform.ToDisplayName(), ColorHelper.FromArgb(255, 89, 108, 104)),
            _ => ("?", platform.ToDisplayName(), ColorHelper.FromArgb(255, 129, 155, 149))
        };
        LogoText.Text = glyph;
        LabelText.Text = label;
        LogoBackground.Background = new SolidColorBrush(color);
        AutomationProperties.SetName(this, $"来源：{label}");
    }
}
