namespace ReleaseGuard.WebhookIngestion.Api;

public sealed class AiExplanationQueryOptions
{
    public const string SectionName = "AiExplanationQuery";
    public const int MinimumReadTimeoutMilliseconds = 100;
    public const int MaximumReadTimeoutMilliseconds = 30_000;

    public int ReadTimeoutMilliseconds { get; init; } = 5_000;

    public static bool IsValid(AiExplanationQueryOptions options) =>
        options is not null &&
        options.ReadTimeoutMilliseconds is
            >= MinimumReadTimeoutMilliseconds and <= MaximumReadTimeoutMilliseconds;

    public static void ThrowIfInvalid(AiExplanationQueryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!IsValid(options))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"Read timeout must be between {MinimumReadTimeoutMilliseconds} and {MaximumReadTimeoutMilliseconds} milliseconds.");
        }
    }
}
