using System.Net;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.Platforms;
using Beans.Windows.Rebuild.Services.Platforms.QQ;

namespace Beans.Windows.Rebuild.Tests.Infrastructure;

internal static class QqSearchTestInfrastructure
{
    public static QqMusicSearchAdapter Adapter(
        IPlatformHttpClientFactory factory,
        IDiscoveryCache? cache = null,
        TimeSpan? searchLifetime = null) =>
        new(factory, new PlatformJsonSerializer(), new PlatformErrorMapper(), cache ?? new DiscoveryCache(), searchLifetime);

    public static string Fixture(string name) => QqDiscoveryTestInfrastructure.Fixture(name);

    public static FakeHttpMessageHandler RoutingHandler() => new((request, _) =>
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = request.RequestUri.Query;
        var fixture = path.Contains("smartbox", StringComparison.Ordinal)
            ? "qq-search-smartbox-success.json"
            : query.Contains("t=8", StringComparison.Ordinal)
                ? "qq-search-albums-success.json"
                : "qq-search-tracks-success.json";
        return Task.FromResult(FakeHttpMessageHandler.Json(Fixture(fixture)));
    });

    public static FakeHttpMessageHandler StatusHandler(HttpStatusCode statusCode) => new((_, _) =>
        Task.FromResult(FakeHttpMessageHandler.Json("{}", statusCode)));
}
