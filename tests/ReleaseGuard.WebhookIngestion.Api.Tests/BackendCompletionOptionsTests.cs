using Microsoft.Extensions.Options;
using ReleaseGuard.WebhookIngestion.Api;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class BackendCompletionOptionsTests
{
    [Fact]
    public void MetricsExport_DefaultsAreDisabledBoundedAndValid()
    {
        var options = new AiExplanationMetricsExportOptions();

        AiExplanationMetricsExportOptions.ThrowIfInvalid(options);
        Assert.False(options.Enabled);
        Assert.Null(options.Endpoint);
        Assert.Null(options.Protocol);
        Assert.Equal(60_000, options.ExportIntervalMilliseconds);
        Assert.Equal(10_000, options.ExportTimeoutMilliseconds);
    }

    [Theory]
    [InlineData("grpc", "http://collector:4317")]
    [InlineData("http/protobuf", "https://collector.example/v1/metrics")]
    public void MetricsExport_EnabledAcceptsExplicitSupportedTransport(
        string protocol,
        string endpoint)
    {
        var options = new AiExplanationMetricsExportOptions
        {
            Enabled = true,
            Protocol = protocol,
            Endpoint = endpoint,
            ExportIntervalMilliseconds = 1_000,
            ExportTimeoutMilliseconds = 1_000
        };

        var result = new AiExplanationMetricsExportOptionsValidator()
            .Validate(null, options);

        Assert.True(result.Succeeded);
        Assert.Equal(new Uri(endpoint),
            AiExplanationMetricsExportOptions.GetEndpoint(options));
    }

    [Theory]
    [InlineData(null, "http://collector:4317", 1_000, 100)]
    [InlineData("udp", "http://collector:4317", 1_000, 100)]
    [InlineData("grpc", null, 1_000, 100)]
    [InlineData("grpc", "http://user:secret@collector:4317", 1_000, 100)]
    [InlineData("http/protobuf", "http://collector:4318", 1_000, 100)]
    [InlineData("grpc", "http://collector:4317", 999, 100)]
    [InlineData("grpc", "http://collector:4317", 1_000, 1_001)]
    public void MetricsExport_RejectsMissingUnsafeOrUnboundedSettings(
        string? protocol,
        string? endpoint,
        int interval,
        int timeout)
    {
        var options = new AiExplanationMetricsExportOptions
        {
            Enabled = true,
            Protocol = protocol,
            Endpoint = endpoint,
            ExportIntervalMilliseconds = interval,
            ExportTimeoutMilliseconds = timeout
        };

        var result = new AiExplanationMetricsExportOptionsValidator()
            .Validate(null, options);

        Assert.True(result.Failed);
        Assert.Throws<OptionsValidationException>(
            () => AiExplanationMetricsExportOptions.ThrowIfInvalid(options));
    }

    [Fact]
    public void MetricsExport_EnabledInvalidHostConfigurationFailsFast()
    {
        using var application = new TestApplicationFactory(
            TestApplicationFactory.AiExplanationQueryCredential,
            metricsExportEnabled: true,
            metricsExportEndpoint: null,
            metricsExportProtocol:
                AiExplanationMetricsExportOptions.GrpcProtocol);

        var exception = Assert.Throws<OptionsValidationException>(
            () => application.CreateClient());

        Assert.Contains(
            AiExplanationMetricsExportOptions.SectionName,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MetricsExport_OptInPushesOnlyConfiguredMeterOverHttpProtobuf()
    {
        var port = ReserveTcpPort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var application = new TestApplicationFactory(
            TestApplicationFactory.AiExplanationQueryCredential,
            metricsExportEnabled: true,
            metricsExportEndpoint:
                $"http://127.0.0.1:{port}/v1/metrics",
            metricsExportProtocol:
                AiExplanationMetricsExportOptions.HttpProtobufProtocol,
            metricsExportIntervalMilliseconds: 1_000,
            metricsExportTimeoutMilliseconds: 1_000,
            useProductionMetrics: true);
        using var client = application.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/v1/release-risk-events/{Guid.NewGuid():D}/ai-explanation");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TestApplicationFactory.AiExplanationQueryCredential);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var context = await listener.GetContextAsync().WaitAsync(
            TimeSpan.FromSeconds(10));
        await using var body = new MemoryStream();
        await context.Request.InputStream.CopyToAsync(body);
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.Close();
        var protobufText = Encoding.UTF8.GetString(body.ToArray());

        Assert.Equal("/v1/metrics", context.Request.RawUrl);
        Assert.Contains(
            AiExplanationQueryMetrics.MeterName,
            protobufText,
            StringComparison.Ordinal);
        Assert.Contains(
            AiExplanationQueryMetrics.RateLimitPermitsInstrumentName,
            protobufText,
            StringComparison.Ordinal);
        Assert.Contains(
            AiExplanationQueryMetrics.OutcomesInstrumentName,
            protobufText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReplayOptions_DefaultsAreBoundedAndValid()
    {
        var options = new AiExplanationReplayOptions();

        AiExplanationReplayOptions.ThrowIfInvalid(options);
        Assert.Equal(5_000, options.RequestTimeoutMilliseconds);
        Assert.Equal(10, options.PermitLimit);
        Assert.Equal(60_000, options.WindowMilliseconds);
    }

    [Theory]
    [InlineData(99, 10, 60_000)]
    [InlineData(5_000, 0, 60_000)]
    [InlineData(5_000, 10, 99)]
    [InlineData(30_001, 10, 60_000)]
    [InlineData(5_000, 1_001, 60_000)]
    [InlineData(5_000, 10, 3_600_001)]
    public void ReplayOptions_RejectUnsafeBounds(
        int timeout,
        int permits,
        int window)
    {
        var options = new AiExplanationReplayOptions
        {
            RequestTimeoutMilliseconds = timeout,
            PermitLimit = permits,
            WindowMilliseconds = window
        };

        Assert.True(new AiExplanationReplayOptionsValidator()
            .Validate(null, options).Failed);
        Assert.Throws<OptionsValidationException>(
            () => AiExplanationReplayOptions.ThrowIfInvalid(options));
    }

    [Fact]
    public void ReplayAuthentication_RequiresDistinctBoundedCredentials()
    {
        var valid = new AiExplanationReplayAuthenticationOptions
        {
            ActiveCredential =
                TestApplicationFactory.AiExplanationReplayCredential,
            PreviousCredential =
                TestApplicationFactory.PreviousAiExplanationQueryCredential
        };
        var invalid = new AiExplanationReplayAuthenticationOptions
        {
            ActiveCredential =
                TestApplicationFactory.AiExplanationReplayCredential,
            PreviousCredential =
                TestApplicationFactory.AiExplanationReplayCredential
        };

        Assert.True(new AiExplanationReplayAuthenticationOptionsValidator()
            .Validate(null, valid).Succeeded);
        Assert.True(new AiExplanationReplayAuthenticationOptionsValidator()
            .Validate(null, invalid).Failed);
    }

    [Fact]
    public void ReplayAuthentication_AcceptsActiveAndPreviousButNotQueryCredential()
    {
        using var authenticator = new AiExplanationReplayAuthenticator(
            Options.Create(new AiExplanationReplayAuthenticationOptions
            {
                ActiveCredential =
                    TestApplicationFactory.AiExplanationReplayCredential,
                PreviousCredential =
                    TestApplicationFactory.PreviousAiExplanationQueryCredential
            }));

        Assert.True(authenticator.IsAuthorized(
            $"Bearer {TestApplicationFactory.AiExplanationReplayCredential}"));
        Assert.True(authenticator.IsAuthorized(
            $"Bearer {TestApplicationFactory.PreviousAiExplanationQueryCredential}"));
        Assert.False(authenticator.IsAuthorized(
            $"Bearer {TestApplicationFactory.AiExplanationQueryCredential}"));
        Assert.False(authenticator.IsAuthorized("Bearer wrong-credential"));
    }

    [Fact]
    public void ReplayRateLimit_UsesBoundedGlobalFixedWindow()
    {
        var time = new ManualTimeProvider();
        var boundary = new AiExplanationReplayRateLimitBoundary(
            Options.Create(new AiExplanationReplayOptions
            {
                PermitLimit = 1,
                WindowMilliseconds = 1_000
            }),
            time);

        Assert.True(boundary.AttemptAcquire().IsAcquired);
        var rejected = boundary.AttemptAcquire();
        Assert.False(rejected.IsAcquired);
        Assert.Equal(1, rejected.RetryAfterSeconds);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(boundary.AttemptAcquire().IsAcquired);
    }

    [Fact]
    public void RetentionDefaults_AreDisabledBoundedAndValid()
    {
        var options = new RetentionCleanupOptions();

        RetentionCleanupOptions.ThrowIfInvalid(options);
        Assert.False(options.Enabled);
        Assert.True(
            options.AcceptedDeliveryRetentionHours >=
            options.PublishedOutboxRetentionHours);
    }

    [Theory]
    [InlineData(0, 3_600_000, 168, 720, 168, 10_000)]
    [InlineData(100, 999, 168, 720, 168, 10_000)]
    [InlineData(100, 3_600_000, 0, 720, 168, 10_000)]
    [InlineData(100, 3_600_000, 168, 100, 168, 10_000)]
    [InlineData(100, 3_600_000, 168, 720, 168, 30_001)]
    public void RetentionOptions_RejectUnsafeBounds(
        int batchSize,
        int poll,
        int outboxHours,
        int acceptedHours,
        int ignoredHours,
        int timeout)
    {
        var options = new RetentionCleanupOptions
        {
            BatchSize = batchSize,
            PollIntervalMilliseconds = poll,
            PublishedOutboxRetentionHours = outboxHours,
            AcceptedDeliveryRetentionHours = acceptedHours,
            IgnoredDeliveryRetentionHours = ignoredHours,
            CleanupTimeoutMilliseconds = timeout
        };

        Assert.True(new RetentionCleanupOptionsValidator()
            .Validate(null, options).Failed);
        Assert.Throws<OptionsValidationException>(
            () => RetentionCleanupOptions.ThrowIfInvalid(options));
    }


    private static int ReserveTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
