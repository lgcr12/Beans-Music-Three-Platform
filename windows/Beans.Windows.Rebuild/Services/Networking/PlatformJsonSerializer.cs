using System.Text.Json;
using System.Text.Json.Serialization;
using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Networking;

public interface IPlatformJsonSerializer
{
    JsonSerializerOptions Options { get; }
    int MaximumResponseBytes { get; }
    Task<PlatformParseResult<T>> DeserializeAsync<T>(Stream stream, CancellationToken cancellationToken);
}

public interface IPlatformResponseParser<T>
{
    Task<PlatformParseResult<T>> ParseAsync(Stream stream, CancellationToken cancellationToken);
}

public sealed class PlatformJsonSerializer : IPlatformJsonSerializer
{
    public PlatformJsonSerializer(int maximumResponseBytes = 1024 * 1024)
    {
        if (maximumResponseBytes < 1024) throw new ArgumentOutOfRangeException(nameof(maximumResponseBytes));
        MaximumResponseBytes = maximumResponseBytes;
        Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };
    }

    public JsonSerializerOptions Options { get; }
    public int MaximumResponseBytes { get; }

    public async Task<PlatformParseResult<T>> DeserializeAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken);
                if (read == 0) break;
                if (buffer.Length + read > MaximumResponseBytes)
                    return PlatformParseResult<T>.Failure(PlatformErrorCode.SecurityFailure, "平台响应超过安全大小限制");
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            }

            var bytes = buffer.ToArray();
            var value = await Task.Run(() => JsonSerializer.Deserialize<T>(bytes, Options), cancellationToken);
            return value is null
                ? PlatformParseResult<T>.Failure(PlatformErrorCode.InvalidResponse, "平台返回的数据为空")
                : PlatformParseResult<T>.Success(value);
        }
        catch (JsonException)
        {
            return PlatformParseResult<T>.Failure(PlatformErrorCode.ParseFailure, "平台响应无法解析");
        }
    }
}

public sealed class JsonPlatformResponseParser<TDto, TModel>(
    IPlatformJsonSerializer serializer,
    Func<TDto, PlatformParseResult<TModel>> map) : IPlatformResponseParser<TModel>
{
    public async Task<PlatformParseResult<TModel>> ParseAsync(Stream stream, CancellationToken cancellationToken)
    {
        var parsed = await serializer.DeserializeAsync<TDto>(stream, cancellationToken);
        return !parsed.IsSuccess || parsed.Value is null
            ? PlatformParseResult<TModel>.Failure(parsed.ErrorCode ?? PlatformErrorCode.ParseFailure, parsed.SafeMessage)
            : map(parsed.Value);
    }
}
