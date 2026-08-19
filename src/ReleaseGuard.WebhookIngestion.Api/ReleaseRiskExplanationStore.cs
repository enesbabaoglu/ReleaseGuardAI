using System.Text.Json;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed record ReleaseRiskExplanationClaim(
    Guid EventId,
    string ClaimOwner,
    int AttemptCount,
    ReleaseRiskOutboxEnvelope Envelope);

public interface IReleaseRiskExplanationStore
{
    Task<IReadOnlyList<ReleaseRiskExplanationClaim>> ClaimPendingAsync(
        string claimOwner,
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<bool> MarkCompletedAsync(
        ReleaseRiskExplanationClaim claim,
        ReleaseRiskExplanation explanation,
        CancellationToken cancellationToken);

    Task<bool> MarkFailedAsync(
        ReleaseRiskExplanationClaim claim,
        TimeSpan retryDelay,
        CancellationToken cancellationToken);

    Task<bool> ReleaseClaimAsync(
        ReleaseRiskExplanationClaim claim,
        CancellationToken cancellationToken);
}

public sealed class PostgreSqlReleaseRiskExplanationStore :
    IReleaseRiskExplanationStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private const string ClaimPendingSql = """
        WITH candidates AS (
            SELECT event_id
            FROM release_risk_event_inbox
            WHERE explanation_completed_at IS NULL
              AND explanation_next_attempt_at <= clock_timestamp()
              AND (
                  explanation_claimed_by IS NULL
                  OR explanation_claim_expires_at <= clock_timestamp())
            ORDER BY explanation_next_attempt_at, accepted_at, event_id
            FOR UPDATE SKIP LOCKED
            LIMIT @batch_size
        )
        UPDATE release_risk_event_inbox AS inbox
        SET
            explanation_claimed_by = @claim_owner,
            explanation_claim_expires_at = clock_timestamp() + @lease_duration,
            explanation_attempt_count = inbox.explanation_attempt_count + 1
        FROM candidates
        WHERE inbox.event_id = candidates.event_id
        RETURNING
            inbox.event_id,
            inbox.explanation_claimed_by,
            inbox.explanation_attempt_count,
            inbox.envelope::text;
        """;

    private const string MarkCompletedSql = """
        UPDATE release_risk_event_inbox
        SET
            explanation_completed_at = clock_timestamp(),
            explanation = @explanation,
            explanation_claimed_by = NULL,
            explanation_claim_expires_at = NULL
        WHERE event_id = @event_id
          AND explanation_completed_at IS NULL
          AND explanation_claimed_by = @claim_owner
          AND explanation_claim_expires_at > clock_timestamp()
        RETURNING event_id;
        """;

    private const string MarkFailedSql = """
        UPDATE release_risk_event_inbox
        SET
            explanation_next_attempt_at = clock_timestamp() + @retry_delay,
            explanation_claimed_by = NULL,
            explanation_claim_expires_at = NULL
        WHERE event_id = @event_id
          AND explanation_completed_at IS NULL
          AND explanation_claimed_by = @claim_owner
          AND explanation_claim_expires_at > clock_timestamp()
        RETURNING event_id;
        """;

