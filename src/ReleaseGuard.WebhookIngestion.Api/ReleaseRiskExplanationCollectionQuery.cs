using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;
using NpgsqlTypes;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed record ReleaseRiskExplanationListCursor(
    DateTimeOffset AcceptedAt,
    Guid EventId);

public sealed record ReleaseRiskExplanationListItem(
    Guid EventId,
    string Status,
    DateTimeOffset AcceptedAt,
    string Repository,
    long ChangeNumber,
    string Kind);

public sealed record ReleaseRiskExplanationListPage(
    IReadOnlyList<ReleaseRiskExplanationListItem> Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? NextCursor);

public sealed record LatestAcceptedReleaseRiskExplanation(
    DateTimeOffset AcceptedAt,
    string Repository,
    long ChangeNumber,
    string Kind,
    ReleaseRiskExplanationQuerySnapshot Snapshot);

public interface IReleaseRiskExplanationCollectionQuery
{
    Task<ReleaseRiskExplanationListPage> ReadPageAsync(
        int limit,
        ReleaseRiskExplanationListCursor? cursor,
        CancellationToken cancellationToken);

    Task<LatestAcceptedReleaseRiskExplanation?> ReadLatestAcceptedAsync(
        string repository,
        long changeNumber,
        CancellationToken cancellationToken);
}

