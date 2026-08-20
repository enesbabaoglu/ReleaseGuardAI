using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed class AiExplanationQueryAuthenticator : IDisposable
{
    public const string HeaderName = "Authorization";
    public const string Scheme = "Bearer";
    public const string Challenge = Scheme;

    private const string HeaderPrefix = Scheme + " ";
    private readonly byte[] _activeCredentialDigest;
    private readonly byte[] _previousCredentialDigest;
    private readonly bool _hasPreviousCredential;

    public AiExplanationQueryAuthenticator(
        IOptions<AiExplanationQueryAuthenticationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        AiExplanationQueryAuthenticationOptions.ThrowIfInvalid(value);

        _activeCredentialDigest = CreateDigest(value.ActiveCredential);
        _hasPreviousCredential = value.PreviousCredential is not null;
        _previousCredentialDigest = _hasPreviousCredential
            ? CreateDigest(value.PreviousCredential!)
            : new byte[SHA256.HashSizeInBytes];
    }

    public bool IsAuthorized(StringValues authorizationHeader)
    {
        var hasSingleBearerCredential = TryGetBearerCredential(
            authorizationHeader,
            out var credential);
        var credentialBytes = Encoding.UTF8.GetBytes(credential);

        try
        {
            Span<byte> providedDigest = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(credentialBytes, providedDigest);

            var matchesActive = CryptographicOperations.FixedTimeEquals(
                _activeCredentialDigest,
                providedDigest);
            var matchesPrevious = CryptographicOperations.FixedTimeEquals(
                _previousCredentialDigest,
                providedDigest);

            return hasSingleBearerCredential &
                   (matchesActive | (_hasPreviousCredential & matchesPrevious));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credentialBytes);
        }
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_activeCredentialDigest);
        CryptographicOperations.ZeroMemory(_previousCredentialDigest);
    }

    private static byte[] CreateDigest(string credential)
    {
        var credentialBytes = Encoding.UTF8.GetBytes(credential);
        try
        {
            return SHA256.HashData(credentialBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credentialBytes);
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

        var headerValue = authorizationHeader[0];
        if (headerValue is null ||
            headerValue.Length <= HeaderPrefix.Length ||
            !headerValue.StartsWith(
                HeaderPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        credential = headerValue[HeaderPrefix.Length..];
        return true;
    }
}
