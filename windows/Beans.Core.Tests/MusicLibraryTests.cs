using Beans.Core;
using Xunit;

namespace Beans.Core.Tests;

public sealed class MusicLibraryTests
{
    [Fact]
    public void LrcParserSortsLinesAndSupportsFractions()
    {
        var lines = LrcParser.Parse("[00:10.50]第二句\n[00:01.005][00:02.10]第一句");
        Assert.Equal(3, lines.Count);
        Assert.Equal("第一句", lines[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(1005), lines[0].Time);
        Assert.Equal(TimeSpan.FromMilliseconds(10500), lines[2].Time);
    }
}
