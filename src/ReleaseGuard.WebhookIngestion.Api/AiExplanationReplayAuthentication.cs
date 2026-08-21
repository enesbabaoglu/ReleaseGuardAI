using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed class AiExplanationReplayAuthenticationOptions
{
    public const string SectionName = "AiExplanationReplayAuthentication";

    public string ActiveCredential { get; init; } = string.Empty;

    public string? PreviousCredential { get; init; }

    public static void ThrowIfInvalid(
        AiExplanationReplayAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = GetValidationFailures(options);
        if (failures.Count > 0)
        {
            throw new OptionsValidationException(
                SectionName,
                typeof(AiExplanationReplayAuthenticationOptions),
                failures);
        }
    }

    internal static IReadOnlyList<string> GetValidationFailures(
        AiExplanationReplayAuthenticationOptions options)
    {
        var failures = new List<string>(3);
        if (!IsValidCredential(options.ActiveCredential))
        {
            failures.Add(
                $"{SectionName}:ActiveCredential must be a {AiExplanationQueryAuthenticationOptions.MinimumCredentialLength}–{AiExplanationQueryAuthenticationOptions.MaximumCredentialLength} character bearer-token value.");
        }

        if (options.PreviousCredential is not null &&
            !IsValidCredential(options.PreviousCredential))
        {
            failures.Add(
                $"{SectionName}:PreviousCredential must be absent or a {AiExplanationQueryAuthenticationOptions.MinimumCredentialLength}–{AiExplanationQueryAuthenticationOptions.MaximumCredentialLength} character bearer-token value.");
        }

        if (options.PreviousCredential is not null &&
            string.Equals(
                options.ActiveCredential,
                options.PreviousCredential,
                StringComparison.Ordinal))
        {
            failures.Add(
                $"{SectionName}:ActiveCredential and PreviousCredential must differ.");
        }

        return failures;
    }

    private static bool IsValidCredential(string? credential)
    {
        if (credential is null ||
            credential.Length is
                < AiExplanationQueryAuthenticationOptions.MinimumCredentialLength or
                > AiExplanationQueryAuthenticationOptions.MaximumCredentialLength)
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

            if (paddingStarted ||
                !(character is >= 'A' and <= 'Z' or
                    >= 'a' and <= 'z' or
                    >= '0' and <= '9' or
                    '-' or '.' or '_' or '~' or '+' or '/'))
            {
                return false;
            }

            hasTokenCharacter = true;
        }

        return hasTokenCharacter;
    }
}

public sealed class AiExplanationReplayAuthenticationOptionsValidator :
    IValidateOptions<AiExplanationReplayAuthenticationOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        AiExplanationReplayAuthenticationOptions options)
    {
        var failures = AiExplanationReplayAuthenticationOptions
            .GetValidationFailures(options);
        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

public sealed class AiExplanationReplayAuthenticator : IDisposable
{
    private readonly byte[] _activeCredentialDigest;
    private readonly byte[] _previousCredentialDigest;
    private readonly bool _hasPreviousCredential;

    public AiExplanationReplayAuthenticator(
        IOptions<AiExplanationReplayAuthenticationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        AiExplanationReplayAuthenticationOptions.ThrowIfInvalid(value);
        _activeCredentialDigest = CreateDigest(value.ActiveCredential);
        _hasPreviousCredential = value.PreviousCredential is not null;
        _previousCredentialDigest = _hasPreviousCredential
            ? CreateDigest(value.PreviousCredential!)
            : new byte[SHA256.HashSizeInBytes];
    }

    public bool IsAuthorized(StringValues authorizationHeader)
    {
        var parsed = TryGetBearerCredential(
            authorizationHeader,
            out var credential);
        var bytes = Encoding.UTF8.GetBytes(credential);
        try
        {
            Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(bytes, digest);
            var active = CryptographicOperations.FixedTimeEquals(
                _activeCredentialDigest,
                digest);
            var previous = CryptographicOperations.FixedTimeEquals(
                _previousCredentialDigest,
                digest);
            return parsed & (active | (_hasPreviousCredential & previous));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_activeCredentialDigest);
        CryptographicOperations.ZeroMemory(_previousCredentialDigest);
    }

    private static byte[] CreateDigest(string credential)
    {
        var bytes = Encoding.UTF8.GetBytes(credential);
        try
        {
            return SHA256.HashData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool TryGetBearerCredential(
        StringValues authorizationHeader,
        out string credential)
    {
        credential = string.Empty;
        if (authorizationHeader.Count != 1)
        {
            return false;
        }

        var value = authorizationHeader[0];
        const string prefix = "Bearer ";
        if (value is null ||
            value.Length <= prefix.Length ||
            !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        credential = value[prefix.Length..];
        return true;
    }
}
