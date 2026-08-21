using System.Text.Json;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed record ReleaseRiskExplanationClaim(
    Guid EventId,
    string ClaimOwner,
    int AttemptCount,
    ReleaseRiskOutboxEnvelope Envelope,
    Guid? ReplayId = null,
    int Generation = 0);

public sealed record ReleaseRiskExplanationTerminalFailure(
    string Code,
    string Reason);

public sealed record ReleaseRiskExplanationFailedWork(
    Guid EventId,
    int AttemptCount,
    DateTimeOffset FailedAt,
    string FailureCode,
    string FailureReason,
    DateTimeOffset AcceptedAt,
    ReleaseRiskOutboxEnvelope Envelope);

public interface IReleaseRiskExplanationStore
{
    Task<IReadOnlyList<ReleaseRiskExplanationClaim>> ClaimPendingAsync(
        string claimOwner,
        int batchSize,
        TimeSpan leaseDuration,
        int maximumAttempts,
        CancellationToken cancellationToken);

    Task<bool> MarkCompletedAsync(
        ReleaseRiskExplanationClaim claim,
        ReleaseRiskExplanation explanation,
        CancellationToken cancellationToken);

    Task<bool> MarkFailedAsync(
        ReleaseRiskExplanationClaim claim,
        TimeSpan retryDelay,
        CancellationToken cancellationToken);

    Task<bool> MarkTerminalAsync(
        ReleaseRiskExplanationClaim claim,
        ReleaseRiskExplanationTerminalFailure failure,
        CancellationToken cancellationToken);

    Task<bool> ReleaseClaimAsync(
        ReleaseRiskExplanationClaim claim,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ReleaseRiskExplanationFailedWork>> ReadFailedWorkAsync(
        int limit,
        CancellationToken cancellationToken);
}

public sealed class PostgreSqlReleaseRiskExplanationStore :
    IReleaseRiskExplanationStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public const int MaximumFailedWorkQueryLimit = 100;

    private const string TerminalizeExhaustedSql = """
        WITH candidates AS (
            SELECT event_id
            FROM release_risk_event_inbox
            WHERE explanation_completed_at IS NULL
              AND explanation_failed_at IS NULL
              AND explanation_attempt_count >= @maximum_attempts
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
            explanation_failed_at = clock_timestamp(),
            explanation_failure_code = @failure_code,
            explanation_failure_reason = @failure_reason,
            explanation_claimed_by = NULL,
            explanation_claim_expires_at = NULL
        FROM candidates
        WHERE inbox.event_id = candidates.event_id;
        """;

