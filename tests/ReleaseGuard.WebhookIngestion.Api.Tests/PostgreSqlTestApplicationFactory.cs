using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class PostgreSqlTestApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly bool _applyMigrationsOnStartup;
    private readonly int _queryReadTimeoutMilliseconds;

    public PostgreSqlTestApplicationFactory(
        string connectionString,
        bool applyMigrationsOnStartup,
        int queryReadTimeoutMilliseconds = 5_000)
    {
        _connectionString = connectionString;
        _applyMigrationsOnStartup = applyMigrationsOnStartup;
        _queryReadTimeoutMilliseconds = queryReadTimeoutMilliseconds;
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
                [$"{AiExplanationQueryAuthenticationOptions.SectionName}:Credential"] =
                    TestApplicationFactory.AiExplanationQueryCredential
            });
        });
    }
}
