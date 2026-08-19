namespace ReleaseGuard.WebhookIngestion.Api;

public sealed record ServiceStatus(string Status, string Service)
{
    public static ServiceStatus Ready() => new("ok", "webhook-ingestion");
}

