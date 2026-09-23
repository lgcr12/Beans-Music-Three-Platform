using System.Net;
using System.Net.Http.Headers;

namespace Beans.Windows.Rebuild.Services.Downloads;

/// <summary>
/// Production-safe boundary until a provider explicitly supplies an authorized
/// offline-download source. Playback URLs are intentionally not accepted here.
/// </summary>
public sealed class UnsupportedDownloadSourceResolver : IDownloadSourceResolver
{
    public Task<DownloadSourceResult> ResolveAsync(DownloadSourceRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DownloadSourceResult.Failure(
            DownloadFailureCode.OfflineDownloadNotAllowed,
            "当前平台尚未提供已授权的离线下载源"));
    }
}

public sealed class HttpDownloadTransport : IDownloadTransport, IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public HttpDownloadTransport() : this(CreateClient(), true) { }

    public HttpDownloadTransport(HttpClient client) : this(client, false) { }

    private HttpDownloadTransport(HttpClient client, bool ownsClient)
    {
        _client = client;
        _ownsClient = ownsClient;
    }

    public async Task<DownloadTransportResponse> OpenReadAsync(DownloadSource source, long offset, CancellationToken cancellationToken)
    {
        if (!source.Uri.IsAbsoluteUri || !string.Equals(source.Uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("下载源必须使用 HTTPS");

        var request = new HttpRequestMessage(HttpMethod.Get, source.Uri);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        if (source.Headers is not null)
        {
            foreach (var (name, value) in source.Headers)
            {
                if (string.IsNullOrWhiteSpace(name) || value.Contains('\r') || value.Contains('\n') ||
                    string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    request.Dispose();
                    throw new InvalidDataException("下载请求头无效");
                }
                if (!request.Headers.TryAddWithoutValidation(name, value))
                {
                    request.Dispose();
                    throw new InvalidDataException("下载请求头无效");
                }
            }
        }

        HttpResponseMessage? response = null;
        try
        {
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
                throw new HttpRequestException("下载服务暂时不可用", null, response.StatusCode);
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var total = response.Content.Headers.ContentRange?.Length ??
                (response.Content.Headers.ContentLength is { } length ? length + (response.StatusCode == HttpStatusCode.PartialContent ? offset : 0) : null);
            return new DownloadTransportResponse(
                stream,
                total,
                response.StatusCode == HttpStatusCode.PartialContent,
                response.Content.Headers.ContentType?.MediaType,
                new ResponseOwner(request, response));
        }
        catch
        {
            response?.Dispose();
            request.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed class ResponseOwner(HttpRequestMessage request, HttpResponseMessage response) : IDisposable
    {
        public void Dispose()
        {
            response.Dispose();
            request.Dispose();
        }
    }
}

public sealed class SystemDownloadStorageProbe : IDownloadStorageProbe
{
    public Task<long?> GetAvailableBytesAsync(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrWhiteSpace(root)) return Task.FromResult<long?>(null);
            return Task.FromResult<long?>(new DriveInfo(root).AvailableFreeSpace);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Task.FromResult<long?>(null);
        }
    }
}
