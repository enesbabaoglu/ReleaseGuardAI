using Microsoft.Extensions.Options;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed class AiExplanationQueryRateLimitOptions
{
    public const string SectionName = "AiExplanationQueryRateLimit";
    public const int MinimumPermitLimit = 1;
    public const int MaximumPermitLimit = 10_000;
    public const int MinimumWindowMilliseconds = 100;
    public const int MaximumWindowMilliseconds = 3_600_000;

    internal static readonly string PermitLimitValidationFailure =
        $"{SectionName}:PermitLimit must be between {MinimumPermitLimit} and {MaximumPermitLimit}.";
    internal static readonly string WindowValidationFailure =
        $"{SectionName}:WindowMilliseconds must be between {MinimumWindowMilliseconds} and {MaximumWindowMilliseconds}.";

    public int PermitLimit { get; init; } = 60;

    public int WindowMilliseconds { get; init; } = 60_000;

    public static void ThrowIfInvalid(
        AiExplanationQueryRateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = GetValidationFailures(options);
        if (failures.Count > 0)
        {
            throw new OptionsValidationException(
                SectionName,
                typeof(AiExplanationQueryRateLimitOptions),
                failures);
        }
    }

    internal static IReadOnlyList<string> GetValidationFailures(
        AiExplanationQueryRateLimitOptions options)
    {
        var failures = new List<string>(2);

        if (options.PermitLimit is < MinimumPermitLimit or > MaximumPermitLimit)
        {
            failures.Add(PermitLimitValidationFailure);
        }

        if (options.WindowMilliseconds is
            < MinimumWindowMilliseconds or > MaximumWindowMilliseconds)
        {
            failures.Add(WindowValidationFailure);
        }

        return failures;
    }
}

public sealed class AiExplanationQueryRateLimitOptionsValidator :
    IValidateOptions<AiExplanationQueryRateLimitOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        AiExplanationQueryRateLimitOptions options)
    {
        var failures = AiExplanationQueryRateLimitOptions
            .GetValidationFailures(options);
        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
