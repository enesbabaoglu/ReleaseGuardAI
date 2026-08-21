using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class TestApplicationFactory : WebApplicationFactory<Program>
{
    public const string GitHubWebhookSecret = "releaseguard-checkpoint-2-test-secret";
    public static string AiExplanationQueryCredential { get; } =
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public static string PreviousAiExplanationQueryCredential { get; } =
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
    public static string AiExplanationReplayCredential { get; } =
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(40));

    private readonly string? _activeAiExplanationQueryCredential;
    private readonly string? _previousAiExplanationQueryCredential;
    private readonly int _rateLimitPermitLimit;
    private readonly int _rateLimitWindowMilliseconds;
    private readonly TimeProvider? _rateLimitTimeProvider;
    private readonly string _activeAiExplanationReplayCredential;
    private readonly string? _previousAiExplanationReplayCredential;
    private readonly int _replayRequestTimeoutMilliseconds;
    private readonly int _replayRateLimitPermitLimit;
    private readonly int _replayRateLimitWindowMilliseconds;
    private readonly bool _metricsExportEnabled;
    private readonly string? _metricsExportEndpoint;
    private readonly string? _metricsExportProtocol;
    private readonly int _metricsExportIntervalMilliseconds;
    private readonly int _metricsExportTimeoutMilliseconds;
    private readonly bool _useProductionMetrics;

    public TestApplicationFactory()
        : this(AiExplanationQueryCredential, previousCredential: null)
    {
    }

    internal TestApplicationFactory(
        string? activeCredential,
        string? previousCredential = null,
        int rateLimitPermitLimit =
            AiExplanationQueryRateLimitOptions.MaximumPermitLimit,
        int rateLimitWindowMilliseconds = 60_000,
        TimeProvider? rateLimitTimeProvider = null,
        string? activeReplayCredential = null,
        string? previousReplayCredential = null,
        int replayRequestTimeoutMilliseconds = 5_000,
        int replayRateLimitPermitLimit = 10,
        int replayRateLimitWindowMilliseconds = 60_000,
        bool metricsExportEnabled = false,
        string? metricsExportEndpoint = null,
        string? metricsExportProtocol = null,
        int metricsExportIntervalMilliseconds = 60_000,
        int metricsExportTimeoutMilliseconds = 10_000,
        bool useProductionMetrics = false)
    {
        _activeAiExplanationQueryCredential = activeCredential;
        _previousAiExplanationQueryCredential = previousCredential;
        _rateLimitPermitLimit = rateLimitPermitLimit;
        _rateLimitWindowMilliseconds = rateLimitWindowMilliseconds;
        _rateLimitTimeProvider = rateLimitTimeProvider;
        _activeAiExplanationReplayCredential =
            activeReplayCredential ?? AiExplanationReplayCredential;
        _previousAiExplanationReplayCredential = previousReplayCredential;
        _replayRequestTimeoutMilliseconds = replayRequestTimeoutMilliseconds;
        _replayRateLimitPermitLimit = replayRateLimitPermitLimit;
        _replayRateLimitWindowMilliseconds = replayRateLimitWindowMilliseconds;
        _metricsExportEnabled = metricsExportEnabled;
        _metricsExportEndpoint = metricsExportEndpoint;
        _metricsExportProtocol = metricsExportProtocol;
        _metricsExportIntervalMilliseconds = metricsExportIntervalMilliseconds;
        _metricsExportTimeoutMilliseconds = metricsExportTimeoutMilliseconds;
        _useProductionMetrics = useProductionMetrics;
    }

    public TestExplanationQuery ExplanationQuery { get; } = new();

    public TestExplanationCollectionQuery ExplanationCollectionQuery { get; } =
        new();

    public TestExplanationReplayStore ExplanationReplayStore { get; } = new();

    internal TestAiExplanationQueryMetrics ExplanationQueryMetrics { get; } =
        new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(configuration =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{GitHubWebhookOptions.SectionName}:Secret"] = GitHubWebhookSecret,
                [$"{PostgreSqlOptions.SectionName}:ConnectionString"] =
                    "Host=unit-test;Database=releaseguard",
                [$"{KafkaProducerOptions.SectionName}:BootstrapServers"] =
                    "localhost:19092",
                [$"{KafkaProducerOptions.SectionName}:Topic"] =
                    "releaseguard.release-risk-assessed-tests",
                [$"{KafkaConsumerOptions.SectionName}:BootstrapServers"] =
                    "localhost:19092",
                [$"{KafkaConsumerOptions.SectionName}:Topic"] =
                    "releaseguard.release-risk-assessed-tests",
                [$"{KafkaConsumerOptions.SectionName}:GroupId"] =
                    "releaseguard-webhook-unit-tests",
                [$"{AiExplanationClientOptions.SectionName}:BaseUrl"] =
                    "http://127.0.0.1:8090",
                [$"{AiExplanationQueryOptions.SectionName}:ReadTimeoutMilliseconds"] =
                    "100",
                [$"{AiExplanationQueryAuthenticationOptions.SectionName}:ActiveCredential"] =
                    _activeAiExplanationQueryCredential,
                [$"{AiExplanationQueryAuthenticationOptions.SectionName}:PreviousCredential"] =
                    _previousAiExplanationQueryCredential,
                [$"{AiExplanationQueryRateLimitOptions.SectionName}:PermitLimit"] =
                    _rateLimitPermitLimit.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                [$"{AiExplanationQueryRateLimitOptions.SectionName}:WindowMilliseconds"] =
                    _rateLimitWindowMilliseconds.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                [$"{AiExplanationReplayAuthenticationOptions.SectionName}:ActiveCredential"] =
                    _activeAiExplanationReplayCredential,
                [$"{AiExplanationReplayAuthenticationOptions.SectionName}:PreviousCredential"] =
                    _previousAiExplanationReplayCredential,
                [$"{AiExplanationReplayOptions.SectionName}:RequestTimeoutMilliseconds"] =
                    _replayRequestTimeoutMilliseconds.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                [$"{AiExplanationReplayOptions.SectionName}:PermitLimit"] =
                    _replayRateLimitPermitLimit.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                [$"{AiExplanationReplayOptions.SectionName}:WindowMilliseconds"] =
                    _replayRateLimitWindowMilliseconds.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                [$"{AiExplanationMetricsExportOptions.SectionName}:Enabled"] =
                    _metricsExportEnabled.ToString(),
                [$"{AiExplanationMetricsExportOptions.SectionName}:Endpoint"] =
                    _metricsExportEndpoint,
                [$"{AiExplanationMetricsExportOptions.SectionName}:Protocol"] =
                    _metricsExportProtocol,
                [$"{AiExplanationMetricsExportOptions.SectionName}:ExportIntervalMilliseconds"] =
                    _metricsExportIntervalMilliseconds.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                [$"{AiExplanationMetricsExportOptions.SectionName}:ExportTimeoutMilliseconds"] =
                    _metricsExportTimeoutMilliseconds.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            if (_metricsExportEnabled)
            {
                services.AddHostedService<AiExplanationMetricsExporter>();
            }
            services.RemoveAll<IGitHubWebhookDeliveryStore>();
            services.AddSingleton<IGitHubWebhookDeliveryStore, TestDeliveryStore>();
            services.RemoveAll<IReleaseRiskExplanationQuery>();
            services.AddSingleton<IReleaseRiskExplanationQuery>(ExplanationQuery);
            services.RemoveAll<IReleaseRiskExplanationCollectionQuery>();
            services.AddSingleton<IReleaseRiskExplanationCollectionQuery>(
                ExplanationCollectionQuery);
            services.RemoveAll<IReleaseRiskExplanationReplayStore>();
            services.AddSingleton<IReleaseRiskExplanationReplayStore>(
                ExplanationReplayStore);
            if (!_useProductionMetrics)
            {
                services.RemoveAll<IAiExplanationQueryMetrics>();
                services.AddSingleton<IAiExplanationQueryMetrics>(
                    ExplanationQueryMetrics);
            }
            if (_rateLimitTimeProvider is not null)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(_rateLimitTimeProvider);
            }
        });
    }

    public sealed class TestExplanationQuery : IReleaseRiskExplanationQuery
    {
        private readonly ConcurrentDictionary<
            Guid,
            Func<CancellationToken, Task<ReleaseRiskExplanationQuerySnapshot?>>>
            _responses = new();

        public void SetSnapshot(
            Guid eventId,
            ReleaseRiskExplanationQuerySnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            _responses[eventId] = _ => Task.FromResult<
                ReleaseRiskExplanationQuerySnapshot?>(snapshot);
        }

        public void SetHandler(
            Guid eventId,
            Func<CancellationToken, Task<ReleaseRiskExplanationQuerySnapshot?>>
                handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            _responses[eventId] = handler;
        }

        public Task<ReleaseRiskExplanationQuerySnapshot?> ReadAsync(
            Guid eventId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _responses.TryGetValue(eventId, out var response)
                ? response(cancellationToken)
                : Task.FromResult<ReleaseRiskExplanationQuerySnapshot?>(null);
        }
    }

    public sealed class TestExplanationCollectionQuery :
        IReleaseRiskExplanationCollectionQuery
    {
        public Func<
            int,
            ReleaseRiskExplanationListCursor?,
            CancellationToken,
            Task<ReleaseRiskExplanationListPage>> ReadPageHandler
        { get; set; } =
            (_, _, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(
                    new ReleaseRiskExplanationListPage(
                        Array.Empty<ReleaseRiskExplanationListItem>(),
                        null));
            };

        public Func<
            string,
            long,
            CancellationToken,
            Task<LatestAcceptedReleaseRiskExplanation?>> ReadLatestHandler
        { get; set; } = (_, _, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<
                LatestAcceptedReleaseRiskExplanation?>(null);
        };

        public Task<ReleaseRiskExplanationListPage> ReadPageAsync(
            int limit,
            ReleaseRiskExplanationListCursor? cursor,
            CancellationToken cancellationToken) =>
            ReadPageHandler(limit, cursor, cancellationToken);

        public Task<LatestAcceptedReleaseRiskExplanation?>
            ReadLatestAcceptedAsync(
                string repository,
                long changeNumber,
                CancellationToken cancellationToken) =>
            ReadLatestHandler(repository, changeNumber, cancellationToken);
    }

    public sealed class TestExplanationReplayStore :
        IReleaseRiskExplanationReplayStore
    {
        public Func<
            Guid,
            Guid,
            CancellationToken,
            Task<ReleaseRiskExplanationReplayReceipt>> Handler
        { get; set; } =
            (eventId, replayId, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(
                    new ReleaseRiskExplanationReplayReceipt(
                        replayId,
                        eventId,
                        1,
                        DateTimeOffset.UnixEpoch,
                        ReleaseRiskExplanationReplayDisposition.Accepted));
            };

        public Task<ReleaseRiskExplanationReplayReceipt> RequestReplayAsync(
            Guid eventId,
            Guid replayId,
            CancellationToken cancellationToken) =>
            Handler(eventId, replayId, cancellationToken);
    }

    private sealed class TestDeliveryStore : IGitHubWebhookDeliveryStore
    {
        private readonly ConcurrentDictionary<Guid, byte> _deliveryIds = new();

        public Task<bool> TryAcceptAsync(
            VerifiedGitHubWebhook webhook,
            ReleaseRiskInput? riskInput,
            ReleaseRiskAssessment? riskAssessment,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_deliveryIds.TryAdd(webhook.DeliveryId, 0));
        }
    }
}
