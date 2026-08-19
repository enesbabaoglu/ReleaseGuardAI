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

        var insertedDeliveryId = await command.ExecuteScalarAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return insertedDeliveryId is not null;
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
