using System.Collections.Concurrent;
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

    public TestExplanationQuery ExplanationQuery { get; } = new();

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
                    "100"
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IGitHubWebhookDeliveryStore>();
            services.AddSingleton<IGitHubWebhookDeliveryStore, TestDeliveryStore>();
            services.RemoveAll<IReleaseRiskExplanationQuery>();
            services.AddSingleton<IReleaseRiskExplanationQuery>(ExplanationQuery);
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
