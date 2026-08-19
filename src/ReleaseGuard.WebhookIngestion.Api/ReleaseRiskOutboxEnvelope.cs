using System.Text.Json;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed record ReleaseRiskOutboxEnvelope(
    Guid EventId,
    string EventType,
    int SchemaVersion,
    string SourceProvider,
    string Kind,
    ReleaseRiskInput RiskInput,
    ReleaseRiskAssessment RiskAssessment)
{
    public const string CurrentEventType = "releaseguard.release-risk-assessed";
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static ReleaseRiskOutboxEnvelope Create(
        Guid eventId,
        ReleaseRiskInput riskInput,
        ReleaseRiskAssessment riskAssessment)
    {
        ArgumentNullException.ThrowIfNull(riskInput);
        ArgumentNullException.ThrowIfNull(riskAssessment);

        if (riskInput.SourceDeliveryId != eventId)
        {
            throw new ArgumentException(
                "The risk input source delivery ID must match the outbox event ID.",
                nameof(riskInput));
        }

        return new ReleaseRiskOutboxEnvelope(
            eventId,
            CurrentEventType,
            CurrentSchemaVersion,
            riskInput.SourceProvider,
            riskInput.Kind,
            riskInput,
            riskAssessment);
    }

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    public byte[] SerializeToUtf8Bytes() =>
        JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);

    public bool IsValidVersionOneContract() =>
        string.Equals(
            EventType,
            CurrentEventType,
            StringComparison.Ordinal) &&
        SchemaVersion == CurrentSchemaVersion &&
        RiskInput is not null &&
        RiskAssessment is not null &&
        EventId == RiskInput.SourceDeliveryId &&
        string.Equals(
            SourceProvider,
            RiskInput.SourceProvider,
            StringComparison.Ordinal) &&
        string.Equals(
            Kind,
            RiskInput.Kind,
            StringComparison.Ordinal);

    public static ReleaseRiskOutboxEnvelope Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<ReleaseRiskOutboxEnvelope>(json, JsonOptions)
            ?? throw new JsonException(
                "The release risk outbox envelope deserialized to null.");
    }

    public static ReleaseRiskOutboxEnvelope Deserialize(
        ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize<ReleaseRiskOutboxEnvelope>(utf8Json, JsonOptions)
        ?? throw new JsonException(
            "The release risk outbox envelope deserialized to null.");
}
