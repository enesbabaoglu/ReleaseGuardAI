using Npgsql;
using NpgsqlTypes;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed record ReleaseRiskOutboxClaim(
    Guid EventId,
    string ClaimOwner,
    int AttemptCount,
    ReleaseRiskOutboxEnvelope Envelope);

public interface IReleaseRiskOutboxStore
{
    Task<IReadOnlyList<ReleaseRiskOutboxClaim>> ClaimPendingAsync(
        string claimOwner,
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<bool> MarkPublishedAsync(
        ReleaseRiskOutboxClaim claim,
        CancellationToken cancellationToken);

    Task<bool> MarkFailedAsync(
        ReleaseRiskOutboxClaim claim,
        TimeSpan retryDelay,
        CancellationToken cancellationToken);

    Task<bool> ReleaseClaimAsync(
        ReleaseRiskOutboxClaim claim,
        CancellationToken cancellationToken);
}

public sealed class PostgreSqlReleaseRiskOutboxStore : IReleaseRiskOutboxStore
{
    private const string ClaimPendingSql = """
        WITH candidates AS (
            SELECT event_id
            FROM release_risk_outbox_messages
            WHERE published_at IS NULL
              AND next_attempt_at <= clock_timestamp()
              AND (
                  claimed_by IS NULL
                  OR claim_expires_at <= clock_timestamp())
            ORDER BY next_attempt_at, created_at, event_id
            FOR UPDATE SKIP LOCKED
            LIMIT @batch_size
        )
        UPDATE release_risk_outbox_messages AS outbox
        SET
            claimed_by = @claim_owner,
            claim_expires_at = clock_timestamp() + @lease_duration,
            attempt_count = outbox.attempt_count + 1
        FROM candidates
        WHERE outbox.event_id = candidates.event_id
        RETURNING
            outbox.event_id,
            outbox.claimed_by,
            outbox.attempt_count,
            outbox.envelope::text;
        """;

    private const string MarkPublishedSql = """
        UPDATE release_risk_outbox_messages
        SET
            published_at = clock_timestamp(),
            claimed_by = NULL,
            claim_expires_at = NULL
        WHERE event_id = @event_id
          AND published_at IS NULL
          AND claimed_by = @claim_owner
          AND claim_expires_at > clock_timestamp()
        RETURNING event_id;
        """;

    private const string MarkFailedSql = """
        UPDATE release_risk_outbox_messages
        SET
            next_attempt_at = clock_timestamp() + @retry_delay,
            claimed_by = NULL,
            claim_expires_at = NULL
        WHERE event_id = @event_id
          AND published_at IS NULL
          AND claimed_by = @claim_owner
          AND claim_expires_at > clock_timestamp()
        RETURNING event_id;
        """;

    private const string ReleaseClaimSql = """
        UPDATE release_risk_outbox_messages
        SET
            next_attempt_at = LEAST(next_attempt_at, clock_timestamp()),
            claimed_by = NULL,
            claim_expires_at = NULL
        WHERE event_id = @event_id
          AND published_at IS NULL
          AND claimed_by = @claim_owner
        RETURNING event_id;
        """;

    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlReleaseRiskOutboxStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<ReleaseRiskOutboxClaim>> ClaimPendingAsync(
        string claimOwner,
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimOwner);

        if (claimOwner.Length > 128)
        {
            throw new ArgumentException(
                "Claim owner must not exceed 128 characters.",
                nameof(claimOwner));
        }

        if (batchSize is < 1 or > OutboxDispatcherOptions.MaximumBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        await using var command = _dataSource.CreateCommand(ClaimPendingSql);
        command.Parameters.AddWithValue("claim_owner", NpgsqlDbType.Text, claimOwner);
        command.Parameters.AddWithValue("batch_size", NpgsqlDbType.Integer, batchSize);
        command.Parameters.AddWithValue(
            "lease_duration",
            NpgsqlDbType.Interval,
            leaseDuration);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var claims = new List<ReleaseRiskOutboxClaim>();

        while (await reader.ReadAsync(cancellationToken))
        {
            claims.Add(new ReleaseRiskOutboxClaim(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetInt32(2),
                ReleaseRiskOutboxEnvelope.Deserialize(reader.GetString(3))));
        }

        return claims;
    }

    public Task<bool> MarkPublishedAsync(
        ReleaseRiskOutboxClaim claim,
        CancellationToken cancellationToken) =>
        ExecuteClaimUpdateAsync(
            MarkPublishedSql,
            claim,
            retryDelay: null,
            cancellationToken);

    public Task<bool> MarkFailedAsync(
        ReleaseRiskOutboxClaim claim,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        if (retryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
        }

        return ExecuteClaimUpdateAsync(
            MarkFailedSql,
            claim,
            retryDelay,
            cancellationToken);
    }

    public Task<bool> ReleaseClaimAsync(
        ReleaseRiskOutboxClaim claim,
        CancellationToken cancellationToken) =>
        ExecuteClaimUpdateAsync(
            ReleaseClaimSql,
            claim,
            retryDelay: null,
            cancellationToken);

    private async Task<bool> ExecuteClaimUpdateAsync(
        string sql,
        ReleaseRiskOutboxClaim claim,
        TimeSpan? retryDelay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("event_id", claim.EventId);
        command.Parameters.AddWithValue(
            "claim_owner",
            NpgsqlDbType.Text,
            claim.ClaimOwner);

        if (retryDelay is not null)
        {
            command.Parameters.AddWithValue(
                "retry_delay",
                NpgsqlDbType.Interval,
                retryDelay.Value);
        }

        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }
}
