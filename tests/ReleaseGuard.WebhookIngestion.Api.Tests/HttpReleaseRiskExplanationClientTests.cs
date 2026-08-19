using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class HttpReleaseRiskExplanationClientTests
{
    [Fact]
    public async Task ExplainAsync_SendsExactEnvelopeAndReturnsExplanation()
    {
        var envelope = CreateEnvelope();
        string? observedBody = null;
        string? observedContentType = null;
        PathString observedPath = default;
        string? observedMethod = null;
        await using var server = await DeterministicHttpServer.StartAsync(
            async context =>
            {
                observedMethod = context.Request.Method;
                observedPath = context.Request.Path;
                observedContentType = context.Request.ContentType;
                using var reader = new StreamReader(context.Request.Body);
                observedBody = await reader.ReadToEndAsync();
                await WriteJsonAsync(
                    context,
                    $$"""
                    {
                      "eventId": "{{envelope.EventId:D}}",
                      "summary": "The recorded risk is low.",
                      "recommendations": ["Review the recorded risk factors."]
                    }
                    """);
            });
        var client = CreateClient(server.Client, server.BaseUrl);

        var result = await client.ExplainAsync(envelope, CancellationToken.None);

        Assert.Equal(envelope.EventId, result.EventId);
        Assert.Equal("The recorded risk is low.", result.Summary);
        Assert.Equal(
            ["Review the recorded risk factors."],
            result.Recommendations);
        Assert.Equal(HttpMethods.Post, observedMethod);
        Assert.Equal(
            $"/{HttpReleaseRiskExplanationClient.EndpointPath}",
            observedPath);
        Assert.Equal("application/json; charset=utf-8", observedContentType);
        Assert.Equal(envelope.Serialize(), observedBody);
    }

    [Fact]
    public async Task ExplainAsync_RejectsMalformedJsonResponse()
    {
        await using var server = await DeterministicHttpServer.StartAsync(
            context => WriteJsonAsync(context, "{not-json"));
        var client = CreateClient(server.Client, server.BaseUrl);

        var exception = await Assert.ThrowsAsync<
            ReleaseRiskExplanationContractException>(
            () => client.ExplainAsync(CreateEnvelope(), CancellationToken.None));

        Assert.IsType<System.Text.Json.JsonException>(exception.InnerException);
    }

    [Fact]
    public async Task ExplainAsync_RejectsInvalidResponseContract()
    {
        var envelope = CreateEnvelope();
        await using var server = await DeterministicHttpServer.StartAsync(
            context => WriteJsonAsync(
                context,
                $$"""
                {
                  "eventId": "{{envelope.EventId:D}}",
                  "summary": "",
                  "recommendations": ["Review the change."],
                  "score": 99
                }
                """));
        var client = CreateClient(server.Client, server.BaseUrl);

        await Assert.ThrowsAsync<ReleaseRiskExplanationContractException>(
            () => client.ExplainAsync(envelope, CancellationToken.None));
    }

    [Fact]
    public async Task ExplainAsync_RejectsConflictingResponseEventId()
    {
        var envelope = CreateEnvelope();
        var conflictingEventId = Guid.Parse(
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        await using var server = await DeterministicHttpServer.StartAsync(
            context => WriteJsonAsync(
                context,
                $$"""
                {
                  "eventId": "{{conflictingEventId:D}}",
                  "summary": "The recorded risk is low.",
                  "recommendations": ["Review the change."]
                }
                """));
        var client = CreateClient(server.Client, server.BaseUrl);

        var exception = await Assert.ThrowsAsync<
            ReleaseRiskExplanationEventIdConflictException>(
            () => client.ExplainAsync(envelope, CancellationToken.None));

        Assert.Equal(envelope.EventId, exception.RequestEventId);
        Assert.Equal(conflictingEventId, exception.ResponseEventId);
    }

    [Fact]
    public async Task ExplainAsync_PropagatesNonSuccessStatusWithoutRetry()
    {
        var requestCount = 0;
        await using var server = await DeterministicHttpServer.StartAsync(
            context =>
            {
                Interlocked.Increment(ref requestCount);
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return Task.CompletedTask;
            });
        var client = CreateClient(server.Client, server.BaseUrl);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ExplainAsync(CreateEnvelope(), CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task ExplainAsync_ConvertsConfiguredDeadlineToTimeoutException()
    {
        var requestCount = 0;
        await using var server = await DeterministicHttpServer.StartAsync(
            async context =>
            {
                Interlocked.Increment(ref requestCount);
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            });
        var client = CreateClient(
            server.Client,
            server.BaseUrl,
            AiExplanationClientOptions.MinimumRequestTimeoutMilliseconds);

        await Assert.ThrowsAsync<TimeoutException>(
            () => client.ExplainAsync(CreateEnvelope(), CancellationToken.None));
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task ExplainAsync_PropagatesCallerCancellation()
    {
        var requestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await DeterministicHttpServer.StartAsync(
            async context =>
            {
                requestStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            });
        var client = CreateClient(server.Client, server.BaseUrl);
        using var cancellationSource = new CancellationTokenSource();

        var explanationTask = client.ExplainAsync(
            CreateEnvelope(),
            cancellationSource.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellationSource.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => explanationTask);
        Assert.IsNotType<TimeoutException>(exception);
        Assert.True(cancellationSource.IsCancellationRequested);
    }

    private static HttpReleaseRiskExplanationClient CreateClient(
        HttpClient httpClient,
        string baseUrl,
        int timeoutMilliseconds = 5_000) =>
        new(
            httpClient,
            Options.Create(new AiExplanationClientOptions
            {
                BaseUrl = baseUrl,
                RequestTimeoutMilliseconds = timeoutMilliseconds
            }));

    private static ReleaseRiskOutboxEnvelope CreateEnvelope()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "contracts",
            "release-risk-assessed.v1.example.json");
        return ReleaseRiskOutboxEnvelope.Deserialize(
            File.ReadAllText(fixturePath));
    }

    private static async Task WriteJsonAsync(
        HttpContext context,
        string json)
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(json);
    }

    private sealed class DeterministicHttpServer : IAsyncDisposable
    {
        private readonly IHost _host;

        private DeterministicHttpServer(IHost host)
        {
            _host = host;
            Client = host.GetTestClient();
            BaseUrl = Client.BaseAddress!.AbsoluteUri;
        }

        public HttpClient Client { get; }

        public string BaseUrl { get; }

        public static async Task<DeterministicHttpServer> StartAsync(
            RequestDelegate handler)
        {
            var host = new HostBuilder()
                .ConfigureWebHost(webHost =>
                {
                    webHost.UseTestServer();
                    webHost.Configure(application => application.Run(handler));
                })
                .Build();
            await host.StartAsync();
            return new DeterministicHttpServer(host);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}
