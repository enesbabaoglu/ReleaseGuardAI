using System.Text.Json;
using Npgsql;

namespace ReleaseGuard.WebhookIngestion.Api;

public abstract record ReleaseRiskExplanationQuerySnapshot(Guid EventId)
{
    public abstract string Status { get; }
}

public sealed record PendingReleaseRiskExplanationQuerySnapshot(Guid EventId) :
    ReleaseRiskExplanationQuerySnapshot(EventId)
{
    public override string Status => "pending";
}

public sealed record CompletedReleaseRiskExplanationQuerySnapshot(
    Guid EventId,
    ReleaseRiskExplanation Explanation) :
    ReleaseRiskExplanationQuerySnapshot(EventId)
{
    public override string Status => "completed";
}

public sealed record FailedReleaseRiskExplanationQuerySnapshot(
    Guid EventId,
    ReleaseRiskExplanationTerminalFailure Failure) :
    ReleaseRiskExplanationQuerySnapshot(EventId)
{
    public override string Status => "failed";
}

public interface IReleaseRiskExplanationQuery
{
    Task<ReleaseRiskExplanationQuerySnapshot?> ReadAsync(
        Guid eventId,
        CancellationToken cancellationToken);
}

public sealed class PostgreSqlReleaseRiskExplanationQuery :
    IReleaseRiskExplanationQuery
{
    private const string ReadSql = """
        SELECT
            inbox.event_id,
            CASE WHEN replay.replay_id IS NULL
                THEN inbox.explanation_completed_at
                ELSE replay.completed_at
            END,
            CASE WHEN replay.replay_id IS NULL
                THEN inbox.explanation::text
                ELSE replay.explanation::text
            END,
            CASE WHEN replay.replay_id IS NULL
                THEN inbox.explanation_failed_at
                ELSE replay.failed_at
            END,
            CASE WHEN replay.replay_id IS NULL
                THEN inbox.explanation_failure_code
                ELSE replay.failure_code
            END,
            CASE WHEN replay.replay_id IS NULL
                THEN inbox.explanation_failure_reason
                ELSE replay.failure_reason
            END
        FROM release_risk_event_inbox AS inbox
        LEFT JOIN LATERAL (
            SELECT
                replay_id,
                completed_at,
                explanation,
                failed_at,
                failure_code,
                failure_reason
            FROM release_risk_ai_explanation_replays
            WHERE event_id = inbox.event_id
            ORDER BY generation DESC
            LIMIT 1
        ) AS replay ON TRUE
        WHERE inbox.event_id = @event_id;
        """;

    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlReleaseRiskExplanationQuery(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<ReleaseRiskExplanationQuerySnapshot?> ReadAsync(
        Guid eventId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var command = _dataSource.CreateCommand(ReadSql);
        command.Parameters.AddWithValue("event_id", eventId);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var snapshot = ReleaseRiskExplanationQuerySnapshotReader.Read(reader);
        if (snapshot.EventId != eventId)
        {
            throw new InvalidOperationException(
                $"AI explanation query returned event '{snapshot.EventId:D}' for requested event '{eventId:D}'.");
        }

        return snapshot;
    }
}

internal static class ReleaseRiskExplanationQuerySnapshotReader
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static ReleaseRiskExplanationQuerySnapshot Read(
        NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var storedEventId = reader.GetGuid(0);
        var completed = !reader.IsDBNull(1);
        var explanationJson = reader.IsDBNull(2) ? null : reader.GetString(2);
        var failed = !reader.IsDBNull(3);
        var failureCode = reader.IsDBNull(4) ? null : reader.GetString(4);
        var failureReason = reader.IsDBNull(5) ? null : reader.GetString(5);

        if (!completed &&
            explanationJson is null &&
            !failed &&
            failureCode is null &&
            failureReason is null)
        {
            return new PendingReleaseRiskExplanationQuerySnapshot(storedEventId);
        }

        if (completed &&
            explanationJson is not null &&
            !failed &&
            failureCode is null &&
            failureReason is null)
        {
            var explanation = DeserializeExplanation(
                explanationJson,
                storedEventId);
            return new CompletedReleaseRiskExplanationQuerySnapshot(
                storedEventId,
                explanation);
        }

        if (!completed &&
            explanationJson is null &&
            failed &&
            failureCode is not null &&
            failureReason is not null)
        {
            return new FailedReleaseRiskExplanationQuerySnapshot(
                storedEventId,
                new ReleaseRiskExplanationTerminalFailure(
                    failureCode,
                    failureReason));
        }

        throw new InvalidOperationException(
            $"AI explanation state for event '{storedEventId:D}' is inconsistent.");
    }

    private static ReleaseRiskExplanation DeserializeExplanation(
        string json,
        Guid eventId)
    {
        var explanation = JsonSerializer.Deserialize<ReleaseRiskExplanation>(
                json,
                JsonOptions)
            ?? throw new JsonException(
                "The stored AI explanation deserialized to null.");

        if (!explanation.IsValidFor(eventId))
        {
            throw new InvalidOperationException(
                $"Stored AI explanation for event '{eventId:D}' violates the event-bound contract.");
        }

        return explanation with
        {
            Recommendations = Array.AsReadOnly(
                explanation.Recommendations.ToArray())
        };
    }
}
