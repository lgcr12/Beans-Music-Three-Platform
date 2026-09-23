using System.Net;
using System.Net.Http.Headers;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.PreviewData;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.Platforms;
using Beans.Windows.Rebuild.Tests.Infrastructure;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class NetworkingInfrastructureTests
{
    [Fact]
    public void DefaultPlatformHandlersNeverPersistOrReplayCookies()
    {
        var factoryMethod = typeof(PlatformHttpClientFactory).GetMethod(
            "CreateSocketsHandler",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        using var handler = Assert.IsType<SocketsHttpHandler>(factoryMethod!.Invoke(null, null));

        Assert.False(handler.UseCookies);
    }

    [Fact]
    public void FactoryReusesNamedClientsAndKeepsPlatformsIndependent()
    {
        var handlers = new Dictionary<string, HttpMessageHandler>
        {
            ["qq"] = Handler(HttpStatusCode.OK),
            ["netease"] = Handler(HttpStatusCode.OK),
            ["kugou"] = Handler(HttpStatusCode.OK)
        };
        using var factory = new PlatformHttpClientFactory(handlers, new CaptureSafeLogger(), new SensitiveDataRedactor(), new PlatformErrorMapper());
        Assert.Same(factory.Get("qq"), factory.Get("qq"));
        Assert.NotSame(factory.Get("qq"), factory.Get("netease"));
        Assert.Equal(PlatformHttpClientFactory.QqClientName, factory.Get("qq").Name);
        Assert.Equal(3, factory.Get("kugou").MaximumConcurrency);
    }

    [Fact]
    public async Task RequestTimeoutMapsToTimeout()
    {
        using var client = NetworkingTestFactory.Client(new DelayedHttpMessageHandler(TimeSpan.FromMilliseconds(180), NetworkingTestFactory.SuccessJson));
        var response = await client.SendAsync(NetworkingTestFactory.Request(),
            NetworkingTestFactory.Context("qq:timeout", timeout: TimeSpan.FromMilliseconds(35), allowRetry: false), NetworkingTestFactory.Parser());
        Assert.Equal(PlatformErrorCode.Timeout, response.Error!.Code);
    }

    [Fact]
    public async Task CallerCancellationMapsToCancelledAndDoesNotRetry()
    {
        var handler = new DelayedHttpMessageHandler(TimeSpan.FromMilliseconds(180), NetworkingTestFactory.SuccessJson);
        using var client = NetworkingTestFactory.Client(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));
        var response = await client.SendAsync(NetworkingTestFactory.Request(),
            NetworkingTestFactory.Context("qq:cancel", cancellation.Token), NetworkingTestFactory.Parser());
        Assert.Equal(PlatformErrorCode.Cancelled, response.Error!.Code);
        Assert.Equal(1, handler.SendCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, PlatformErrorCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, PlatformErrorCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, PlatformErrorCode.NotFound)]
    [InlineData((HttpStatusCode)429, PlatformErrorCode.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, PlatformErrorCode.ServiceUnavailable)]
    public async Task HttpStatusMapsToSafeError(HttpStatusCode status, PlatformErrorCode expected)
    {
        using var client = NetworkingTestFactory.Client(Handler(status));
        var response = await client.SendAsync(NetworkingTestFactory.Request(),
            NetworkingTestFactory.Context($"qq:status-{(int)status}", allowRetry: false), NetworkingTestFactory.Parser());
        Assert.Equal(expected, response.Error!.Code);
        Assert.DoesNotContain("Exception", response.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidJsonMapsToParseFailureWithoutRetry()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeHttpMessageHandler.Json("{")));
        using var client = NetworkingTestFactory.Client(handler);
        var response = await client.SendAsync(NetworkingTestFactory.Request(),
            NetworkingTestFactory.Context("qq:invalid-json"), NetworkingTestFactory.Parser());
        Assert.Equal(PlatformErrorCode.ParseFailure, response.Error!.Code);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task MissingRequiredFieldMapsToInvalidResponse()
    {
        using var client = NetworkingTestFactory.Client(new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(FakeHttpMessageHandler.Json("{\"value\":7,\"items\":[]}"))));
        var response = await client.SendAsync(NetworkingTestFactory.Request(),
            NetworkingTestFactory.Context("qq:missing-required"), NetworkingTestFactory.Parser());
        Assert.Equal(PlatformErrorCode.InvalidResponse, response.Error!.Code);
    }

    [Fact]
    public async Task NoContentMapsToInvalidResponse()
    {
        using var client = NetworkingTestFactory.Client(Handler(HttpStatusCode.NoContent));
        var response = await client.SendAsync(NetworkingTestFactory.Request(),
            NetworkingTestFactory.Context("qq:no-content"), NetworkingTestFactory.Parser());
        Assert.Equal(PlatformErrorCode.InvalidResponse, response.Error!.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task PermanentStatusDoesNotRetry(HttpStatusCode status)
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        using var client = NetworkingTestFactory.Client(handler);
        await client.SendAsync(NetworkingTestFactory.Request(), NetworkingTestFactory.Context($"qq:no-retry-{(int)status}"), NetworkingTestFactory.Parser());
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task ServerFailureRetriesOnlyOnce()
    {
        var handler = new SequenceHttpMessageHandler(
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)),
            _ => Task.FromResult(FakeHttpMessageHandler.Json(NetworkingTestFactory.SuccessJson)));
        using var client = NetworkingTestFactory.Client(handler);
        var response = await client.SendAsync(NetworkingTestFactory.Request(), NetworkingTestFactory.Context("qq:retry-500"), NetworkingTestFactory.Parser());
        Assert.True(response.IsSuccess);
        Assert.Equal(1, response.RetryCount);
        Assert.Equal(2, handler.SendCount);
    }

    [Fact]
    public async Task RetryAfterIsHonoredButStillBoundedToOneRetry()
    {
        var handler = new SequenceHttpMessageHandler(
            _ =>
            {
                var response = new HttpResponseMessage((HttpStatusCode)429);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(response);
            },
            _ => Task.FromResult(FakeHttpMessageHandler.Json(NetworkingTestFactory.SuccessJson)));
        using var client = NetworkingTestFactory.Client(handler);
        var response = await client.SendAsync(NetworkingTestFactory.Request(), NetworkingTestFactory.Context("qq:retry-after"), NetworkingTestFactory.Parser());
        Assert.True(response.IsSuccess);
        Assert.Equal(2, handler.SendCount);
    }

    [Fact]
    public async Task RetryCannotEscapeTotalTimeout()
    {
        var handler = new SequenceHttpMessageHandler(
            async token => { await Task.Delay(25, token); return new HttpResponseMessage(HttpStatusCode.InternalServerError); },
            _ => Task.FromResult(FakeHttpMessageHandler.Json(NetworkingTestFactory.SuccessJson)));
        using var client = NetworkingTestFactory.Client(handler);
        var response = await client.SendAsync(NetworkingTestFactory.Request(),
            NetworkingTestFactory.Context("qq:retry-total-timeout", timeout: TimeSpan.FromMilliseconds(40)), NetworkingTestFactory.Parser());
        Assert.Equal(PlatformErrorCode.Timeout, response.Error!.Code);
        Assert.True(response.Elapsed < TimeSpan.FromMilliseconds(180));
    }

    [Fact]
    public async Task AuthenticatedRequestNeverAutomaticallyRetries()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        using var client = NetworkingTestFactory.Client(handler);
        var context = NetworkingTestFactory.Context("qq:account-read", authenticated: true);
        Assert.False(context.AllowRetry);
        await client.SendAsync(NetworkingTestFactory.Request(), context, NetworkingTestFactory.Parser());
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task SamePlatformConcurrencyNeverExceedsConfiguredLimit()
    {
        var handler = new FakeHttpMessageHandler(async (_, token) =>
        {
            await Task.Delay(45, token);
            return FakeHttpMessageHandler.Json(NetworkingTestFactory.SuccessJson);
        });
        using var client = NetworkingTestFactory.Client(handler, maximumConcurrency: 3);
        var tasks = Enumerable.Range(0, 9).Select(index => client.SendAsync(
            NetworkingTestFactory.Request($"https://example.test/v1/{index}"),
            NetworkingTestFactory.Context($"qq:parallel-{index}"), NetworkingTestFactory.Parser()));
        await Task.WhenAll(tasks);
        Assert.InRange(handler.MaximumObservedConcurrency, 2, 3);
    }

    [Fact]
    public async Task DifferentPlatformsDoNotBlockEachOther()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var qqEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neteaseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var qq = NetworkingTestFactory.Client(new FakeHttpMessageHandler(async (_, token) =>
        {
            qqEntered.TrySetResult();
            await release.Task.WaitAsync(token);
            return FakeHttpMessageHandler.Json(NetworkingTestFactory.SuccessJson);
        }), "qq", 1);
        using var netease = NetworkingTestFactory.Client(new FakeHttpMessageHandler(async (_, token) =>
        {
            neteaseEntered.TrySetResult();
            await release.Task.WaitAsync(token);
            return FakeHttpMessageHandler.Json(NetworkingTestFactory.SuccessJson);
        }), "netease", 1);

        var qqTask = qq.SendAsync(NetworkingTestFactory.Request(), NetworkingTestFactory.Context("qq:independent"), NetworkingTestFactory.Parser());
        var neteaseTask = netease.SendAsync(NetworkingTestFactory.Request(), NetworkingTestFactory.Context("netease:independent", platformId: "netease"), NetworkingTestFactory.Parser());
        await Task.WhenAll(qqEntered.Task, neteaseEntered.Task).WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        release.TrySetResult();
        Assert.All(await Task.WhenAll(qqTask, neteaseTask), result => Assert.True(result.IsSuccess));
    }

    [Fact]
    public async Task HandlerExceptionDoesNotLeakSemaphorePermit()
    {
        var count = 0;
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            if (Interlocked.Increment(ref count) == 1) throw new HttpRequestException("fixture failure");
            return Task.FromResult(FakeHttpMessageHandler.Json(NetworkingTestFactory.SuccessJson));
        });
        using var client = NetworkingTestFactory.Client(handler, maximumConcurrency: 1);
        var first = await client.SendAsync(NetworkingTestFactory.Request(), NetworkingTestFactory.Context("qq:throw", allowRetry: false), NetworkingTestFactory.Parser());
        var second = await client.SendAsync(NetworkingTestFactory.Request(), NetworkingTestFactory.Context("qq:after-throw", allowRetry: false), NetworkingTestFactory.Parser());
        Assert.False(first.IsSuccess);
        Assert.True(second.IsSuccess);
    }

    [Fact]
    public async Task CancellingSemaphoreWaitDoesNotLeakPermitOrEnterNetwork()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHttpMessageHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/hold")
            {
                firstEntered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return FakeHttpMessageHandler.Json(NetworkingTestFactory.SuccessJson);
        });
        using var client = NetworkingTestFactory.Client(handler, maximumConcurrency: 1);
        var first = client.SendAsync(NetworkingTestFactory.Request("https://example.test/hold"), NetworkingTestFactory.Context("qq:hold"), NetworkingTestFactory.Parser());
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));
        var cancelled = await client.SendAsync(NetworkingTestFactory.Request("https://example.test/wait"), NetworkingTestFactory.Context("qq:wait", cancellation.Token), NetworkingTestFactory.Parser());
        Assert.Equal(PlatformErrorCode.Cancelled, cancelled.Error!.Code);
        Assert.Equal(1, handler.SendCount);
        release.TrySetResult();
        await first;
        var third = await client.SendAsync(NetworkingTestFactory.Request("https://example.test/after"), NetworkingTestFactory.Context("qq:after-wait"), NetworkingTestFactory.Parser());
        Assert.True(third.IsSuccess);
        Assert.Equal(2, handler.SendCount);
    }

    [Fact]
    public async Task SameRequestKeyIsSentOnlyOnce()
    {
        var handler = new DelayedHttpMessageHandler(TimeSpan.FromMilliseconds(70), NetworkingTestFactory.SuccessJson);
        using var client = NetworkingTestFactory.Client(handler);
        var first = client.SendAsync(NetworkingTestFactory.Request(), NetworkingTestFactory.Context("qq:merged"), NetworkingTestFactory.Parser());
        var second = client.SendAsync(NetworkingTestFactory.Request(), NetworkingTestFactory.Context("qq:merged"), NetworkingTestFactory.Parser());
        Assert.All(await Task.WhenAll(first, second), result => Assert.True(result.IsSuccess));
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task DifferentAccountIdentityHashesNeverShareARequest()
    {
        var handler = new FakeHttpMessageHandler(async (_, token) =>
        {
            await Task.Delay(30, token);
            return FakeHttpMessageHandler.Json(NetworkingTestFactory.SuccessJson);
        });
        using var client = NetworkingTestFactory.Client(handler);
        var first = client.SendAsync(NetworkingTestFactory.Request(),
            NetworkingTestFactory.ContextForAccount("qq:account-discovery", "account-a"), NetworkingTestFactory.Parser());
        var second = client.SendAsync(NetworkingTestFactory.Request(),
            NetworkingTestFactory.ContextForAccount("qq:account-discovery", "account-b"), NetworkingTestFactory.Parser());

        await Task.WhenAll(first, second);

        Assert.Equal(2, handler.SendCount);
    }

    [Fact]
    public async Task OneCancelledWaiterDoesNotCancelOtherWaiter()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHttpMessageHandler(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(80, token);
            return FakeHttpMessageHandler.Json(NetworkingTestFactory.SuccessJson);
        });
        using var client = NetworkingTestFactory.Client(handler);
        using var cancellation = new CancellationTokenSource();
        var first = client.SendAsync(NetworkingTestFactory.Request(), NetworkingTestFactory.Context("qq:shared-cancel", cancellation.Token), NetworkingTestFactory.Parser());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        var second = client.SendAsync(NetworkingTestFactory.Request(), NetworkingTestFactory.Context("qq:shared-cancel"), NetworkingTestFactory.Parser());
        await Task.Delay(10, TestContext.Current.CancellationToken);
        cancellation.Cancel();
        Assert.Equal(PlatformErrorCode.Cancelled, (await first).Error!.Code);
        Assert.True((await second).IsSuccess);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task CompletedRequestIsRemovedFromInflightDictionary()
    {
        using var client = NetworkingTestFactory.Client(Handler(HttpStatusCode.OK));
        await client.SendAsync(NetworkingTestFactory.Request(), NetworkingTestFactory.Context("qq:cleanup"), NetworkingTestFactory.Parser());
        await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Equal(0, client.InFlightRequestCount);
    }

    [Fact]
    public async Task FailedRequestCanBeSentAgain()
    {
        var handler = new SequenceHttpMessageHandler(
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)),
            _ => Task.FromResult(FakeHttpMessageHandler.Json(NetworkingTestFactory.SuccessJson)));
        using var client = NetworkingTestFactory.Client(handler);
        var first = await client.SendAsync(NetworkingTestFactory.Request(), NetworkingTestFactory.Context("qq:repeat-failure", allowRetry: false), NetworkingTestFactory.Parser());
        var second = await client.SendAsync(NetworkingTestFactory.Request(), NetworkingTestFactory.Context("qq:repeat-failure", allowRetry: false), NetworkingTestFactory.Parser());
        Assert.False(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(2, handler.SendCount);
    }

    [Fact]
    public async Task PlatformMismatchFailsBeforeNetwork()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeHttpMessageHandler.Json(NetworkingTestFactory.SuccessJson)));
        using var client = NetworkingTestFactory.Client(handler, "qq");
        var response = await client.SendAsync(NetworkingTestFactory.Request(),
            NetworkingTestFactory.Context("netease:mismatch", platformId: "netease"), NetworkingTestFactory.Parser());
        Assert.Equal(PlatformErrorCode.SecurityFailure, response.Error!.Code);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public void RequestKeyRejectsSensitiveMaterialAndRequiresPlatformPrefix()
    {
        Assert.Throws<ArgumentException>(() => NetworkingTestFactory.Context("qq:token=secret"));
        Assert.Throws<ArgumentException>(() => NetworkingTestFactory.Context("qq:api-key=value"));
        Assert.Throws<ArgumentException>(() => NetworkingTestFactory.Context("qq:sign=value"));
        Assert.Throws<ArgumentException>(() => NetworkingTestFactory.Context("netease:rankings"));
    }

    [Fact]
    public void AccountIdentityHashIsDeterministicAndDoesNotExposeInput()
    {
        var first = AccountIdentityHasher.Hash("user@example.test");
        var second = AccountIdentityHasher.Hash("user@example.test");
        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.DoesNotContain("user", first, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(first, AccountIdentityHasher.Hash("another@example.test"));
    }

    [Fact]
    public async Task SafeLoggingNeverReceivesQueryString()
    {
        var logger = new CaptureSafeLogger();
        using var client = NetworkingTestFactory.Client(Handler(HttpStatusCode.OK), logger: logger);
        await client.SendAsync(NetworkingTestFactory.Request("https://example.test/v1/items?token=secret&uin=123"),
            NetworkingTestFactory.Context("qq:safe-log"), NetworkingTestFactory.Parser());
        var entry = Assert.Single(logger.Started);
        Assert.Equal("example.test", entry.Host);
        Assert.Equal("/v1/items", entry.Path);
        Assert.DoesNotContain("secret", entry.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("cookie=session-value")]
    [InlineData("token=token-value")]
    [InlineData("password=pass-value")]
    [InlineData("Authorization: Bearer abc.def")]
    [InlineData("signature=private-sign")]
    public void RedactorRemovesSensitiveValues(string input)
    {
        var redacted = new SensitiveDataRedactor().Redact(input);
        Assert.Contains("[REDACTED]", redacted);
        Assert.DoesNotContain(input.Split('=', ':').Last().Trim(), redacted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResponseSizeLimitReturnsSecurityFailure()
    {
        var oversized = "{\"required\":\"" + new string('x', 1800) + "\",\"value\":1}";
        using var client = NetworkingTestFactory.Client(new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeHttpMessageHandler.Json(oversized))));
        var response = await client.SendAsync(NetworkingTestFactory.Request(), NetworkingTestFactory.Context("qq:size-limit"), NetworkingTestFactory.Parser(1024));
        Assert.Equal(PlatformErrorCode.SecurityFailure, response.Error!.Code);
    }

    [Theory]
    [InlineData(DiscoveryDataOrigin.Live, false)]
    [InlineData(DiscoveryDataOrigin.CacheFresh, false)]
    [InlineData(DiscoveryDataOrigin.CacheStale, true)]
    [InlineData(DiscoveryDataOrigin.Preview, false)]
    public void ResponsePreservesDataOrigin(DiscoveryDataOrigin origin, bool stale)
    {
        var response = PlatformResponse<string>.Success("value", "correlation", TimeSpan.Zero, origin: origin, isStale: stale);
        Assert.Equal(origin, response.DataOrigin);
        Assert.Equal(stale, response.IsStale);
    }

    [Fact]
    public void PreviewContentIsExplicitAndBuildPolicyIsDeterministic()
    {
        var preview = QqDiscoveryPreviewData.Create();
        Assert.Equal(DiscoveryDataOrigin.Preview, preview.DataOrigin);
        Assert.True(preview.IsPreview);
        Assert.Equal("预览内容 · 在线服务尚未连接", preview.SafeStatusText);
        Assert.False(new FixedPreviewModePolicy(false).IsEnabled);
#if DEBUG
        Assert.True(new BuildPreviewModePolicy().IsEnabled);
#else
        Assert.False(new BuildPreviewModePolicy().IsEnabled);
#endif
    }

    [Fact]
    public async Task StubAdapterReturnsExplicitUnsupportedError()
    {
        var adapter = new StubPlatformDiscoveryAdapter("qq", new(true, true, true, true, false, true), new PlatformErrorMapper());
        var response = await adapter.GetPublicRankingsAsync(NetworkingTestFactory.Context("qq:stub-rankings"), TestContext.Current.CancellationToken);
        Assert.Equal(PlatformErrorCode.Unsupported, response.Error!.Code);
        Assert.False(response.IsSuccess);
    }

    [Fact]
    public async Task FixtureAndFakeAdaptersStayInsideTestBoundary()
    {
        var content = QqDiscoveryPreviewData.Create();
        var fixture = new FixtureDiscoveryAdapter(content);
        var fake = new FakeDiscoveryAdapter(content);
        var context = NetworkingTestFactory.Context("qq:fixture-rankings", TestContext.Current.CancellationToken);
        Assert.True((await fixture.GetPublicRankingsAsync(context, TestContext.Current.CancellationToken)).IsSuccess);
        await fake.GetPublicRankingsAsync(context, TestContext.Current.CancellationToken);
        Assert.Equal(1, fake.RankingCallCount);
    }

    [Fact]
    public async Task JsonFixturesAreStableAndContainNoSensitiveFields()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Networking", "success-response.json");
        var text = await File.ReadAllTextAsync(fixturePath, TestContext.Current.CancellationToken);
        var lower = text.ToLowerInvariant();
        Assert.DoesNotContain("cookie", lower);
        Assert.DoesNotContain("token", lower);
        Assert.DoesNotContain("authorization", lower);
        await using var stream = File.OpenRead(fixturePath);
        var parsed = await new PlatformJsonSerializer().DeserializeAsync<FixtureDto>(stream, TestContext.Current.CancellationToken);
        Assert.True(parsed.IsSuccess);
    }

    [Fact]
    public void CorrelationIdsAreUniquePerLogicalOperation()
    {
        var first = NetworkingTestFactory.Context("qq:correlation-one");
        var second = NetworkingTestFactory.Context("qq:correlation-two");
        Assert.NotEqual(first.CorrelationId, second.CorrelationId);
    }

    private static FakeHttpMessageHandler Handler(HttpStatusCode status) => new((_, _) => Task.FromResult(
        status == HttpStatusCode.OK
            ? FakeHttpMessageHandler.Json(NetworkingTestFactory.SuccessJson)
            : new HttpResponseMessage(status)));
}
