using Microsoft.Extensions.Options;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed class AiExplanationQueryAuthenticationOptions
{
    public const string SectionName = "AiExplanationQueryAuthentication";
    public const int MinimumCredentialLength = 32;
    public const int MaximumCredentialLength = 512;

    internal static readonly string ActiveCredentialValidationFailure =
        $"{SectionName}:ActiveCredential must be a {MinimumCredentialLength}–{MaximumCredentialLength} character bearer-token value supplied by a configuration or secret provider.";
    internal static readonly string PreviousCredentialValidationFailure =
        $"{SectionName}:PreviousCredential must be absent or a {MinimumCredentialLength}–{MaximumCredentialLength} character bearer-token value supplied by a configuration or secret provider.";
    internal static readonly string CredentialsMustDifferValidationFailure =
        $"{SectionName}:ActiveCredential and PreviousCredential must differ when a rotation credential is configured.";

    public string ActiveCredential { get; set; } = string.Empty;

    public string? PreviousCredential { get; set; }

    public static bool HasValidActiveCredential(
        AiExplanationQueryAuthenticationOptions options) =>
        options is not null && IsValidCredential(options.ActiveCredential);

    public static bool HasValidPreviousCredential(
        AiExplanationQueryAuthenticationOptions options) =>
        options is not null &&
        (options.PreviousCredential is null ||
         IsValidCredential(options.PreviousCredential));

    public static bool HaveDistinctCredentials(
        AiExplanationQueryAuthenticationOptions options) =>
        options is not null &&
        (options.PreviousCredential is null ||
         !string.Equals(
             options.ActiveCredential,
             options.PreviousCredential,
             StringComparison.Ordinal));

    public static void ThrowIfInvalid(
        AiExplanationQueryAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = GetValidationFailures(options);
        if (failures.Count > 0)
        {
            throw new OptionsValidationException(
                SectionName,
                typeof(AiExplanationQueryAuthenticationOptions),
                failures);
        }
    }

    internal static IReadOnlyList<string> GetValidationFailures(
        AiExplanationQueryAuthenticationOptions options)
    {
        var failures = new List<string>(3);

        if (!HasValidActiveCredential(options))
        {
            failures.Add(ActiveCredentialValidationFailure);
        }

        if (!HasValidPreviousCredential(options))
        {
            failures.Add(PreviousCredentialValidationFailure);
        }

        if (!HaveDistinctCredentials(options))
        {
            failures.Add(CredentialsMustDifferValidationFailure);
        }

        return failures;
    }

    private static bool IsValidCredential(string? credential)
    {
        if (credential is null ||
            credential.Length is < MinimumCredentialLength or
                > MaximumCredentialLength)
        {
            return false;
        }

        var paddingStarted = false;
        var hasTokenCharacter = false;
        foreach (var character in credential)
        {
            if (character == '=')
            {
                paddingStarted = true;
                continue;
            }

            if (paddingStarted || !IsBearerTokenCharacter(character))
            {
                return false;
            }

            hasTokenCharacter = true;
        }

        return hasTokenCharacter;
    }

    private static bool IsBearerTokenCharacter(char character) =>
        character is >= 'A' and <= 'Z' or
            >= 'a' and <= 'z' or
            >= '0' and <= '9' or
            '-' or '.' or '_' or '~' or '+' or '/';
}

public sealed class AiExplanationQueryAuthenticationOptionsValidator :
    IValidateOptions<AiExplanationQueryAuthenticationOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        AiExplanationQueryAuthenticationOptions options)
    {
        var failures = AiExplanationQueryAuthenticationOptions
            .GetValidationFailures(options);
        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
