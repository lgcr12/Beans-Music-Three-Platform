using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.Platforms;
using Beans.Windows.Rebuild.Services.Platforms.NetEase;

namespace Beans.Windows.Rebuild.Tests.Infrastructure;

internal static class NetEaseSearchTestInfrastructure
{
    public static NetEaseMusicSearchAdapter Adapter(
        IPlatformHttpClientFactory factory,
        IDiscoveryCache? cache = null,
        TimeSpan? searchLifetime = null) =>
        new(factory, new PlatformJsonSerializer(), new PlatformErrorMapper(), cache ?? new DiscoveryCache(), searchLifetime);

    public static string Fixture(string name) => NetEaseDiscoveryTestInfrastructure.Fixture(name);

    public static FakeHttpMessageHandler Handler(string fixture) => new((_, _) =>
        Task.FromResult(FakeHttpMessageHandler.Json(Fixture(fixture))));
}
