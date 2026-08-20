using Microsoft.Extensions.Options;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed class AiExplanationQueryAuthenticationOptions
{
    public const string SectionName = "AiExplanationQueryAuthentication";
    public const int MinimumCredentialLength = 32;
    public const int MaximumCredentialLength = 512;

    internal static readonly string ValidationFailure =
        $"{SectionName}:Credential must be a {MinimumCredentialLength}–{MaximumCredentialLength} character bearer-token value supplied by a configuration or secret provider.";

    public string Credential { get; set; } = string.Empty;

    public static bool HasValidCredential(
        AiExplanationQueryAuthenticationOptions options)
    {
        if (options?.Credential is not { } credential ||
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

    public static void ThrowIfInvalid(
        AiExplanationQueryAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!HasValidCredential(options))
        {
            throw new OptionsValidationException(
                SectionName,
                typeof(AiExplanationQueryAuthenticationOptions),
                [ValidationFailure]);
        }
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
        AiExplanationQueryAuthenticationOptions options) =>
        AiExplanationQueryAuthenticationOptions.HasValidCredential(options)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                AiExplanationQueryAuthenticationOptions.ValidationFailure);
}
