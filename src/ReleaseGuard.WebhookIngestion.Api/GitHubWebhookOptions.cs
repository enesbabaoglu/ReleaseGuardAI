namespace ReleaseGuard.WebhookIngestion.Api;

public sealed class GitHubWebhookOptions
{
    public const string SectionName = "GitHubWebhook";
    public const int MinimumSecretLength = 32;

    public string Secret { get; set; } = string.Empty;
}
