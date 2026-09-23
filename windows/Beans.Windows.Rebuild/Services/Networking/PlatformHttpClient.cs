using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Networking;

public sealed class PlatformHttpClient : IPlatformHttpClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly PlatformHttpClientOptions _options;
    private readonly ISafeLogger _logger;
    private readonly ISensitiveDataRedactor _redactor;
    private readonly IPlatformErrorMapper _errorMapper;
    private readonly SemaphoreSlim _concurrency;
    private readonly ConcurrentDictionary<string, InFlightRequest> _inFlight = new(StringComparer.Ordinal);

    public PlatformHttpClient(
        HttpClient httpClient,
        PlatformHttpClientOptions options,
        ISafeLogger logger,
        ISensitiveDataRedactor redactor,
        IPlatformErrorMapper errorMapper)
    {
        if (options.MaximumConcurrency <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        _httpClient = httpClient;
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _options = options;
        _logger = logger;
        _redactor = redactor;
        _errorMapper = errorMapper;
        _concurrency = new SemaphoreSlim(options.MaximumConcurrency, options.MaximumConcurrency);
    }

    public string Name => _options.Name;
    public string PlatformId => _options.PlatformId;
    public int MaximumConcurrency => _options.MaximumConcurrency;
    public int InFlightRequestCount => _inFlight.Count;

    public async Task<PlatformResponse<T>> SendAsync<T>(
        Func<HttpRequestMessage> requestFactory,
        PlatformRequestContext context,
        IPlatformResponseParser<T> parser)
    {
        return await SendCoreAsync(
            requestFactory,
            context,
            async (response, cancellationToken) =>
            {
                if (response.StatusCode == HttpStatusCode.NoContent)
                    return PlatformParseResult<T>.Failure(PlatformErrorCode.InvalidResponse, "平台响应没有内容");

                await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await parser.ParseAsync(responseStream, cancellationToken);
            });
    }

    public async Task<PlatformResponse<HttpStatusCode>> SendHeadersAsync(
        Func<HttpRequestMessage> requestFactory,
        PlatformRequestContext context) =>
        await SendCoreAsync(
            requestFactory,
            context,
            (response, _) => Task.FromResult(PlatformParseResult<HttpStatusCode>.Success(response.StatusCode)));

    private async Task<PlatformResponse<T>> SendCoreAsync<T>(
        Func<HttpRequestMessage> requestFactory,
        PlatformRequestContext context,
        Func<HttpResponseMessage, CancellationToken, Task<PlatformParseResult<T>>> responseParser)
    {
        if (!string.Equals(context.PlatformId, PlatformId, StringComparison.Ordinal))
        {
            var mismatch = _errorMapper.FromCode(context.PlatformId, context.CorrelationId, PlatformErrorCode.SecurityFailure,
                "请求平台与客户端不匹配");
            return PlatformResponse<T>.Failure(mismatch, TimeSpan.Zero);
        }

        var mergeKey = $"{context.RequestKey}|{context.AccountIdentityHash}|{typeof(T).FullName}";
        while (true)
        {
            var candidate = new InFlightRequest(token => ExecuteBoxedAsync(requestFactory, context, responseParser, token));
            var active = _inFlight.GetOrAdd(mergeKey, candidate);
            if (!ReferenceEquals(active, candidate)) candidate.Dispose();
            var task = active.Task;
            _ = task.ContinueWith(
                _ => RemoveCompleted(mergeKey, active),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            try
            {
                return (PlatformResponse<T>)await active.WaitAsync(context.CancellationToken);
            }
            catch (InFlightRequestAbandonedException)
            {
                RemoveCompleted(mergeKey, active);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                if (!active.AcceptsWaiters) RemoveCompleted(mergeKey, active);
                var cancelled = _errorMapper.FromException(context.PlatformId, context.CorrelationId,
                    new OperationCanceledException(), false, true);
                return PlatformResponse<T>.Failure(cancelled, TimeSpan.Zero);
            }
        }
    }

    private async Task<object> ExecuteBoxedAsync<T>(
        Func<HttpRequestMessage> requestFactory,
        PlatformRequestContext context,
        Func<HttpResponseMessage, CancellationToken, Task<PlatformParseResult<T>>> responseParser,
        CancellationToken sharedCancellationToken) =>
        await ExecuteCoreAsync(requestFactory, context, responseParser, sharedCancellationToken);

    private async Task<PlatformResponse<T>> ExecuteCoreAsync<T>(
        Func<HttpRequestMessage> requestFactory,
        PlatformRequestContext context,
        Func<HttpResponseMessage, CancellationToken, Task<PlatformParseResult<T>>> responseParser,
        CancellationToken sharedCancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeoutCancellation = new CancellationTokenSource(context.Timeout);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            sharedCancellationToken, timeoutCancellation.Token);
        var token = operationCancellation.Token;
        var acquired = false;
        var retryCount = 0;
        Uri? lastUri = null;

        try
        {
            await _concurrency.WaitAsync(token);
            acquired = true;

            while (true)
            {
                token.ThrowIfCancellationRequested();
                using var request = requestFactory();
                lastUri = request.RequestUri;
                if (lastUri is null)
                {
                    var invalid = _errorMapper.FromCode(PlatformId, context.CorrelationId, PlatformErrorCode.InvalidRequest);
                    return Complete(PlatformResponse<T>.Failure(invalid, stopwatch.Elapsed, retryCount), context, lastUri, retryCount);
                }

                var safeUri = _redactor.SafeUriParts(lastUri);
                _logger.RequestStarted(LogEntry(context, safeUri, null, null, stopwatch.Elapsed, retryCount));

                try
                {
                    using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    if (!response.IsSuccessStatusCode)
                    {
                        var statusError = _errorMapper.FromStatus(PlatformId, context.CorrelationId, response.StatusCode);
                        if (ShouldRetry(context, statusError, retryCount))
                        {
                            retryCount++;
                            await DelayBeforeRetryAsync(response, retryCount, token);
                            continue;
                        }
                        return Complete(PlatformResponse<T>.Failure(statusError, stopwatch.Elapsed, retryCount, response.StatusCode), context, lastUri, retryCount);
                    }

                    var parsed = await responseParser(response, token);
                    if (!parsed.IsSuccess || parsed.Value is null)
                    {
                        var parseError = _errorMapper.FromCode(PlatformId, context.CorrelationId,
                            parsed.ErrorCode ?? PlatformErrorCode.ParseFailure, parsed.SafeMessage);
                        return Complete(PlatformResponse<T>.Failure(parseError, stopwatch.Elapsed, retryCount, response.StatusCode), context, lastUri, retryCount);
                    }

                    return Complete(PlatformResponse<T>.Success(parsed.Value, context.CorrelationId, stopwatch.Elapsed,
                        retryCount, DiscoveryDataOrigin.Live, response.StatusCode, context.RequestKey), context, lastUri, retryCount);
                }
                catch (HttpRequestException exception)
                {
                    var networkError = _errorMapper.FromException(PlatformId, context.CorrelationId, exception, false, false);
                    if (ShouldRetry(context, networkError, retryCount))
                    {
                        retryCount++;
                        await DelayBeforeRetryAsync(null, retryCount, token);
                        continue;
                    }
                    return Complete(PlatformResponse<T>.Failure(networkError, stopwatch.Elapsed, retryCount), context, lastUri, retryCount);
                }
            }
        }
        catch (OperationCanceledException exception)
        {
            var timedOut = timeoutCancellation.IsCancellationRequested && !sharedCancellationToken.IsCancellationRequested;
            var error = _errorMapper.FromException(PlatformId, context.CorrelationId, exception, timedOut, !timedOut);
            return Complete(PlatformResponse<T>.Failure(error, stopwatch.Elapsed, retryCount), context, lastUri, retryCount);
        }
        catch (Exception exception)
        {
            var error = _errorMapper.FromException(PlatformId, context.CorrelationId, exception, false, false);
            return Complete(PlatformResponse<T>.Failure(error, stopwatch.Elapsed, retryCount), context, lastUri, retryCount);
        }
        finally
        {
            if (acquired) _concurrency.Release();
        }
    }

    private PlatformResponse<T> Complete<T>(PlatformResponse<T> result, PlatformRequestContext context, Uri? uri, int retryCount)
    {
        var safeUri = _redactor.SafeUriParts(uri);
        _logger.RequestCompleted(LogEntry(context, safeUri, result.Error?.Code, result.StatusCode,
            result.Elapsed, retryCount, result.DataOrigin));
        return result;
    }

    private static bool ShouldRetry(PlatformRequestContext context, PlatformError error, int retryCount) =>
        context.AllowRetry && error.CanRetry && retryCount < context.MaximumRetries && error.Code is not PlatformErrorCode.Cancelled
            and not PlatformErrorCode.ParseFailure and not PlatformErrorCode.InvalidResponse;

    private async Task DelayBeforeRetryAsync(HttpResponseMessage? response, int retryCount, CancellationToken cancellationToken)
    {
        var retryAfter = response?.Headers.RetryAfter?.Delta;
        var delay = retryAfter is { } serverDelay
            ? TimeSpan.FromMilliseconds(Math.Min(serverDelay.TotalMilliseconds, 2000))
            : _options.EffectiveRetryBaseDelay + TimeSpan.FromMilliseconds(Random.Shared.Next(20, 90) * retryCount);
        if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
    }

    private static SafePlatformLogEvent LogEntry(
        PlatformRequestContext context,
        (string Host, string Path) uri,
        PlatformErrorCode? errorCode,
        HttpStatusCode? statusCode,
        TimeSpan elapsed,
        int retryCount,
        DiscoveryDataOrigin origin = DiscoveryDataOrigin.Live) =>
        new(context.PlatformId, context.OperationName, context.CorrelationId, uri.Host, uri.Path, errorCode,
            statusCode is null ? null : (int)statusCode.Value, (long)elapsed.TotalMilliseconds, retryCount, origin);

    private void RemoveCompleted(string key, InFlightRequest request)
    {
        var entry = new KeyValuePair<string, InFlightRequest>(key, request);
        if (((ICollection<KeyValuePair<string, InFlightRequest>>)_inFlight).Remove(entry))
        {
            request.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var request in _inFlight.Values) request.Cancel();
        _concurrency.Dispose();
        _httpClient.Dispose();
    }

    private sealed class InFlightRequest : IDisposable
    {
        private readonly CancellationTokenSource _sharedCancellation = new();
        private readonly Lazy<Task<object>> _task;
        private readonly object _waiterGate = new();
        private int _waiterCount;
        private bool _acceptsWaiters = true;

        public InFlightRequest(Func<CancellationToken, Task<object>> operation) =>
            _task = new Lazy<Task<object>>(() => operation(_sharedCancellation.Token), LazyThreadSafetyMode.ExecutionAndPublication);

        public Task<object> Task => _task.Value;
        public bool AcceptsWaiters
        {
            get { lock (_waiterGate) return _acceptsWaiters; }
        }

        public async Task<object> WaitAsync(CancellationToken callerCancellationToken)
        {
            lock (_waiterGate)
            {
                if (!_acceptsWaiters) throw new InFlightRequestAbandonedException();
                _waiterCount++;
            }
            try
            {
                return await Task.WaitAsync(callerCancellationToken);
            }
            finally
            {
                var cancelShared = false;
                lock (_waiterGate)
                {
                    _waiterCount--;
                    if (_waiterCount == 0 && !Task.IsCompleted)
                    {
                        _acceptsWaiters = false;
                        cancelShared = true;
                    }
                }
                if (cancelShared) await _sharedCancellation.CancelAsync();
            }
        }

        public void Cancel()
        {
            if (_task.IsValueCreated && _task.Value.IsCompleted) return;
            try { _sharedCancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose() => _sharedCancellation.Dispose();
    }

    private sealed class InFlightRequestAbandonedException : Exception;
}
