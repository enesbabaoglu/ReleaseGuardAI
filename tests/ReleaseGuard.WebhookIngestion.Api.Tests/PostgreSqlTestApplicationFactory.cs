using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class PostgreSqlTestApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly bool _applyMigrationsOnStartup;
    private readonly int _queryReadTimeoutMilliseconds;
    private readonly string? _queryPreviousCredential;
    private readonly int _rateLimitPermitLimit;
    private readonly int _rateLimitWindowMilliseconds;
    private readonly TimeProvider? _rateLimitTimeProvider;
    private readonly IAiExplanationQueryMetrics? _queryMetrics;

    public PostgreSqlTestApplicationFactory(
        string connectionString,
        bool applyMigrationsOnStartup,
        int queryReadTimeoutMilliseconds = 5_000,
        string? queryPreviousCredential = null,
        int rateLimitPermitLimit =
            AiExplanationQueryRateLimitOptions.MaximumPermitLimit,
        int rateLimitWindowMilliseconds = 60_000,
        TimeProvider? rateLimitTimeProvider = null,
        IAiExplanationQueryMetrics? queryMetrics = null)
    {
        _connectionString = connectionString;
        _applyMigrationsOnStartup = applyMigrationsOnStartup;
        _queryReadTimeoutMilliseconds = queryReadTimeoutMilliseconds;
        _queryPreviousCredential = queryPreviousCredential;
        _rateLimitPermitLimit = rateLimitPermitLimit;
        _rateLimitWindowMilliseconds = rateLimitWindowMilliseconds;
        _rateLimitTimeProvider = rateLimitTimeProvider;
        _queryMetrics = queryMetrics;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(configuration =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{GitHubWebhookOptions.SectionName}:Secret"] =
                    TestApplicationFactory.GitHubWebhookSecret,
                [$"{PostgreSqlOptions.SectionName}:ConnectionString"] =
                    _connectionString,
                [$"{PostgreSqlOptions.SectionName}:ApplyMigrationsOnStartup"] =
                    _applyMigrationsOnStartup.ToString(),
                [$"{KafkaProducerOptions.SectionName}:BootstrapServers"] =
                    "localhost:19092",
                [$"{KafkaProducerOptions.SectionName}:Topic"] =
                    "releaseguard.release-risk-assessed-tests",
                [$"{KafkaConsumerOptions.SectionName}:BootstrapServers"] =
                    "localhost:19092",
                [$"{KafkaConsumerOptions.SectionName}:Topic"] =
                    "releaseguard.release-risk-assessed-tests",
                [$"{KafkaConsumerOptions.SectionName}:GroupId"] =
                    "releaseguard-postgresql-tests",
                [$"{AiExplanationClientOptions.SectionName}:BaseUrl"] =
                    "http://127.0.0.1:8090",
                [$"{AiExplanationQueryOptions.SectionName}:ReadTimeoutMilliseconds"] =
                    _queryReadTimeoutMilliseconds.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                [$"{AiExplanationQueryAuthenticationOptions.SectionName}:ActiveCredential"] =
                    TestApplicationFactory.AiExplanationQueryCredential,
                [$"{AiExplanationQueryAuthenticationOptions.SectionName}:PreviousCredential"] =
                    _queryPreviousCredential,
                [$"{AiExplanationQueryRateLimitOptions.SectionName}:PermitLimit"] =
                    _rateLimitPermitLimit.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                [$"{AiExplanationQueryRateLimitOptions.SectionName}:WindowMilliseconds"] =
                    _rateLimitWindowMilliseconds.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
            });
        });

        if (_rateLimitTimeProvider is not null || _queryMetrics is not null)
        {
            builder.ConfigureTestServices(services =>
            {
                if (_rateLimitTimeProvider is not null)
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton(_rateLimitTimeProvider);
                }

                if (_queryMetrics is not null)
                {
                    services.RemoveAll<IAiExplanationQueryMetrics>();
                    services.AddSingleton(_queryMetrics);
                }
            });
        }
    }
}
