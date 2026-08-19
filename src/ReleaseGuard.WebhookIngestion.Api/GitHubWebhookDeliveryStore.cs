using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace ReleaseGuard.WebhookIngestion.Api;

public interface IGitHubWebhookDeliveryStore
{
    Task<bool> TryAcceptAsync(
        VerifiedGitHubWebhook webhook,
        ReleaseRiskInput? riskInput,
        ReleaseRiskAssessment? riskAssessment,
        CancellationToken cancellationToken);
}

public sealed class PostgreSqlGitHubWebhookDeliveryStore : IGitHubWebhookDeliveryStore
{
    private const string InsertDeliverySql = """
        INSERT INTO github_webhook_deliveries (
            delivery_id,
            event_name,
            payload,
            disposition,
            risk_input,
            risk_assessment)
        VALUES (
            @delivery_id,
            @event_name,
            @payload,
            @disposition,
            @risk_input,
            @risk_assessment)
        ON CONFLICT (delivery_id) DO NOTHING
        RETURNING delivery_id;
        """;

    private const string InsertOutboxMessageSql = """
        INSERT INTO release_risk_outbox_messages (
            event_id,
            delivery_disposition,
            event_type,
            schema_version,
            source_provider,
            event_kind,
            envelope)
        VALUES (
            @event_id,
            'accepted',
            @event_type,
            @schema_version,
            @source_provider,
            @event_kind,
            @envelope);
        """;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlGitHubWebhookDeliveryStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<bool> TryAcceptAsync(
        VerifiedGitHubWebhook webhook,
        ReleaseRiskInput? riskInput,
        ReleaseRiskAssessment? riskAssessment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(webhook);

        if ((riskInput is null) != (riskAssessment is null))
        {
            throw new ArgumentException(
                "Risk input and assessment must either both be present or both be absent.");
        }

        var disposition = riskInput is null ? "ignored" : "accepted";
        var riskInputJson = SerializeOrNull(riskInput);
        var riskAssessmentJson = SerializeOrNull(riskAssessment);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var insertedDeliveryId = await InsertDeliveryAsync(
            connection,
            transaction,
            webhook,
            disposition,
            riskInputJson,
            riskAssessmentJson,
            cancellationToken);

        if (insertedDeliveryId is not null &&
            riskInput is not null &&
            riskAssessment is not null)
        {
            await InsertOutboxMessageAsync(
                connection,
                transaction,
                ReleaseRiskOutboxEnvelope.Create(
                    webhook.DeliveryId,
                    riskInput,
                    riskAssessment),
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return insertedDeliveryId is not null;
    }

    private static async Task<object?> InsertDeliveryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        VerifiedGitHubWebhook webhook,
        string disposition,
        string? riskInputJson,
        string? riskAssessmentJson,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            InsertDeliverySql,
            connection,
            transaction);

        command.Parameters.AddWithValue("delivery_id", webhook.DeliveryId);
        command.Parameters.AddWithValue("event_name", webhook.EventName);
        command.Parameters.AddWithValue(
            "payload",
            NpgsqlDbType.Jsonb,
            webhook.Payload.GetRawText());
        command.Parameters.AddWithValue("disposition", disposition);
        AddNullableJsonParameter(command, "risk_input", riskInputJson);
        AddNullableJsonParameter(command, "risk_assessment", riskAssessmentJson);

        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task InsertOutboxMessageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ReleaseRiskOutboxEnvelope envelope,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            InsertOutboxMessageSql,
            connection,
            transaction);

        command.Parameters.AddWithValue("event_id", envelope.EventId);
        command.Parameters.AddWithValue("event_type", envelope.EventType);
        command.Parameters.AddWithValue("schema_version", envelope.SchemaVersion);
        command.Parameters.AddWithValue("source_provider", envelope.SourceProvider);
        command.Parameters.AddWithValue("event_kind", envelope.Kind);
        command.Parameters.AddWithValue(
            "envelope",
            NpgsqlDbType.Jsonb,
            envelope.Serialize());

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? SerializeOrNull<T>(T? value)
        where T : class =>
        value is null ? null : JsonSerializer.Serialize(value, JsonOptions);

    private static void AddNullableJsonParameter(
        NpgsqlCommand command,
        string parameterName,
        string? value)
    {
        var parameter = command.Parameters.Add(parameterName, NpgsqlDbType.Jsonb);
        parameter.Value = value is null ? DBNull.Value : value;
    }
}
