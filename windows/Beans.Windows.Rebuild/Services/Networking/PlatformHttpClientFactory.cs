using System.Net;

namespace Beans.Windows.Rebuild.Services.Networking;

public sealed class PlatformHttpClientFactory : IPlatformHttpClientFactory
{
    public const string QqClientName = "Beans.Platform.Qq";
    public const string NetEaseClientName = "Beans.Platform.NetEase";
    public const string KuGouClientName = "Beans.Platform.KuGou";

    private readonly Dictionary<string, PlatformHttpClient> _clients;

    public PlatformHttpClientFactory(
        ISafeLogger logger,
        ISensitiveDataRedactor redactor,
        IPlatformErrorMapper errorMapper)
        : this(CreateDefaultHandlers(), logger, redactor, errorMapper)
    {
    }

    public PlatformHttpClientFactory(
        IReadOnlyDictionary<string, HttpMessageHandler> handlers,
        ISafeLogger logger,
        ISensitiveDataRedactor redactor,
        IPlatformErrorMapper errorMapper)
    {
        _clients = new Dictionary<string, PlatformHttpClient>(StringComparer.Ordinal)
        {
            ["qq"] = Create(QqClientName, "qq", handlers["qq"], logger, redactor, errorMapper),
            ["netease"] = Create(NetEaseClientName, "netease", handlers["netease"], logger, redactor, errorMapper),
            ["kugou"] = Create(KuGouClientName, "kugou", handlers["kugou"], logger, redactor, errorMapper)
        };
    }

    public IPlatformHttpClient Get(string platformId) => _clients.TryGetValue(platformId, out var client)
        ? client
        : throw new KeyNotFoundException($"No platform HTTP client is registered for '{platformId}'.");

    public void Dispose()
    {
        foreach (var client in _clients.Values) client.Dispose();
    }

    private static PlatformHttpClient Create(
        string name,
        string platformId,
        HttpMessageHandler handler,
        ISafeLogger logger,
        ISensitiveDataRedactor redactor,
        IPlatformErrorMapper mapper)
    {
        var httpClient = new HttpClient(handler, true);
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("BeansMusic-Windows/Phase5A");
        return new PlatformHttpClient(httpClient, new PlatformHttpClientOptions(name, platformId, 3), logger, redactor, mapper);
    }

    private static IReadOnlyDictionary<string, HttpMessageHandler> CreateDefaultHandlers() =>
        new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal)
        {
            ["qq"] = CreateSocketsHandler(),
            ["netease"] = CreateSocketsHandler(),
            ["kugou"] = CreateSocketsHandler()
        };

    private static SocketsHttpHandler CreateSocketsHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(6),
        EnableMultipleHttp2Connections = true
    };
}
