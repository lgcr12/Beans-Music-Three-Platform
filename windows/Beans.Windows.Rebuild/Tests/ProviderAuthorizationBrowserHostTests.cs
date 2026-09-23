using Beans.Windows.Rebuild.Services.Accounts;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class ProviderAuthorizationBrowserHostTests
{
    private static readonly string[] AllowedHosts = ["music.163.com", "ssl.ptlogin2.qq.com"];

    [Theory]
    [InlineData("https://music.163.com/login")]
    [InlineData("https://ssl.ptlogin2.qq.com/")]
    [InlineData("https://MUSIC.163.COM:443/login")]
    public void IsAllowedHttps_AcceptsOnlyExactAllowlistedHttpsHosts(string value)
    {
        Assert.True(ProviderAuthorizationBrowserHost.IsAllowedHttps(new Uri(value), AllowedHosts));
    }

    [Theory]
    [InlineData("http://music.163.com/login")]
    [InlineData("https://login.music.163.com/")]
    [InlineData("https://music.163.com.evil.example/")]
    [InlineData("https://music.163.com:8443/")]
    [InlineData("https://user@music.163.com/")]
    public void IsAllowedHttps_RejectsUnsafeOrNonExactOrigins(string value)
    {
        Assert.False(ProviderAuthorizationBrowserHost.IsAllowedHttps(new Uri(value), AllowedHosts));
    }

    [Fact]
    public void CalculateLayout_WideWindowEscapesDefaultContentDialogWidth()
    {
        var layout = ProviderAuthorizationBrowserHost.CalculateLayout(1440, 900);

        Assert.Equal(1120, layout.ContentViewportWidth);
        Assert.Equal(1200, layout.DialogMaxWidth);
        Assert.Equal(layout.ContentViewportWidth, layout.BrowserContentWidth);
        Assert.True(layout.DialogMaxWidth > 548);
    }

    [Theory]
    [InlineData(960, 864, 928)]
    [InlineData(720, 624, 688)]
    public void CalculateLayout_NarrowWindowKeepsProviderContentHorizontallyReachable(
        double availableWidth,
        double expectedViewportWidth,
        double expectedDialogWidth)
    {
        var layout = ProviderAuthorizationBrowserHost.CalculateLayout(availableWidth, 640);

        Assert.Equal(expectedViewportWidth, layout.ContentViewportWidth);
        Assert.Equal(expectedDialogWidth, layout.DialogMaxWidth);
        Assert.Equal(980, layout.BrowserContentWidth);
        Assert.True(layout.BrowserContentWidth > layout.ContentViewportWidth);
    }

    [Theory]
    [InlineData(double.NaN, double.NaN)]
    [InlineData(0, 0)]
    [InlineData(double.PositiveInfinity, double.NegativeInfinity)]
    public void CalculateLayout_InvalidMeasurementsUseSafeFallbacks(double width, double height)
    {
        var layout = ProviderAuthorizationBrowserHost.CalculateLayout(width, height);

        Assert.Equal(904, layout.ContentViewportWidth);
        Assert.Equal(620, layout.ContentHeight);
        Assert.Equal(980, layout.BrowserContentWidth);
        Assert.Equal(968, layout.DialogMaxWidth);
    }
}
