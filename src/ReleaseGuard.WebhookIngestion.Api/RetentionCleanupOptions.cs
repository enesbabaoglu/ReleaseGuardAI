using Microsoft.Extensions.Options;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed class RetentionCleanupOptions
{
    public const string SectionName = "RetentionCleanup";
    public const int MaximumBatchSize = 1_000;
    public const int MinimumPollIntervalMilliseconds = 1_000;
    public const int MaximumPollIntervalMilliseconds = 86_400_000;
    public const int MinimumRetentionHours = 1;
    public const int MaximumRetentionHours = 87_600;
    public const int MinimumCleanupTimeoutMilliseconds = 100;
    public const int MaximumCleanupTimeoutMilliseconds = 30_000;

    public bool Enabled { get; init; }

    public int BatchSize { get; init; } = 100;

    public int PollIntervalMilliseconds { get; init; } = 3_600_000;

    public int PublishedOutboxRetentionHours { get; init; } = 168;

    public int AcceptedDeliveryRetentionHours { get; init; } = 720;

    public int IgnoredDeliveryRetentionHours { get; init; } = 168;

    public int CleanupTimeoutMilliseconds { get; init; } = 10_000;

    public static void ThrowIfInvalid(RetentionCleanupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = GetValidationFailures(options);
        if (failures.Count > 0)
        {
            throw new OptionsValidationException(
                SectionName,
                typeof(RetentionCleanupOptions),
                failures);
        }
    }

    internal static IReadOnlyList<string> GetValidationFailures(
        RetentionCleanupOptions options)
    {
        var failures = new List<string>();
        if (options.BatchSize is < 1 or > MaximumBatchSize)
        {
            failures.Add(
                $"{SectionName}:BatchSize must be between 1 and {MaximumBatchSize}.");
        }

        if (options.PollIntervalMilliseconds is
            < MinimumPollIntervalMilliseconds or
            > MaximumPollIntervalMilliseconds)
        {
            failures.Add(
                $"{SectionName}:PollIntervalMilliseconds must be between {MinimumPollIntervalMilliseconds} and {MaximumPollIntervalMilliseconds}.");
        }

        ValidateRetention(
            options.PublishedOutboxRetentionHours,
            nameof(options.PublishedOutboxRetentionHours),
            failures);
        ValidateRetention(
            options.AcceptedDeliveryRetentionHours,
            nameof(options.AcceptedDeliveryRetentionHours),
            failures);
        ValidateRetention(
            options.IgnoredDeliveryRetentionHours,
            nameof(options.IgnoredDeliveryRetentionHours),
            failures);

        if (options.AcceptedDeliveryRetentionHours <
            options.PublishedOutboxRetentionHours)
        {
            failures.Add(
                $"{SectionName}:AcceptedDeliveryRetentionHours must not be shorter than PublishedOutboxRetentionHours.");
        }

        if (options.CleanupTimeoutMilliseconds is
            < MinimumCleanupTimeoutMilliseconds or
            > MaximumCleanupTimeoutMilliseconds)
        {
            failures.Add(
                $"{SectionName}:CleanupTimeoutMilliseconds must be between {MinimumCleanupTimeoutMilliseconds} and {MaximumCleanupTimeoutMilliseconds}.");
        }

        return failures;
    }

    private static void ValidateRetention(
        int hours,
        string name,
        ICollection<string> failures)
    {
        if (hours is < MinimumRetentionHours or > MaximumRetentionHours)
        {
            failures.Add(
                $"{SectionName}:{name} must be between {MinimumRetentionHours} and {MaximumRetentionHours} hours.");
        }
    }
}

public sealed class RetentionCleanupOptionsValidator :
    IValidateOptions<RetentionCleanupOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        RetentionCleanupOptions options)
    {
        var failures = RetentionCleanupOptions.GetValidationFailures(options);
        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
