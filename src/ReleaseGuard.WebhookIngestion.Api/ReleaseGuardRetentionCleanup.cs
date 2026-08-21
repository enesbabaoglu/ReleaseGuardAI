using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed record ReleaseGuardRetentionCleanupResult(
    int PublishedOutboxMessagesDeleted,
    int AcceptedDeliveriesDeleted,
    int IgnoredDeliveriesDeleted)
{
    public int TotalDeleted =>
        PublishedOutboxMessagesDeleted +
        AcceptedDeliveriesDeleted +
        IgnoredDeliveriesDeleted;
}

public interface IReleaseGuardRetentionStore
{
    Task<ReleaseGuardRetentionCleanupResult> DeleteBatchAsync(
        int batchSize,
        TimeSpan publishedOutboxRetention,
        TimeSpan acceptedDeliveryRetention,
        TimeSpan ignoredDeliveryRetention,
        CancellationToken cancellationToken);
}

public sealed class PostgreSqlReleaseGuardRetentionStore :
    IReleaseGuardRetentionStore
{
    private const string DeletePublishedOutboxSql = """
        WITH candidates AS (
            SELECT outbox.event_id
            FROM release_risk_outbox_messages AS outbox
            WHERE outbox.published_at IS NOT NULL
              AND outbox.published_at <
                  clock_timestamp() - @published_retention
              AND EXISTS (
                  SELECT 1
                  FROM release_risk_event_inbox AS inbox
                  WHERE inbox.event_id = outbox.event_id)
            ORDER BY outbox.published_at, outbox.event_id
            FOR UPDATE SKIP LOCKED
            LIMIT @batch_size
        )
        DELETE FROM release_risk_outbox_messages AS outbox
        USING candidates
        WHERE outbox.event_id = candidates.event_id;
        """;

    private const string DeleteAcceptedDeliveriesSql = """
        WITH candidates AS (
            SELECT delivery.delivery_id
            FROM github_webhook_deliveries AS delivery
            WHERE delivery.disposition = 'accepted'
              AND delivery.accepted_at <
                  clock_timestamp() - @accepted_retention
              AND NOT EXISTS (
                  SELECT 1
                  FROM release_risk_outbox_messages AS outbox
                  WHERE outbox.event_id = delivery.delivery_id)
              AND EXISTS (
                  SELECT 1
                  FROM release_risk_event_inbox AS inbox
                  WHERE inbox.event_id = delivery.delivery_id)
            ORDER BY delivery.accepted_at, delivery.delivery_id
            FOR UPDATE SKIP LOCKED
            LIMIT @batch_size
        )
        DELETE FROM github_webhook_deliveries AS delivery
        USING candidates
        WHERE delivery.delivery_id = candidates.delivery_id;
        """;

    private const string DeleteIgnoredDeliveriesSql = """
        WITH candidates AS (
            SELECT delivery_id
            FROM github_webhook_deliveries
            WHERE disposition = 'ignored'
              AND accepted_at < clock_timestamp() - @ignored_retention
            ORDER BY accepted_at, delivery_id
            FOR UPDATE SKIP LOCKED
            LIMIT @batch_size
        )
        DELETE FROM github_webhook_deliveries AS delivery
        USING candidates
        WHERE delivery.delivery_id = candidates.delivery_id;
        """;

    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlReleaseGuardRetentionStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<ReleaseGuardRetentionCleanupResult> DeleteBatchAsync(
        int batchSize,
        TimeSpan publishedOutboxRetention,
        TimeSpan acceptedDeliveryRetention,
        TimeSpan ignoredDeliveryRetention,
        CancellationToken cancellationToken)
    {
        if (batchSize is < 1 or > RetentionCleanupOptions.MaximumBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        if (publishedOutboxRetention <= TimeSpan.Zero ||
            acceptedDeliveryRetention < publishedOutboxRetention ||
            ignoredDeliveryRetention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(publishedOutboxRetention));
        }

        await using var connection = await _dataSource.OpenConnectionAsync(
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            cancellationToken);
        var outbox = await ExecuteAsync(
            connection,
            transaction,
            DeletePublishedOutboxSql,
            "published_retention",
            publishedOutboxRetention,
            batchSize,
            cancellationToken);
        var accepted = await ExecuteAsync(
            connection,
            transaction,
            DeleteAcceptedDeliveriesSql,
            "accepted_retention",
            acceptedDeliveryRetention,
            batchSize,
            cancellationToken);
        var ignored = await ExecuteAsync(
            connection,
            transaction,
            DeleteIgnoredDeliveriesSql,
            "ignored_retention",
            ignoredDeliveryRetention,
            batchSize,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ReleaseGuardRetentionCleanupResult(
            outbox,
            accepted,
            ignored);
    }

    private static async Task<int> ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        string retentionParameter,
        TimeSpan retention,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(
            retentionParameter,
            NpgsqlDbType.Interval,
            retention);
        command.Parameters.AddWithValue(
            "batch_size",
            NpgsqlDbType.Integer,
            batchSize);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed class ReleaseGuardRetentionCleanup : BackgroundService
{
    private readonly IReleaseGuardRetentionStore _store;
    private readonly RetentionCleanupOptions _options;
    private readonly ILogger<ReleaseGuardRetentionCleanup> _logger;

    public ReleaseGuardRetentionCleanup(
        IReleaseGuardRetentionStore store,
        IOptions<RetentionCleanupOptions> options,
        ILogger<ReleaseGuardRetentionCleanup> logger)
    {
        _store = store;
        _options = options.Value;
        RetentionCleanupOptions.ThrowIfInvalid(_options);
        _logger = logger;
    }

    public async Task<ReleaseGuardRetentionCleanupResult> RunCleanupBatchAsync(
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(
            TimeSpan.FromMilliseconds(_options.CleanupTimeoutMilliseconds));
        return await _store.DeleteBatchAsync(
            _options.BatchSize,
            TimeSpan.FromHours(_options.PublishedOutboxRetentionHours),
            TimeSpan.FromHours(_options.AcceptedDeliveryRetentionHours),
            TimeSpan.FromHours(_options.IgnoredDeliveryRetentionHours),
            timeout.Token);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Retention cleanup is disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await RunCleanupBatchAsync(stoppingToken);
                if (result.TotalDeleted > 0)
                {
                    _logger.LogInformation(
                        "Retention cleanup deleted {PublishedOutboxCount} published outbox messages, {AcceptedDeliveryCount} accepted delivery receipts, and {IgnoredDeliveryCount} ignored delivery receipts.",
                        result.PublishedOutboxMessagesDeleted,
                        result.AcceptedDeliveriesDeleted,
                        result.IgnoredDeliveriesDeleted);
                }

                await Task.Delay(
                    _options.PollIntervalMilliseconds,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Retention cleanup failed; the next bounded poll will retry.");
                await Task.Delay(
                    _options.PollIntervalMilliseconds,
                    stoppingToken);
            }
        }
    }
}
