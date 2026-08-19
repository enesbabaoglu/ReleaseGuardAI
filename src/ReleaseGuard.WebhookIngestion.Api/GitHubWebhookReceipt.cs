namespace ReleaseGuard.WebhookIngestion.Api;

public sealed record GitHubWebhookReceipt(
    Guid DeliveryId,
    string EventName,
    string Status,
    ReleaseRiskInput? RiskInput)
{
    public static GitHubWebhookReceipt Accepted(
        VerifiedGitHubWebhook webhook,
        ReleaseRiskInput riskInput) =>
        new(webhook.DeliveryId, webhook.EventName, "accepted", riskInput);

    public static GitHubWebhookReceipt Ignored(VerifiedGitHubWebhook webhook) =>
        new(webhook.DeliveryId, webhook.EventName, "ignored", null);

    public static GitHubWebhookReceipt Duplicate(VerifiedGitHubWebhook webhook) =>
        new(webhook.DeliveryId, webhook.EventName, "duplicate", null);
}