    private const string ReleaseClaimSql = """
        UPDATE release_risk_event_inbox
        SET
            explanation_next_attempt_at = LEAST(
                explanation_next_attempt_at,
                clock_timestamp()),
            explanation_claimed_by = NULL,
            explanation_claim_expires_at = NULL
        WHERE event_id = @event_id
          AND explanation_completed_at IS NULL
          AND explanation_claimed_by = @claim_owner
        RETURNING event_id;
        """;

    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlReleaseRiskExplanationStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<ReleaseRiskExplanationClaim>> ClaimPendingAsync(
        string claimOwner,
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ValidateClaimRequest(claimOwner, batchSize, leaseDuration);

        await using var command = _dataSource.CreateCommand(ClaimPendingSql);
        command.Parameters.AddWithValue(
            "claim_owner",
            NpgsqlDbType.Text,
            claimOwner);
        command.Parameters.AddWithValue(
            "batch_size",
            NpgsqlDbType.Integer,
            batchSize);
        command.Parameters.AddWithValue(
            "lease_duration",
            NpgsqlDbType.Interval,
            leaseDuration);

        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        var claims = new List<ReleaseRiskExplanationClaim>();

        while (await reader.ReadAsync(cancellationToken))
        {
            var claim = new ReleaseRiskExplanationClaim(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetInt32(2),
                ReleaseRiskOutboxEnvelope.Deserialize(reader.GetString(3)));
            ValidateClaim(claim);
            claims.Add(claim);
        }

        return claims;
    }

    public Task<bool> MarkCompletedAsync(
        ReleaseRiskExplanationClaim claim,
        ReleaseRiskExplanation explanation,
        CancellationToken cancellationToken)
    {
        ValidateClaim(claim);
        ArgumentNullException.ThrowIfNull(explanation);

        if (!explanation.IsValidFor(claim.EventId))
        {
            throw new ArgumentException(
                "The explanation must be a valid result for the claimed event ID.",
                nameof(explanation));
        }

        return ExecuteClaimUpdateAsync(
            MarkCompletedSql,
            claim,
            explanation,
            retryDelay: null,
            cancellationToken);
    }

    public Task<bool> MarkFailedAsync(
        ReleaseRiskExplanationClaim claim,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        ValidateClaim(claim);

        if (retryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
        }

        return ExecuteClaimUpdateAsync(
            MarkFailedSql,
            claim,
            explanation: null,
            retryDelay,
            cancellationToken);
    }

    public Task<bool> ReleaseClaimAsync(
        ReleaseRiskExplanationClaim claim,
        CancellationToken cancellationToken)
    {
        ValidateClaim(claim);
        return ExecuteClaimUpdateAsync(
            ReleaseClaimSql,
            claim,
            explanation: null,
            retryDelay: null,
            cancellationToken);
    }

    private async Task<bool> ExecuteClaimUpdateAsync(
        string sql,
        ReleaseRiskExplanationClaim claim,
        ReleaseRiskExplanation? explanation,
        TimeSpan? retryDelay,
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("event_id", claim.EventId);
        command.Parameters.AddWithValue(
            "claim_owner",
            NpgsqlDbType.Text,
            claim.ClaimOwner);

        if (explanation is not null)
        {
            command.Parameters.AddWithValue(
                "explanation",
                NpgsqlDbType.Jsonb,
                JsonSerializer.Serialize(explanation, JsonOptions));
        }

        if (retryDelay is not null)
        {
            command.Parameters.AddWithValue(
                "retry_delay",
                NpgsqlDbType.Interval,
                retryDelay.Value);
        }

        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static void ValidateClaimRequest(
        string claimOwner,
        int batchSize,
        TimeSpan leaseDuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimOwner);

        if (Encoding.UTF8.GetByteCount(claimOwner) > 128)
        {
            throw new ArgumentException(
                "Claim owner must not exceed 128 UTF-8 bytes.",
                nameof(claimOwner));
        }

        if (batchSize is < 1 or > AiExplanationProcessorOptions.MaximumBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }
    }

    private static void ValidateClaim(ReleaseRiskExplanationClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);

        if (string.IsNullOrWhiteSpace(claim.ClaimOwner) ||
            Encoding.UTF8.GetByteCount(claim.ClaimOwner) > 128 ||
            claim.AttemptCount < 1 ||
            claim.Envelope is null ||
            claim.EventId != claim.Envelope.EventId ||
            !claim.Envelope.IsValidVersionOneContract())
        {
            throw new ArgumentException(
                "The explanation claim must contain a valid owner, attempt and matching V1 event snapshot.",
                nameof(claim));
        }
    }
}
