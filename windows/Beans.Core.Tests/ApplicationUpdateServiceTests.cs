using System.Runtime.InteropServices;
using System.Net;
using System.Text;
using Beans.Core;
using Xunit;

namespace Beans.Core.Tests;

public sealed class ApplicationUpdateServiceTests
{
    [Theory]
    [InlineData("1.2.0", "1.1.9", true)]
    [InlineData("v1.0.0", "1.0", false)]
    [InlineData("1.0.0", "1.0.1", false)]
    public void VersionComparisonIsNumeric(string candidate, string current, bool expected) =>
        Assert.Equal(expected, ApplicationUpdateService.IsNewer(candidate, current));

    [Fact]
    public void SelectsMatchingMsixArchitecture()
    {
        var release = new ApplicationRelease("2.0", "release", "notes", DateTimeOffset.UtcNow, new Uri("https://example.com"),
        [
            new("Beans-Windows-x64.msix", new Uri("https://example.com/x64")),
            new("Beans-Windows-arm64.msix", new Uri("https://example.com/arm64")),
            new("Beans-iOS.ipa", new Uri("https://example.com/ios"))
        ]);
        Assert.Contains("arm64", ApplicationUpdateService.SelectWindowsAsset(release, Architecture.Arm64)!.Name);
        Assert.Contains("x64", ApplicationUpdateService.SelectWindowsAsset(release, Architecture.X64)!.Name);
    }

    [Fact]
    public async Task SendsEtagAndHandlesNotModified()
    {
        string? observedEtag = null;
        using var http = new HttpClient(new RouteHandler(request =>
        {
            observedEtag = request.Headers.IfNoneMatch.SingleOrDefault()?.Tag;
            var response = new HttpResponseMessage(HttpStatusCode.NotModified);
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"release-2\"");
            return response;
        }));
        var result = await new ApplicationUpdateService(http).CheckAsync("\"release-1\"", TestContext.Current.CancellationToken);
        Assert.Equal("\"release-1\"", observedEtag);
        Assert.True(result.NotModified);
        Assert.Equal("\"release-2\"", result.ETag);
    }

    [Fact]
    public async Task ParsesReleaseAssetsAndNotes()
    {
        const string json = """
            [
              {"tag_name":"ios-v3.0.0","name":"iOS","body":"iOS only","published_at":"2026-09-18T01:00:00Z","html_url":"https://example.com/ios","assets":[{"name":"Beans-iOS26.ipa","browser_download_url":"https://example.com/app.ipa"}]},
              {"tag_name":"windows-v2.1.0","name":"Beans 2.1","body":"修复播放","published_at":"2026-09-18T00:00:00Z","html_url":"https://example.com/release","assets":[{"name":"Beans-Windows-x64.msix","browser_download_url":"https://example.com/app.msix"}]}
            ]
            """;
        using var http = new HttpClient(new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));
        var result = await new ApplicationUpdateService(http, Architecture.X64).CheckAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal("2.1.0", result.Release?.Version);
        Assert.Equal("修复播放", result.Release?.Notes);
        Assert.Single(result.Release!.Assets);
    }

    [Fact]
    public async Task DoesNotReturnAnotherWindowsArchitecture()
    {
        const string json = """
            [{"tag_name":"windows-v2.1.0","name":"Windows","body":"","published_at":"2026-09-18T00:00:00Z","html_url":"https://example.com/release","assets":[{"name":"Beans-Windows-arm64.msix","browser_download_url":"https://example.com/app.msix"}]}]
            """;
        using var http = new HttpClient(new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));
        var result = await new ApplicationUpdateService(http, Architecture.X64).CheckAsync(ct: TestContext.Current.CancellationToken);
        Assert.Null(result.Release);
    }

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(route(request));
    }
}
