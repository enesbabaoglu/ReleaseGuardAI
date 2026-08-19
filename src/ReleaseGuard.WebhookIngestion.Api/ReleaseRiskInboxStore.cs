using Npgsql;
using NpgsqlTypes;

namespace ReleaseGuard.WebhookIngestion.Api;

public enum ReleaseRiskInboxAcceptance
{
    Accepted,
    Duplicate
}

public interface IReleaseRiskInboxStore
{
    Task<ReleaseRiskInboxAcceptance> AcceptAsync(
        ConsumedReleaseRiskEvent consumedEvent,
        CancellationToken cancellationToken);
}

public sealed class ReleaseRiskInboxConflictException : Exception
{
    public ReleaseRiskInboxConflictException(Guid eventId)
        : base(
            $"Inbox event '{eventId}' already exists with a different V1 payload.")
    {
    }
}

public sealed class PostgreSqlReleaseRiskInboxStore : IReleaseRiskInboxStore
{
    private const string InsertSql = """
        INSERT INTO release_risk_event_inbox (
            event_id,
            message_key,
            topic,
            kafka_partition,
            kafka_offset,
            event_type,
            schema_version,
            source_provider,
            event_kind,
            payload,
            envelope)
        VALUES (
            @event_id,
            @message_key,
            @topic,
            @kafka_partition,
            @kafka_offset,
            @event_type,
            @schema_version,
            @source_provider,
            @event_kind,
            @payload,
            @envelope)
        ON CONFLICT DO NOTHING
        RETURNING event_id;
        """;

    private const string ReadExistingPayloadSql = """
        SELECT payload
        FROM release_risk_event_inbox
        WHERE event_id = @event_id;
        """;

    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlReleaseRiskInboxStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<ReleaseRiskInboxAcceptance> AcceptAsync(
        ConsumedReleaseRiskEvent consumedEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumedEvent);
        Validate(consumedEvent);

        await using var connection = await _dataSource.OpenConnectionAsync(
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            cancellationToken);
        var inserted = await TryInsertAsync(
            connection,
            transaction,
            consumedEvent,
            cancellationToken);

        if (!inserted)
        {
            var existingPayload = await ReadExistingPayloadAsync(
                connection,
                transaction,
                consumedEvent.MessageKey,
                cancellationToken);
            if (existingPayload is null ||
                !existingPayload.AsSpan().SequenceEqual(consumedEvent.Payload))
            {
                throw new ReleaseRiskInboxConflictException(
                    consumedEvent.MessageKey);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return inserted
            ? ReleaseRiskInboxAcceptance.Accepted
            : ReleaseRiskInboxAcceptance.Duplicate;
    }

    private static async Task<bool> TryInsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ConsumedReleaseRiskEvent consumedEvent,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            InsertSql,
            connection,
            transaction);
        command.Parameters.AddWithValue("event_id", consumedEvent.Envelope.EventId);
        command.Parameters.AddWithValue("message_key", consumedEvent.MessageKey);
        command.Parameters.AddWithValue("topic", consumedEvent.Topic);
        command.Parameters.AddWithValue("kafka_partition", consumedEvent.Partition);
        command.Parameters.AddWithValue("kafka_offset", consumedEvent.Offset);
        command.Parameters.AddWithValue(
            "event_type",
            consumedEvent.Envelope.EventType);
        command.Parameters.AddWithValue(
            "schema_version",
            consumedEvent.Envelope.SchemaVersion);
        command.Parameters.AddWithValue(
            "source_provider",
            consumedEvent.Envelope.SourceProvider);
        command.Parameters.AddWithValue(
            "event_kind",
            consumedEvent.Envelope.Kind);
        command.Parameters.AddWithValue(
            "payload",
            NpgsqlDbType.Bytea,
            consumedEvent.Payload);
        command.Parameters.AddWithValue(
            "envelope",
            NpgsqlDbType.Jsonb,
            consumedEvent.Envelope.Serialize());

        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<byte[]?> ReadExistingPayloadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            ReadExistingPayloadSql,
            connection,
            transaction);
        command.Parameters.AddWithValue("event_id", eventId);
        return await command.ExecuteScalarAsync(cancellationToken) as byte[];
    }

    private static void Validate(ConsumedReleaseRiskEvent consumedEvent)
    {
        ReleaseRiskOutboxEnvelope payloadEnvelope;
        try
        {
            payloadEnvelope = ReleaseRiskOutboxEnvelope.Deserialize(
                consumedEvent.Payload);
        }
        catch (Exception exception)
            when (exception is System.Text.Json.JsonException or
                  NotSupportedException)
        {
            throw new ArgumentException(
                "Only a validated V1 Kafka record can be accepted into the inbox.",
                nameof(consumedEvent),
                exception);
        }

        if (!KafkaProducerOptions.HasValidTopic(consumedEvent.Topic) ||
            consumedEvent.Partition < 0 ||
            consumedEvent.Offset < 0 ||
            consumedEvent.Payload is not { Length: > 0 } ||
            consumedEvent.Envelope is null ||
            consumedEvent.MessageKey != consumedEvent.Envelope.EventId ||
            !consumedEvent.Envelope.IsValidVersionOneContract() ||
            !payloadEnvelope.IsValidVersionOneContract() ||
            payloadEnvelope.EventId != consumedEvent.MessageKey ||
            !string.Equals(
                payloadEnvelope.Serialize(),
                consumedEvent.Envelope.Serialize(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Only a validated V1 Kafka record can be accepted into the inbox.",
                nameof(consumedEvent));
        }
    }
}
