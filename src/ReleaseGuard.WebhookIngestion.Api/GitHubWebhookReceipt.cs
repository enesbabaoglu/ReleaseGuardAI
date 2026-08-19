namespace ReleaseGuard.WebhookIngestion.Api;

public sealed record GitHubWebhookReceipt(
    Guid DeliveryId,
    string EventName,
    string Status,
    ReleaseRiskInput? RiskInput,
    ReleaseRiskAssessment? RiskAssessment)
{
    public static GitHubWebhookReceipt Accepted(
        VerifiedGitHubWebhook webhook,
        ReleaseRiskInput riskInput,
        ReleaseRiskAssessment riskAssessment) =>
        new(
            webhook.DeliveryId,
            webhook.EventName,
            "accepted",
            riskInput,
            riskAssessment);

    public static GitHubWebhookReceipt Ignored(VerifiedGitHubWebhook webhook) =>
        new(webhook.DeliveryId, webhook.EventName, "ignored", null, null);

    public static GitHubWebhookReceipt Duplicate(VerifiedGitHubWebhook webhook) =>
        new(webhook.DeliveryId, webhook.EventName, "duplicate", null, null);
}
