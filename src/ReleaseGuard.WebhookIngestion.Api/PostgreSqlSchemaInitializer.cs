using System.Reflection;
using Npgsql;
using NpgsqlTypes;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed class PostgreSqlSchemaInitializer : IHostedService
{
    private const int LatestSchemaVersion = 1;
    private const long MigrationLockKey = 7_142_033_117_501_001;
    private const string MigrationResourceName =
        "ReleaseGuard.Database.Migrations.V001.sql";

    private const string CreateMigrationTableSql = """
        CREATE TABLE IF NOT EXISTS release_guard_schema_migrations (
            version integer PRIMARY KEY,
            description text NOT NULL,
            applied_at timestamptz NOT NULL DEFAULT transaction_timestamp()
        );
        """;

    private const string ReadSchemaVersionSql = """
        SELECT COALESCE(MAX(version), 0)
        FROM release_guard_schema_migrations;
        """;

    private const string VerifyDeliveryTableSql = """
        SELECT 1
        FROM github_webhook_deliveries
        LIMIT 0;
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgreSqlOptions _options;
    private readonly ILogger<PostgreSqlSchemaInitializer> _logger;

    public PostgreSqlSchemaInitializer(
        NpgsqlDataSource dataSource,
        Microsoft.Extensions.Options.IOptions<PostgreSqlOptions> options,
        ILogger<PostgreSqlSchemaInitializer> logger)
    {
        _dataSource = dataSource;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await InitializeAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "PostgreSQL schema initialization failed. Verify connectivity and apply the checked-in migrations before serving traffic.",
                exception);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        if (_options.ApplyMigrationsOnStartup)
        {
            await ApplyMigrationsAsync(connection, cancellationToken);
        }

        await VerifySchemaAsync(connection, cancellationToken);
    }

    private async Task ApplyMigrationsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var lockCommand = new NpgsqlCommand(
                         "SELECT pg_advisory_xact_lock(@lock_key);",
                         connection,
                         transaction))
        {
            lockCommand.Parameters.AddWithValue(
                "lock_key",
                NpgsqlDbType.Bigint,
                MigrationLockKey);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var createTableCommand = new NpgsqlCommand(
                         CreateMigrationTableSql,
                         connection,
                         transaction))
        {
            await createTableCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var currentVersion = await ReadSchemaVersionAsync(
            connection,
            transaction,
            cancellationToken);

        if (currentVersion > LatestSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Database schema version {currentVersion} is newer than supported version {LatestSchemaVersion}.");
        }

        if (currentVersion < LatestSchemaVersion)
        {
            await ApplyVersionOneAsync(connection, transaction, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        _logger.LogInformation(
            "PostgreSQL schema is at version {SchemaVersion}.",
            LatestSchemaVersion);
    }

    private static async Task ApplyVersionOneAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var migrationSql = await ReadMigrationResourceAsync(cancellationToken);

        await using (var migrationCommand = new NpgsqlCommand(
                         migrationSql,
                         connection,
                         transaction))
        {
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var recordMigrationCommand = new NpgsqlCommand(
            """
            INSERT INTO release_guard_schema_migrations (version, description)
            VALUES (1, 'create GitHub webhook deliveries');
            """,
            connection,
            transaction);
        await recordMigrationCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task VerifySchemaAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var version = await ReadSchemaVersionAsync(
            connection,
            transaction: null,
            cancellationToken);

        if (version != LatestSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Database schema version {version} does not match required version {LatestSchemaVersion}.");
        }

        await using var verifyCommand = new NpgsqlCommand(
            VerifyDeliveryTableSql,
            connection);
        await verifyCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ReadSchemaVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var versionCommand = new NpgsqlCommand(
            ReadSchemaVersionSql,
            connection,
            transaction);
        var result = await versionCommand.ExecuteScalarAsync(cancellationToken);

        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ReadMigrationResourceAsync(
        CancellationToken cancellationToken)
    {
        var assembly = Assembly.GetExecutingAssembly();
        await using var stream = assembly.GetManifestResourceStream(MigrationResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded migration '{MigrationResourceName}' was not found.");
        using var reader = new StreamReader(stream);

        return await reader.ReadToEndAsync(cancellationToken);
    }
}
