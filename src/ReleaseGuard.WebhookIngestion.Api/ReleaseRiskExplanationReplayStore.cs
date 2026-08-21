using Npgsql;
using NpgsqlTypes;

namespace ReleaseGuard.WebhookIngestion.Api;

public enum ReleaseRiskExplanationReplayDisposition
{
    Accepted,
    Duplicate,
    EventNotFound,
    NotEligible,
    ReplayIdConflict
}

public sealed record ReleaseRiskExplanationReplayReceipt(
    Guid ReplayId,
    Guid EventId,
    int Generation,
    DateTimeOffset RequestedAt,
    ReleaseRiskExplanationReplayDisposition Disposition);

public interface IReleaseRiskExplanationReplayStore
{
    Task<ReleaseRiskExplanationReplayReceipt> RequestReplayAsync(
        Guid eventId,
        Guid replayId,
        CancellationToken cancellationToken);
}

public sealed class PostgreSqlReleaseRiskExplanationReplayStore :
    IReleaseRiskExplanationReplayStore
{
    private const string LockReplayIdSql = """
        SELECT pg_advisory_xact_lock(
            hashtextextended(@replay_id::text, 0));
        """;

    private const string ReadReplayIdSql = """
        SELECT event_id, generation, requested_at
        FROM release_risk_ai_explanation_replays
        WHERE replay_id = @replay_id
        FOR UPDATE;
        """;

    private const string ReadInboxSql = """
        SELECT
            explanation_completed_at,
            explanation_failed_at,
            explanation_failure_code,
            explanation_failure_reason,
            envelope::text
        FROM release_risk_event_inbox
        WHERE event_id = @event_id
        FOR UPDATE;
        """;

    private const string ReadLatestReplaySql = """
        SELECT
            generation,
            completed_at,
            failed_at,
            failure_code,
            failure_reason
        FROM release_risk_ai_explanation_replays
        WHERE event_id = @event_id
        ORDER BY generation DESC
        LIMIT 1;
        """;

    private const string InsertReplaySql = """
        INSERT INTO release_risk_ai_explanation_replays (
            replay_id,
            event_id,
            generation,
            prior_failed_at,
            prior_failure_code,
            prior_failure_reason,
            envelope)
        VALUES (
            @replay_id,
            @event_id,
            @generation,
            @prior_failed_at,
            @prior_failure_code,
            @prior_failure_reason,
            @envelope)
        RETURNING requested_at;
        """;

    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlReleaseRiskExplanationReplayStore(
        NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<ReleaseRiskExplanationReplayReceipt> RequestReplayAsync(
        Guid eventId,
        Guid replayId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            cancellationToken);

        await using (var lockCommand = new NpgsqlCommand(
                         LockReplayIdSql,
                         connection,
                         transaction))
        {
            lockCommand.Parameters.AddWithValue("replay_id", replayId);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var existing = await ReadExistingAsync(
            connection,
            transaction,
            replayId,
            cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing.EventId == eventId
                ? existing with
                {
                    Disposition =
                        ReleaseRiskExplanationReplayDisposition.Duplicate
                }
                : existing with
                {
                    EventId = eventId,
                    Disposition =
                        ReleaseRiskExplanationReplayDisposition.ReplayIdConflict
                };
        }

        var inbox = await ReadInboxAsync(
            connection,
            transaction,
            eventId,
            cancellationToken);
        if (inbox is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new ReleaseRiskExplanationReplayReceipt(
                replayId,
                eventId,
                0,
                default,
                ReleaseRiskExplanationReplayDisposition.EventNotFound);
        }

        var latest = await ReadLatestAsync(
            connection,
            transaction,
            eventId,
            cancellationToken);
        var priorFailure = latest is null
            ? inbox.Failure
            : latest.Failure;
        var eligible = latest is null
            ? !inbox.Completed && priorFailure is not null
            : !latest.Completed && priorFailure is not null;
        if (!eligible || priorFailure is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new ReleaseRiskExplanationReplayReceipt(
                replayId,
                eventId,
                latest?.Generation ?? 0,
                default,
                ReleaseRiskExplanationReplayDisposition.NotEligible);
        }

        var generation = checked((latest?.Generation ?? 0) + 1);
        await using var insert = new NpgsqlCommand(
            InsertReplaySql,
            connection,
            transaction);
        insert.Parameters.AddWithValue("replay_id", replayId);
        insert.Parameters.AddWithValue("event_id", eventId);
        insert.Parameters.AddWithValue(
            "generation",
            NpgsqlDbType.Integer,
            generation);
        insert.Parameters.AddWithValue(
            "prior_failed_at",
            NpgsqlDbType.TimestampTz,
            priorFailure.FailedAt);
        insert.Parameters.AddWithValue(
            "prior_failure_code",
            NpgsqlDbType.Text,
            priorFailure.Code);
        insert.Parameters.AddWithValue(
            "prior_failure_reason",
            NpgsqlDbType.Text,
            priorFailure.Reason);
        insert.Parameters.AddWithValue(
            "envelope",
            NpgsqlDbType.Jsonb,
            inbox.EnvelopeJson);
        await using var insertedReader = await insert.ExecuteReaderAsync(
            cancellationToken);
        if (!await insertedReader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "Replay insert did not return its request timestamp.");
        }

        var requestedAt = insertedReader.GetFieldValue<DateTimeOffset>(0);
        await insertedReader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);

        return new ReleaseRiskExplanationReplayReceipt(
            replayId,
            eventId,
            generation,
            requestedAt,
            ReleaseRiskExplanationReplayDisposition.Accepted);
    }

    private static async Task<ReleaseRiskExplanationReplayReceipt?>
        ReadExistingAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid replayId,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            ReadReplayIdSql,
            connection,
            transaction);
        command.Parameters.AddWithValue("replay_id", replayId);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ReleaseRiskExplanationReplayReceipt(
            replayId,
            reader.GetGuid(0),
            reader.GetInt32(1),
            reader.GetFieldValue<DateTimeOffset>(2),
            ReleaseRiskExplanationReplayDisposition.Duplicate);
    }

    private static async Task<InboxState?> ReadInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            ReadInboxSql,
            connection,
            transaction);
        command.Parameters.AddWithValue("event_id", eventId);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new InboxState(
            !reader.IsDBNull(0),
            ReadFailure(reader, 1, 2, 3),
            reader.GetString(4));
    }

    private static async Task<ReplayState?> ReadLatestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            ReadLatestReplaySql,
            connection,
            transaction);
        command.Parameters.AddWithValue("event_id", eventId);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ReplayState(
            reader.GetInt32(0),
            !reader.IsDBNull(1),
            ReadFailure(reader, 2, 3, 4));
    }

    private static StoredFailure? ReadFailure(
        NpgsqlDataReader reader,
        int failedAtOrdinal,
        int codeOrdinal,
        int reasonOrdinal)
    {
        if (reader.IsDBNull(failedAtOrdinal))
        {
            return null;
        }

        if (reader.IsDBNull(codeOrdinal) || reader.IsDBNull(reasonOrdinal))
        {
            throw new InvalidOperationException(
                "Stored AI explanation failure is incomplete.");
        }

        return new StoredFailure(
            reader.GetFieldValue<DateTimeOffset>(failedAtOrdinal),
            reader.GetString(codeOrdinal),
            reader.GetString(reasonOrdinal));
    }

    private sealed record StoredFailure(
        DateTimeOffset FailedAt,
        string Code,
        string Reason);

    private sealed record InboxState(
        bool Completed,
        StoredFailure? Failure,
        string EnvelopeJson);

    private sealed record ReplayState(
        int Generation,
        bool Completed,
        StoredFailure? Failure);
}