    private const string ClaimPendingSql = """
        WITH candidates AS (
            SELECT event_id
            FROM release_risk_event_inbox
            WHERE explanation_completed_at IS NULL
              AND explanation_failed_at IS NULL
              AND explanation_attempt_count < @maximum_attempts
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

    private const string TerminalizeExhaustedReplaySql = """
        WITH candidates AS (
            SELECT replay_id
            FROM release_risk_ai_explanation_replays
            WHERE completed_at IS NULL
              AND failed_at IS NULL
              AND attempt_count >= @maximum_attempts
              AND next_attempt_at <= clock_timestamp()
              AND (
                  claimed_by IS NULL
                  OR claim_expires_at <= clock_timestamp())
            ORDER BY next_attempt_at, requested_at, replay_id
            FOR UPDATE SKIP LOCKED
            LIMIT @batch_size
        )
        UPDATE release_risk_ai_explanation_replays AS replay
        SET
            failed_at = clock_timestamp(),
            failure_code = @failure_code,
            failure_reason = @failure_reason,
            claimed_by = NULL,
            claim_expires_at = NULL
        FROM candidates
        WHERE replay.replay_id = candidates.replay_id;
        """;

    private const string ClaimPendingReplaySql = """
        WITH candidates AS (
            SELECT replay_id
            FROM release_risk_ai_explanation_replays
            WHERE completed_at IS NULL
              AND failed_at IS NULL
              AND attempt_count < @maximum_attempts
              AND next_attempt_at <= clock_timestamp()
              AND (
                  claimed_by IS NULL
                  OR claim_expires_at <= clock_timestamp())
            ORDER BY next_attempt_at, requested_at, replay_id
            FOR UPDATE SKIP LOCKED
            LIMIT @batch_size
        )
        UPDATE release_risk_ai_explanation_replays AS replay
        SET
            claimed_by = @claim_owner,
            claim_expires_at = clock_timestamp() + @lease_duration,
            attempt_count = replay.attempt_count + 1
        FROM candidates
        WHERE replay.replay_id = candidates.replay_id
        RETURNING
            replay.event_id,
            replay.claimed_by,
            replay.attempt_count,
            replay.envelope::text,
            replay.replay_id,
            replay.generation;
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
          AND explanation_failed_at IS NULL
          AND explanation_claimed_by = @claim_owner
          AND explanation_attempt_count = @attempt_count
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
          AND explanation_failed_at IS NULL
          AND explanation_claimed_by = @claim_owner
          AND explanation_attempt_count = @attempt_count
          AND explanation_claim_expires_at > clock_timestamp()
        RETURNING event_id;
        """;

    private const string MarkTerminalSql = """
        WITH applied AS (
            UPDATE release_risk_event_inbox
            SET
                explanation_failed_at = clock_timestamp(),
                explanation_failure_code = @failure_code,
                explanation_failure_reason = @failure_reason,
                explanation_claimed_by = NULL,
                explanation_claim_expires_at = NULL
            WHERE event_id = @event_id
              AND explanation_completed_at IS NULL
              AND explanation_failed_at IS NULL
              AND explanation_claimed_by = @claim_owner
              AND explanation_attempt_count = @attempt_count
              AND explanation_claim_expires_at > clock_timestamp()
            RETURNING event_id
        )
        SELECT EXISTS(SELECT 1 FROM applied)
            OR EXISTS(
                SELECT 1
                FROM release_risk_event_inbox
                WHERE event_id = @event_id
                  AND explanation_completed_at IS NULL
                  AND explanation_failed_at IS NOT NULL
                  AND explanation_failure_code = @failure_code
                  AND explanation_failure_reason = @failure_reason);
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
          AND explanation_failed_at IS NULL
          AND explanation_claimed_by = @claim_owner
          AND explanation_attempt_count = @attempt_count
        RETURNING event_id;
        """;

    private const string MarkReplayCompletedSql = """
        UPDATE release_risk_ai_explanation_replays
        SET
            completed_at = clock_timestamp(),
            explanation = @explanation,
            claimed_by = NULL,
            claim_expires_at = NULL
        WHERE replay_id = @replay_id
          AND event_id = @event_id
          AND generation = @generation
          AND completed_at IS NULL
          AND failed_at IS NULL
          AND claimed_by = @claim_owner
          AND attempt_count = @attempt_count
          AND claim_expires_at > clock_timestamp()
        RETURNING replay_id;
        """;

    private const string MarkReplayFailedSql = """
        UPDATE release_risk_ai_explanation_replays
        SET
            next_attempt_at = clock_timestamp() + @retry_delay,
            claimed_by = NULL,
            claim_expires_at = NULL
        WHERE replay_id = @replay_id
          AND event_id = @event_id
          AND generation = @generation
          AND completed_at IS NULL
          AND failed_at IS NULL
          AND claimed_by = @claim_owner
          AND attempt_count = @attempt_count
          AND claim_expires_at > clock_timestamp()
        RETURNING replay_id;
        """;

    private const string MarkReplayTerminalSql = """
        WITH applied AS (
            UPDATE release_risk_ai_explanation_replays
            SET
                failed_at = clock_timestamp(),
                failure_code = @failure_code,
                failure_reason = @failure_reason,
                claimed_by = NULL,
                claim_expires_at = NULL
            WHERE replay_id = @replay_id
              AND event_id = @event_id
              AND generation = @generation
              AND completed_at IS NULL
              AND failed_at IS NULL
              AND claimed_by = @claim_owner
              AND attempt_count = @attempt_count
              AND claim_expires_at > clock_timestamp()
            RETURNING replay_id
        )
        SELECT EXISTS(SELECT 1 FROM applied)
            OR EXISTS(
                SELECT 1
                FROM release_risk_ai_explanation_replays
                WHERE replay_id = @replay_id
                  AND event_id = @event_id
                  AND generation = @generation
                  AND completed_at IS NULL
                  AND failed_at IS NOT NULL
                  AND failure_code = @failure_code
                  AND failure_reason = @failure_reason);
        """;

    private const string ReleaseReplayClaimSql = """
        UPDATE release_risk_ai_explanation_replays
        SET
            next_attempt_at = LEAST(next_attempt_at, clock_timestamp()),
            claimed_by = NULL,
            claim_expires_at = NULL
        WHERE replay_id = @replay_id
          AND event_id = @event_id
          AND generation = @generation
          AND completed_at IS NULL
          AND failed_at IS NULL
          AND claimed_by = @claim_owner
          AND attempt_count = @attempt_count
        RETURNING replay_id;
        """;

    private const string ReadFailedWorkSql = """
        SELECT
            event_id,
            attempt_count,
            failed_at,
            failure_code,
            failure_reason,
            accepted_at,
            envelope::text
        FROM release_risk_ai_explanation_failed_work
        ORDER BY failed_at, event_id
        LIMIT @limit;
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
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        ValidateClaimRequest(
            claimOwner,
            batchSize,
            leaseDuration,
            maximumAttempts);

