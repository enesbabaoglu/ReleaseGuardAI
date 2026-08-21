using System.Reflection;
using Npgsql;
using NpgsqlTypes;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed class PostgreSqlSchemaInitializer : IHostedService
{
    private const int LatestSchemaVersion = 8;
    private const long MigrationLockKey = 7_142_033_117_501_001;

    private static readonly Migration[] Migrations =
    [
        new(
            1,
            "create GitHub webhook deliveries",
            "ReleaseGuard.Database.Migrations.V001.sql"),
        new(
            2,
            "create release risk outbox",
            "ReleaseGuard.Database.Migrations.V002.sql"),
        new(
            3,
            "add release risk outbox dispatch lifecycle",
            "ReleaseGuard.Database.Migrations.V003.sql"),
        new(
            4,
            "create release risk event inbox",
            "ReleaseGuard.Database.Migrations.V004.sql"),
        new(
            5,
            "add release risk AI explanation lifecycle",
            "ReleaseGuard.Database.Migrations.V005.sql"),
        new(
            6,
            "add release risk AI explanation terminal lifecycle",
            "ReleaseGuard.Database.Migrations.V006.sql"),
        new(
            7,
            "add release risk AI explanation replay lifecycle",
            "ReleaseGuard.Database.Migrations.V007.sql"),
        new(
            8,
            "add bounded retention cleanup indexes",
            "ReleaseGuard.Database.Migrations.V008.sql")
    ];

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

    private const string VerifyApplicationTablesSql = """
        SELECT 1
        FROM github_webhook_deliveries
        LIMIT 0;

        SELECT
            published_at,
            attempt_count,
            next_attempt_at,
            claimed_by,
            claim_expires_at
        FROM release_risk_outbox_messages
        LIMIT 0;

        SELECT
            event_id,
            message_key,
            topic,
            kafka_partition,
            kafka_offset,
            payload,
            envelope,
            accepted_at,
            explanation_attempt_count,
            explanation_next_attempt_at,
            explanation_claimed_by,
            explanation_claim_expires_at,
            explanation_completed_at,
            explanation,
            explanation_failed_at,
            explanation_failure_code,
            explanation_failure_reason
        FROM release_risk_event_inbox
        LIMIT 0;

        SELECT
            event_id,
            attempt_count,
            failed_at,
            failure_code,
            failure_reason,
            accepted_at,
            envelope
        FROM release_risk_ai_explanation_failed_work
        LIMIT 0;

        SELECT
            replay_id,
            event_id,
            generation,
            requested_at,
            prior_failed_at,
            prior_failure_code,
            prior_failure_reason,
            attempt_count,
            next_attempt_at,
            claimed_by,
            claim_expires_at,
            completed_at,
            explanation,
            failed_at,
            failure_code,
            failure_reason
        FROM release_risk_ai_explanation_replays
        LIMIT 0;

        SELECT
            replay_id,
            event_id,
            generation,
            requested_at,
            prior_failed_at,
            prior_failure_code,
            prior_failure_reason,
            attempt_count,
            completed_at,
            failed_at,
            failure_code,
            failure_reason
        FROM release_risk_ai_explanation_replay_history
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

        foreach (var migration in Migrations.Where(
                     migration => migration.Version > currentVersion))
        {
            await ApplyMigrationAsync(
                connection,
                transaction,
                migration,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        _logger.LogInformation(
            "PostgreSQL schema is at version {SchemaVersion}.",
            LatestSchemaVersion);
    }

    private static async Task ApplyMigrationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Migration migration,
        CancellationToken cancellationToken)
    {
        var migrationSql = await ReadMigrationResourceAsync(
            migration.ResourceName,
            cancellationToken);

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
            VALUES (@version, @description);
            """,
            connection,
            transaction);
        recordMigrationCommand.Parameters.AddWithValue("version", migration.Version);
        recordMigrationCommand.Parameters.AddWithValue(
            "description",
            migration.Description);
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
            VerifyApplicationTablesSql,
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
        string resourceName,
        CancellationToken cancellationToken)
    {
        var assembly = Assembly.GetExecutingAssembly();
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded migration '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);

        return await reader.ReadToEndAsync(cancellationToken);
    }

    private sealed record Migration(
        int Version,
        string Description,
        string ResourceName);
}
