namespace ReleaseGuard.WebhookIngestion.Api;

public sealed class ReleaseRiskInboxProcessorOptions
{
    public const string SectionName = "InboxProcessor";
    public const int MinimumPersistenceTimeoutMilliseconds = 1_000;
    public const int MaximumPersistenceTimeoutMilliseconds = 30_000;

    public bool Enabled { get; init; }

    public int PersistenceTimeoutMilliseconds { get; init; } = 5_000;

    public static bool IsValid(ReleaseRiskInboxProcessorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.PersistenceTimeoutMilliseconds is
            >= MinimumPersistenceTimeoutMilliseconds and
            <= MaximumPersistenceTimeoutMilliseconds;
    }
}
