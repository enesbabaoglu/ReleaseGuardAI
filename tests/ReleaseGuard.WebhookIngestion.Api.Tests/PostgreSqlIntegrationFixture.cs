using Npgsql;
using Testcontainers.PostgreSql;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

[CollectionDefinition(CollectionName)]
public sealed class PostgreSqlIntegrationCollection :
    ICollectionFixture<PostgreSqlIntegrationFixture>
{
    public const string CollectionName = "PostgreSQL integration";
}

public sealed class PostgreSqlIntegrationFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(
            "postgres:16-alpine")
        .WithDatabase("releaseguard_admin")
        .WithUsername("postgres")
        .WithPassword("releaseguard-integration-tests")
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<string> CreateIsolatedDatabaseAsync()
    {
        var databaseName = $"rg_{Guid.NewGuid():N}";
        var quotedDatabaseName = new NpgsqlCommandBuilder()
            .QuoteIdentifier(databaseName);

        await using var connection = new NpgsqlConnection(
            _container.GetConnectionString());
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            $"CREATE DATABASE {quotedDatabaseName};",
            connection);
        await command.ExecuteNonQueryAsync();

        var connectionString = new NpgsqlConnectionStringBuilder(
            _container.GetConnectionString())
        {
            Database = databaseName
        };

        return connectionString.ConnectionString;
    }
}
