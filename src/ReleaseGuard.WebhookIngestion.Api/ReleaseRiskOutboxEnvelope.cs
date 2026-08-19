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
}
