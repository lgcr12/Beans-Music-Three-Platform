using System.Net;
using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Networking;

public interface IPlatformHttpClient
{
    string Name { get; }
    string PlatformId { get; }
    int MaximumConcurrency { get; }
    int InFlightRequestCount { get; }
    Task<PlatformResponse<T>> SendAsync<T>(
        Func<HttpRequestMessage> requestFactory,
        PlatformRequestContext context,
        IPlatformResponseParser<T> parser);
    Task<PlatformResponse<HttpStatusCode>> SendHeadersAsync(
        Func<HttpRequestMessage> requestFactory,
        PlatformRequestContext context);
}

public interface IPlatformHttpClientFactory : IDisposable
{
    IPlatformHttpClient Get(string platformId);
}

public sealed record PlatformHttpClientOptions(
    string Name,
    string PlatformId,
    int MaximumConcurrency = 3,
    TimeSpan? RetryBaseDelay = null)
{
    public TimeSpan EffectiveRetryBaseDelay => RetryBaseDelay ?? TimeSpan.FromMilliseconds(140);
}
