using Microsoft.Extensions.Options;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed class OutboxDispatcherOptions
{
    public const string SectionName = "OutboxDispatcher";
    public const int MaximumBatchSize = 100;
    public const int MinimumPollIntervalMilliseconds = 100;
    public const int MaximumPollIntervalMilliseconds = 60_000;
    public const int MinimumLeaseDurationMilliseconds = 5_000;
    public const int MaximumLeaseDurationMilliseconds = 300_000;
    public const int MinimumStateUpdateTimeoutMilliseconds = 1_000;
    public const int MaximumStateUpdateTimeoutMilliseconds = 30_000;
    public const int MaximumRetryDelayMillisecondsLimit = 3_600_000;

    public bool Enabled { get; init; }

    public int BatchSize { get; init; } = 10;

    public int PollIntervalMilliseconds { get; init; } = 1_000;

    public int LeaseDurationMilliseconds { get; init; } = 30_000;

    public int InitialRetryDelayMilliseconds { get; init; } = 1_000;

    public int MaximumRetryDelayMilliseconds { get; init; } = 60_000;

    public int StateUpdateTimeoutMilliseconds { get; init; } = 5_000;

    public static bool IsValid(OutboxDispatcherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.BatchSize is >= 1 and <= MaximumBatchSize &&
               options.PollIntervalMilliseconds is
                   >= MinimumPollIntervalMilliseconds and <= MaximumPollIntervalMilliseconds &&
               options.LeaseDurationMilliseconds is
                   >= MinimumLeaseDurationMilliseconds and <= MaximumLeaseDurationMilliseconds &&
               options.StateUpdateTimeoutMilliseconds is
                   >= MinimumStateUpdateTimeoutMilliseconds and <= MaximumStateUpdateTimeoutMilliseconds &&
               options.InitialRetryDelayMilliseconds is
                   >= MinimumPollIntervalMilliseconds and <= MaximumRetryDelayMillisecondsLimit &&
               options.MaximumRetryDelayMilliseconds is
                   >= MinimumPollIntervalMilliseconds and <= MaximumRetryDelayMillisecondsLimit &&
               options.InitialRetryDelayMilliseconds <= options.MaximumRetryDelayMilliseconds &&
               options.StateUpdateTimeoutMilliseconds < options.LeaseDurationMilliseconds;
    }

    public static TimeSpan CalculateRetryDelay(
        OutboxDispatcherOptions options,
        int attemptCount)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!IsValid(options))
        {
            throw new ArgumentException(
                "Outbox dispatcher options are invalid.",
                nameof(options));
        }

        if (attemptCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(attemptCount),
                "Attempt count must be at least one.");
        }

        var exponent = Math.Min(attemptCount - 1, 30);
        var multiplier = 1L << exponent;
        var delayMilliseconds = Math.Min(
            options.MaximumRetryDelayMilliseconds,
            options.InitialRetryDelayMilliseconds * multiplier);

        return TimeSpan.FromMilliseconds(delayMilliseconds);
    }
}

public sealed class OutboxDispatcherOptionsValidator :
    IValidateOptions<OutboxDispatcherOptions>
{
    private readonly IOptions<KafkaProducerOptions> _kafkaOptions;

    public OutboxDispatcherOptionsValidator(
        IOptions<KafkaProducerOptions> kafkaOptions)
    {
        _kafkaOptions = kafkaOptions;
    }

    public ValidateOptionsResult Validate(
        string? name,
        OutboxDispatcherOptions options)
    {
        if (!OutboxDispatcherOptions.IsValid(options))
        {
            return ValidateOptionsResult.Fail(
                $"{OutboxDispatcherOptions.SectionName} settings must define a bounded batch, poll interval, lease, state-update timeout and exponential retry range; state-update timeout must be shorter than the lease.");
        }

        var minimumLeaseMilliseconds =
            (long)_kafkaOptions.Value.DeliveryTimeoutMilliseconds +
            options.StateUpdateTimeoutMilliseconds;

        if (options.Enabled &&
            options.LeaseDurationMilliseconds <= minimumLeaseMilliseconds)
        {
            return ValidateOptionsResult.Fail(
                $"{OutboxDispatcherOptions.SectionName}:LeaseDurationMilliseconds must exceed Kafka:DeliveryTimeoutMilliseconds plus StateUpdateTimeoutMilliseconds when dispatch is enabled.");
        }

        return ValidateOptionsResult.Success;
    }
}