public static class ReleaseRiskExplanationListCursorCodec
{
    public static string Encode(ReleaseRiskExplanationListCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        var value = string.Create(
            CultureInfo.InvariantCulture,
            $"{cursor.AcceptedAt:O}|{cursor.EventId:D}");
        return WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(value));
    }

    public static bool TryDecode(
        string? encoded,
        out ReleaseRiskExplanationListCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > 256)
        {
            return false;
        }

        try
        {
            var value = Encoding.UTF8.GetString(
                WebEncoders.Base64UrlDecode(encoded));
            var separator = value.IndexOf('|');
            if (separator <= 0 ||
                separator != value.LastIndexOf('|') ||
                !DateTimeOffset.TryParseExact(
                    value.AsSpan(0, separator),
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var acceptedAt) ||
                !Guid.TryParseExact(
                    value.AsSpan(separator + 1),
                    "D",
                    out var eventId))
            {
                return false;
            }

            var parsed = new ReleaseRiskExplanationListCursor(
                acceptedAt,
                eventId);
            if (!string.Equals(Encode(parsed), encoded, StringComparison.Ordinal))
            {
                return false;
            }

            cursor = parsed;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed class PostgreSqlReleaseRiskExplanationCollectionQuery :
    IReleaseRiskExplanationCollectionQuery
{
    public const int DefaultLimit = 50;
    public const int MaximumLimit = 100;

    private const string ReadPageSql = """
        SELECT
            inbox.event_id,
            CASE
                WHEN replay.replay_id IS NOT NULL AND replay.completed_at IS NOT NULL
                    THEN 'completed'
                WHEN replay.replay_id IS NOT NULL AND replay.failed_at IS NOT NULL
                    THEN 'failed'
                WHEN replay.replay_id IS NOT NULL THEN 'pending'
                WHEN inbox.explanation_completed_at IS NOT NULL THEN 'completed'
                WHEN inbox.explanation_failed_at IS NOT NULL THEN 'failed'
                ELSE 'pending'
            END AS status,
            inbox.accepted_at,
            inbox.envelope #>> '{riskInput,repository}' AS repository,
            inbox.envelope #>> '{riskInput,changeNumber}' AS change_number,
            inbox.event_kind
        FROM release_risk_event_inbox AS inbox
        LEFT JOIN LATERAL (
            SELECT replay_id, completed_at, failed_at
            FROM release_risk_ai_explanation_replays
            WHERE event_id = inbox.event_id
            ORDER BY generation DESC
            LIMIT 1
        ) AS replay ON TRUE
        WHERE @cursor_accepted_at IS NULL
           OR (inbox.accepted_at, inbox.event_id) <
              (@cursor_accepted_at::timestamptz, @cursor_event_id::uuid)
        ORDER BY inbox.accepted_at DESC, inbox.event_id DESC
        LIMIT @fetch_limit;
        """;

    private const string ReadLatestAcceptedSql = """
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
            END,
            inbox.accepted_at,
            inbox.envelope #>> '{riskInput,repository}' AS repository,
            inbox.envelope #>> '{riskInput,changeNumber}' AS change_number,
            inbox.event_kind
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
        WHERE inbox.envelope #>> '{riskInput,repository}' = @repository
          AND inbox.envelope #>> '{riskInput,changeNumber}' = @change_number
        ORDER BY inbox.accepted_at DESC, inbox.event_id DESC
        LIMIT 1;
        """;

    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlReleaseRiskExplanationCollectionQuery(
        NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<ReleaseRiskExplanationListPage> ReadPageAsync(
        int limit,
        ReleaseRiskExplanationListCursor? cursor,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > MaximumLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await using var command = _dataSource.CreateCommand(ReadPageSql);
        command.Parameters.AddWithValue(
            "cursor_accepted_at",
            NpgsqlDbType.TimestampTz,
            cursor is null ? DBNull.Value : cursor.AcceptedAt);
        command.Parameters.AddWithValue(
            "cursor_event_id",
            NpgsqlDbType.Uuid,
            cursor is null ? DBNull.Value : cursor.EventId);
        command.Parameters.AddWithValue(
            "fetch_limit",
            NpgsqlDbType.Integer,
            limit + 1);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        var items = new List<ReleaseRiskExplanationListItem>(limit + 1);

        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ReleaseRiskExplanationListItem(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                ReadRequiredString(reader, 3, "repository"),
                ReadRequiredInt64(reader, 4, "changeNumber"),
                reader.GetString(5)));
        }

        string? nextCursor = null;
        if (items.Count > limit)
        {
            items.RemoveAt(limit);
            var last = items[^1];
            nextCursor = ReleaseRiskExplanationListCursorCodec.Encode(
                new ReleaseRiskExplanationListCursor(
                    last.AcceptedAt,
                    last.EventId));
        }

        return new ReleaseRiskExplanationListPage(
            items.AsReadOnly(),
            nextCursor);
    }

    public async Task<LatestAcceptedReleaseRiskExplanation?>
        ReadLatestAcceptedAsync(
            string repository,
            long changeNumber,
            CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        if (changeNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(changeNumber));
        }

        await using var command = _dataSource.CreateCommand(
            ReadLatestAcceptedSql);
        command.Parameters.AddWithValue(
            "repository",
            NpgsqlDbType.Text,
            repository);
        command.Parameters.AddWithValue(
            "change_number",
            NpgsqlDbType.Text,
            changeNumber.ToString(CultureInfo.InvariantCulture));
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new LatestAcceptedReleaseRiskExplanation(
            reader.GetFieldValue<DateTimeOffset>(6),
            ReadRequiredString(reader, 7, "repository"),
            ReadRequiredInt64(reader, 8, "changeNumber"),
            reader.GetString(9),
            ReleaseRiskExplanationQuerySnapshotReader.Read(reader));
    }

    private static string ReadRequiredString(
        NpgsqlDataReader reader,
        int ordinal,
        string fieldName)
    {
        if (reader.IsDBNull(ordinal))
        {
            throw new InvalidOperationException(
                $"Stored V1 envelope is missing required {fieldName}.");
        }

        return reader.GetString(ordinal);
    }

    private static long ReadRequiredInt64(
        NpgsqlDataReader reader,
        int ordinal,
        string fieldName)
    {
        var value = ReadRequiredString(reader, ordinal, fieldName);
        if (!long.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed) ||
            parsed < 1)
        {
            throw new InvalidOperationException(
                $"Stored V1 envelope contains an invalid {fieldName}.");
        }

        return parsed;
    }
}
