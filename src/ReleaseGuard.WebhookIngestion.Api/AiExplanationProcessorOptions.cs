using Microsoft.Extensions.Options;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed class AiExplanationProcessorOptions
{
    public const string SectionName = "AiExplanationProcessor";
    public const int MaximumBatchSize = 100;
    public const int MinimumPollIntervalMilliseconds = 100;
    public const int MaximumPollIntervalMilliseconds = 60_000;
    public const int MinimumLeaseDurationMilliseconds = 1_000;
    public const int MaximumLeaseDurationMilliseconds = 300_000;
    public const int MinimumStateUpdateTimeoutMilliseconds = 100;
    public const int MaximumStateUpdateTimeoutMilliseconds = 30_000;
    public const int MaximumRetryDelayMillisecondsLimit = 3_600_000;
    public const int MaximumAllowedAttempts = 100;

    public bool Enabled { get; init; }

    public int BatchSize { get; init; } = 10;

    public int PollIntervalMilliseconds { get; init; } = 1_000;

    public int LeaseDurationMilliseconds { get; init; } = 30_000;

    public int InitialRetryDelayMilliseconds { get; init; } = 1_000;

    public int MaximumRetryDelayMilliseconds { get; init; } = 60_000;

    public int MaximumAttempts { get; init; } = 5;

    public int StateUpdateTimeoutMilliseconds { get; init; } = 5_000;

    public static bool IsValid(AiExplanationProcessorOptions options)
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
               options.MaximumAttempts is >= 1 and <= MaximumAllowedAttempts &&
               options.StateUpdateTimeoutMilliseconds < options.LeaseDurationMilliseconds;
    }

    public static TimeSpan CalculateRetryDelay(
        AiExplanationProcessorOptions options,
        int attemptCount)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!IsValid(options))
        {
            throw new ArgumentException(
                "AI explanation processor options are invalid.",
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

public sealed class AiExplanationProcessorOptionsValidator :
    IValidateOptions<AiExplanationProcessorOptions>
{
    private readonly IOptions<AiExplanationClientOptions> _clientOptions;

    public AiExplanationProcessorOptionsValidator(
        IOptions<AiExplanationClientOptions> clientOptions)
    {
        _clientOptions = clientOptions;
    }

    public ValidateOptionsResult Validate(
        string? name,
        AiExplanationProcessorOptions options)
    {
        if (!AiExplanationProcessorOptions.IsValid(options))
        {
            return ValidateOptionsResult.Fail(
                $"{AiExplanationProcessorOptions.SectionName} settings must define a bounded batch, poll interval, lease, state-update timeout, maximum attempt count and exponential retry range; state-update timeout must be shorter than the lease.");
        }

        var minimumLeaseMilliseconds =
            (long)_clientOptions.Value.RequestTimeoutMilliseconds +
            options.StateUpdateTimeoutMilliseconds;

        if (options.Enabled &&
            options.LeaseDurationMilliseconds <= minimumLeaseMilliseconds)
        {
            return ValidateOptionsResult.Fail(
                $"{AiExplanationProcessorOptions.SectionName}:LeaseDurationMilliseconds must exceed AiExplanationClient:RequestTimeoutMilliseconds plus StateUpdateTimeoutMilliseconds when processing is enabled.");
        }

        return ValidateOptionsResult.Success;
    }
}
