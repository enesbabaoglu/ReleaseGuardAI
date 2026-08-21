using Microsoft.Extensions.Options;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed class AiExplanationReplayOptions
{
    public const string SectionName = "AiExplanationReplay";
    public const int MinimumRequestTimeoutMilliseconds = 100;
    public const int MaximumRequestTimeoutMilliseconds = 30_000;
    public const int MinimumPermitLimit = 1;
    public const int MaximumPermitLimit = 1_000;
    public const int MinimumWindowMilliseconds = 100;
    public const int MaximumWindowMilliseconds = 3_600_000;

    public int RequestTimeoutMilliseconds { get; init; } = 5_000;

    public int PermitLimit { get; init; } = 10;

    public int WindowMilliseconds { get; init; } = 60_000;

    public static void ThrowIfInvalid(AiExplanationReplayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = GetValidationFailures(options);
        if (failures.Count > 0)
        {
            throw new OptionsValidationException(
                SectionName,
                typeof(AiExplanationReplayOptions),
                failures);
        }
    }

    internal static IReadOnlyList<string> GetValidationFailures(
        AiExplanationReplayOptions options)
    {
        var failures = new List<string>(3);
        if (options.RequestTimeoutMilliseconds is
            < MinimumRequestTimeoutMilliseconds or
            > MaximumRequestTimeoutMilliseconds)
        {
            failures.Add(
                $"{SectionName}:RequestTimeoutMilliseconds must be between {MinimumRequestTimeoutMilliseconds} and {MaximumRequestTimeoutMilliseconds}.");
        }

        if (options.PermitLimit is < MinimumPermitLimit or > MaximumPermitLimit)
        {
            failures.Add(
                $"{SectionName}:PermitLimit must be between {MinimumPermitLimit} and {MaximumPermitLimit}.");
        }

        if (options.WindowMilliseconds is
            < MinimumWindowMilliseconds or
            > MaximumWindowMilliseconds)
        {
            failures.Add(
                $"{SectionName}:WindowMilliseconds must be between {MinimumWindowMilliseconds} and {MaximumWindowMilliseconds}.");
        }

        return failures;
    }
}

public sealed class AiExplanationReplayOptionsValidator :
    IValidateOptions<AiExplanationReplayOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        AiExplanationReplayOptions options)
    {
        var failures = AiExplanationReplayOptions.GetValidationFailures(options);
        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
