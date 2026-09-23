using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Networking;
using Xunit;

namespace Beans.Windows.Rebuild.Tests.Infrastructure;

internal sealed class FakeHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    private int _sendCount;
    private int _currentConcurrency;
    private int _maximumObservedConcurrency;

    public int SendCount => Volatile.Read(ref _sendCount);
    public int MaximumObservedConcurrency => Volatile.Read(ref _maximumObservedConcurrency);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _sendCount);
        var current = Interlocked.Increment(ref _currentConcurrency);
        UpdateMaximum(current);
        try { return await responder(request, cancellationToken); }
        finally { Interlocked.Decrement(ref _currentConcurrency); }
    }

    private void UpdateMaximum(int current)
    {
        while (true)
        {
            var observed = Volatile.Read(ref _maximumObservedConcurrency);
            if (current <= observed || Interlocked.CompareExchange(ref _maximumObservedConcurrency, current, observed) == observed) return;
        }
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}

internal sealed class SequenceHttpMessageHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<CancellationToken, Task<HttpResponseMessage>>> _responses;
    private int _sendCount;

    public SequenceHttpMessageHandler(params Func<CancellationToken, Task<HttpResponseMessage>>[] responses) => _responses = new(responses);
    public int SendCount => Volatile.Read(ref _sendCount);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _sendCount);
        if (!_responses.TryDequeue(out var response)) throw new InvalidOperationException("No fixture response remains.");
        return response(cancellationToken);
    }
}

internal sealed class DelayedHttpMessageHandler(TimeSpan delay, string json) : HttpMessageHandler
{
    private int _sendCount;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int SendCount => Volatile.Read(ref _sendCount);
    public Task Started => _started.Task;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _sendCount);
        _started.TrySetResult();
        await Task.Delay(delay, cancellationToken);
        return FakeHttpMessageHandler.Json(json);
    }
}

internal sealed class CaptureSafeLogger : ISafeLogger
{
    public ConcurrentQueue<SafePlatformLogEvent> Started { get; } = [];
    public ConcurrentQueue<SafePlatformLogEvent> Completed { get; } = [];
    public void RequestStarted(SafePlatformLogEvent entry) => Started.Enqueue(entry);
    public void RequestCompleted(SafePlatformLogEvent entry) => Completed.Enqueue(entry);
}

internal sealed record FixtureDto(string? Required, int Value, IReadOnlyList<string>? Items);
internal sealed record FixtureModel(string Required, int Value, IReadOnlyList<string> Items);

internal static class NetworkingTestFactory
{
    public const string SuccessJson = "{\"required\":\"fixture\",\"value\":\"42\",\"items\":[\"alpha\",\"beta\"]}";

    public static PlatformHttpClient Client(
        HttpMessageHandler handler,
        string platformId = "qq",
        int maximumConcurrency = 3,
        CaptureSafeLogger? logger = null) =>
        new(
            new HttpClient(handler, true),
            new PlatformHttpClientOptions($"Beans.Platform.{platformId}", platformId, maximumConcurrency, TimeSpan.Zero),
            logger ?? new CaptureSafeLogger(),
            new SensitiveDataRedactor(),
            new PlatformErrorMapper());

    public static IPlatformResponseParser<FixtureModel> Parser(int maximumBytes = 1024 * 1024)
    {
        var serializer = new PlatformJsonSerializer(maximumBytes);
        return new JsonPlatformResponseParser<FixtureDto, FixtureModel>(serializer, dto =>
            string.IsNullOrWhiteSpace(dto.Required)
                ? PlatformParseResult<FixtureModel>.Failure(PlatformErrorCode.InvalidResponse, "必需字段缺失")
                : PlatformParseResult<FixtureModel>.Success(new FixtureModel(dto.Required, dto.Value, dto.Items ?? [])));
    }

    public static PlatformRequestContext Context(
        string requestKey,
        string platformId = "qq",
        TimeSpan? timeout = null,
        bool allowRetry = true,
        bool authenticated = false) =>
        Context(requestKey, TestContext.Current.CancellationToken, platformId, timeout, allowRetry, authenticated);

    public static PlatformRequestContext Context(
        string requestKey,
        CancellationToken cancellationToken,
        string platformId = "qq",
        TimeSpan? timeout = null,
        bool allowRetry = true,
        bool authenticated = false) =>
        new(platformId, "fixture-read", requestKey, timeout ?? TimeSpan.FromSeconds(2), authenticated,
            PlatformCachePolicy.NetworkOnly, authenticated ? AccountIdentityHasher.Hash("fixture-account") : AccountIdentityHasher.Anonymous,
            allowRetry, 1, cancellationToken);

    public static PlatformRequestContext ContextForAccount(string requestKey, string accountIdentity) =>
        new("qq", "fixture-read", requestKey, TimeSpan.FromSeconds(2), true,
            PlatformCachePolicy.NetworkOnly, AccountIdentityHasher.Hash(accountIdentity), false, 0,
            TestContext.Current.CancellationToken);

    public static Func<HttpRequestMessage> Request(string uri = "https://example.test/v1/discovery") =>
        () => new HttpRequestMessage(HttpMethod.Get, uri);
}
