using System.Text.Json;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed record VerifiedGitHubWebhook(
    Guid DeliveryId,
    string EventName,
    JsonElement Payload);
