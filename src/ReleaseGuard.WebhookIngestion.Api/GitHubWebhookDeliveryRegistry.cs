using System.Collections.Concurrent;

namespace ReleaseGuard.WebhookIngestion.Api;

public interface IGitHubWebhookDeliveryRegistry
{
    bool TryRegister(VerifiedGitHubWebhook webhook);
}

public sealed class InMemoryGitHubWebhookDeliveryRegistry : IGitHubWebhookDeliveryRegistry
{
    private readonly ConcurrentDictionary<Guid, byte> _deliveryIds = new();

    public bool TryRegister(VerifiedGitHubWebhook webhook)
    {
        ArgumentNullException.ThrowIfNull(webhook);

        return _deliveryIds.TryAdd(webhook.DeliveryId, 0);
    }
}
