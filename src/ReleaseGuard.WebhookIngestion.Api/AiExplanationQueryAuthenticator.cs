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
    private readonly byte[] _expectedCredentialDigest;

    public AiExplanationQueryAuthenticator(
        IOptions<AiExplanationQueryAuthenticationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        AiExplanationQueryAuthenticationOptions.ThrowIfInvalid(value);

        var credentialBytes = Encoding.UTF8.GetBytes(value.Credential);
        try
        {
            _expectedCredentialDigest = SHA256.HashData(credentialBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credentialBytes);
        }
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

            return CryptographicOperations.FixedTimeEquals(
                       _expectedCredentialDigest,
                       providedDigest) &&
                   hasSingleBearerCredential;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credentialBytes);
        }
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_expectedCredentialDigest);
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