        await using var connection = await _dataSource.OpenConnectionAsync(
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            cancellationToken);
        var claims = new List<ReleaseRiskExplanationClaim>();

        await TerminalizeExhaustedReplayAsync(
            connection,
            transaction,
            batchSize,
            maximumAttempts,
            cancellationToken);
        await ClaimPendingReplayAsync(
            connection,
            transaction,
            claimOwner,
            batchSize,
            leaseDuration,
            maximumAttempts,
            claims,
            cancellationToken);

        var remainingBatchSize = batchSize - claims.Count;
        if (remainingBatchSize == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return claims;
        }

        await using (var exhaustedCommand = new NpgsqlCommand(
                         TerminalizeExhaustedSql,
                         connection,
                         transaction))
        {
            exhaustedCommand.Parameters.AddWithValue(
                "maximum_attempts",
                NpgsqlDbType.Integer,
                maximumAttempts);
            exhaustedCommand.Parameters.AddWithValue(
                "batch_size",
                NpgsqlDbType.Integer,
                remainingBatchSize);
            exhaustedCommand.Parameters.AddWithValue(
                "failure_code",
                NpgsqlDbType.Text,
                AiExplanationFailureClassifier.AttemptLimitExhaustedCode);
            exhaustedCommand.Parameters.AddWithValue(
                "failure_reason",
                NpgsqlDbType.Text,
                "Configured maximum attempt count was reached before a result was persisted.");
            await exhaustedCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = new NpgsqlCommand(
            ClaimPendingSql,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "claim_owner",
            NpgsqlDbType.Text,
            claimOwner);
        command.Parameters.AddWithValue(
            "batch_size",
            NpgsqlDbType.Integer,
            remainingBatchSize);
        command.Parameters.AddWithValue(
            "lease_duration",
            NpgsqlDbType.Interval,
            leaseDuration);
        command.Parameters.AddWithValue(
            "maximum_attempts",
            NpgsqlDbType.Integer,
            maximumAttempts);

        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
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

        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);

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
            MarkReplayCompletedSql,
            claim,
            explanation,
            retryDelay: null,
            failure: null,
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
            MarkReplayFailedSql,
            claim,
            explanation: null,
            retryDelay,
            failure: null,
            cancellationToken);
    }

    public Task<bool> MarkTerminalAsync(
        ReleaseRiskExplanationClaim claim,
        ReleaseRiskExplanationTerminalFailure failure,
        CancellationToken cancellationToken)
    {
        ValidateClaim(claim);
        ValidateTerminalFailure(failure);

        return ExecuteClaimUpdateAsync(
            MarkTerminalSql,
            MarkReplayTerminalSql,
            claim,
            explanation: null,
            retryDelay: null,
            failure,
            cancellationToken);
    }

    public Task<bool> ReleaseClaimAsync(
        ReleaseRiskExplanationClaim claim,
        CancellationToken cancellationToken)
    {
        ValidateClaim(claim);
        return ExecuteClaimUpdateAsync(
            ReleaseClaimSql,
            ReleaseReplayClaimSql,
            claim,
            explanation: null,
            retryDelay: null,
            failure: null,
            cancellationToken);
    }

    public async Task<IReadOnlyList<ReleaseRiskExplanationFailedWork>>
        ReadFailedWorkAsync(
            int limit,
            CancellationToken cancellationToken)
    {
        if (limit is < 1 or > MaximumFailedWorkQueryLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await using var command = _dataSource.CreateCommand(ReadFailedWorkSql);
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, limit);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        var failedWork = new List<ReleaseRiskExplanationFailedWork>();

        while (await reader.ReadAsync(cancellationToken))
        {
            failedWork.Add(new ReleaseRiskExplanationFailedWork(
                reader.GetGuid(0),
                reader.GetInt32(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                ReleaseRiskOutboxEnvelope.Deserialize(reader.GetString(6))));
        }

        return failedWork;
    }

    private async Task<bool> ExecuteClaimUpdateAsync(
        string sql,
        string replaySql,
        ReleaseRiskExplanationClaim claim,
        ReleaseRiskExplanation? explanation,
        TimeSpan? retryDelay,
        ReleaseRiskExplanationTerminalFailure? failure,
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            claim.ReplayId is null ? sql : replaySql);
        command.Parameters.AddWithValue("event_id", claim.EventId);
        command.Parameters.AddWithValue(
            "claim_owner",
            NpgsqlDbType.Text,
            claim.ClaimOwner);
        command.Parameters.AddWithValue(
            "attempt_count",
            NpgsqlDbType.Integer,
            claim.AttemptCount);

        if (claim.ReplayId is not null)
        {
            command.Parameters.AddWithValue("replay_id", claim.ReplayId.Value);
            command.Parameters.AddWithValue(
                "generation",
                NpgsqlDbType.Integer,
                claim.Generation);
        }

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

        if (failure is not null)
        {
            command.Parameters.AddWithValue(
                "failure_code",
                NpgsqlDbType.Text,
                failure.Code);
            command.Parameters.AddWithValue(
                "failure_reason",
                NpgsqlDbType.Text,
                failure.Reason);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result switch
        {
            bool applied => applied,
            null => false,
            _ => true
        };
    }

    private static void ValidateClaimRequest(
        string claimOwner,
        int batchSize,
        TimeSpan leaseDuration,
        int maximumAttempts)
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

        if (maximumAttempts is < 1 or > AiExplanationProcessorOptions.MaximumAllowedAttempts)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }
    }

    private static async Task TerminalizeExhaustedReplayAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int batchSize,
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            TerminalizeExhaustedReplaySql,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "maximum_attempts",
            NpgsqlDbType.Integer,
            maximumAttempts);
        command.Parameters.AddWithValue(
            "batch_size",
            NpgsqlDbType.Integer,
            batchSize);
        command.Parameters.AddWithValue(
            "failure_code",
            NpgsqlDbType.Text,
            AiExplanationFailureClassifier.AttemptLimitExhaustedCode);
        command.Parameters.AddWithValue(
            "failure_reason",
            NpgsqlDbType.Text,
            "Configured maximum attempt count was reached before a replay result was persisted.");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ClaimPendingReplayAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string claimOwner,
        int batchSize,
        TimeSpan leaseDuration,
        int maximumAttempts,
        List<ReleaseRiskExplanationClaim> claims,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            ClaimPendingReplaySql,
            connection,
            transaction);
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
        command.Parameters.AddWithValue(
            "maximum_attempts",
            NpgsqlDbType.Integer,
            maximumAttempts);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var claim = new ReleaseRiskExplanationClaim(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetInt32(2),
                ReleaseRiskOutboxEnvelope.Deserialize(reader.GetString(3)),
                reader.GetGuid(4),
                reader.GetInt32(5));
            ValidateClaim(claim);
            claims.Add(claim);
        }
    }

    private static void ValidateTerminalFailure(
        ReleaseRiskExplanationTerminalFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        if (string.IsNullOrWhiteSpace(failure.Code) ||
            failure.Code.Length > 64 ||
            !char.IsAsciiLetterLower(failure.Code[0]) ||
            failure.Code.Any(character =>
                !(char.IsAsciiLetterLower(character) ||
                  char.IsAsciiDigit(character) ||
                  character == '_')) ||
            string.IsNullOrWhiteSpace(failure.Reason) ||
            Encoding.UTF8.GetByteCount(failure.Reason) > 1024)
        {
            throw new ArgumentException(
                "Terminal failure must contain a bounded snake-case code and a non-empty bounded reason.",
                nameof(failure));
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
            !claim.Envelope.IsValidVersionOneContract() ||
            (claim.ReplayId is null && claim.Generation != 0) ||
            (claim.ReplayId is not null && claim.Generation < 1))
        {
            throw new ArgumentException(
                "The explanation claim must contain a valid owner, attempt and matching V1 event snapshot.",
                nameof(claim));
        }
    }
}
