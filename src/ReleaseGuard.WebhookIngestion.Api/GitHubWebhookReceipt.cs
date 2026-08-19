namespace ReleaseGuard.WebhookIngestion.Api;

public sealed record GitHubWebhookReceipt(
    Guid DeliveryId,
    string EventName,
    string Status)
{
    public static GitHubWebhookReceipt Accepted(VerifiedGitHubWebhook webhook) =>
        new(webhook.DeliveryId, webhook.EventName, "accepted");

    public static GitHubWebhookReceipt Duplicate(VerifiedGitHubWebhook webhook) =>
        new(webhook.DeliveryId, webhook.EventName, "duplicate");
}
