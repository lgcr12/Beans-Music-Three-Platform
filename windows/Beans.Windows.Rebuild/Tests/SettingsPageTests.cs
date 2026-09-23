using Beans.Windows.Rebuild.Pages.Settings;
using Beans.Windows.Rebuild.Services.Platforms;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class SettingsPageTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("72.5", 72.5)]
    [InlineData("100", 100)]
    public void StoredInvariantVolumeIsAccepted(string value, double expected)
    {
        Assert.True(SettingsPage.TryReadVolume(value, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NaN")]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("72,5")]
    public void MissingInvalidOrOutOfRangeVolumeIsRejected(string? value)
    {
        Assert.False(SettingsPage.TryReadVolume(value, out _));
    }

    [Fact]
    public void JsonPreferenceStore_RoundTripsWithoutPackagedApplicationData()
    {
        var directory = Path.Combine(Path.GetTempPath(), "BeansMusic.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var writer = new LocalSettingsPlatformPreferenceStore(path);
            writer.SetString("settings.default-volume", "64.5");

            var reader = new LocalSettingsPlatformPreferenceStore(path);

            Assert.Equal("64.5", reader.GetString("settings.default-volume"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void JsonPreferenceStore_CorruptFileFallsBackToEmptyState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "BeansMusic.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "{not-json");

            var store = new LocalSettingsPlatformPreferenceStore(path);

            Assert.Null(store.GetString("settings.default-volume"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
