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
            event_id,
            explanation_completed_at,
            explanation::text,
            explanation_failed_at,
            explanation_failure_code,
            explanation_failure_reason
        FROM release_risk_event_inbox
        WHERE event_id = @event_id;
        """;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

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

        var storedEventId = reader.GetGuid(0);
        if (storedEventId != eventId)
        {
            throw new InvalidOperationException(
                $"AI explanation query returned event '{storedEventId:D}' for requested event '{eventId:D}'.");
        }

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
            $"AI explanation state for event '{eventId:D}' is inconsistent.");
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
