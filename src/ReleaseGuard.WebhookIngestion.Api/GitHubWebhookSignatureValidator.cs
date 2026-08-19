using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed class GitHubWebhookSignatureValidator : IDisposable
{
    public const string SignatureHeaderName = "X-Hub-Signature-256";

    private const string SignaturePrefix = "sha256=";
    private const int Sha256DigestLength = 32;
    private readonly byte[] _secret;

    public GitHubWebhookSignatureValidator(IOptions<GitHubWebhookOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _secret = Encoding.UTF8.GetBytes(options.Value.Secret);
    }

    public async Task<GitHubWebhookSignatureValidationResult> ValidateAsync(
        Stream requestBody,
        StringValues signatureHeader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestBody);

        if (StringValues.IsNullOrEmpty(signatureHeader))
        {
            return GitHubWebhookSignatureValidationResult.Missing;
        }

        if (signatureHeader.Count != 1)
        {
            return GitHubWebhookSignatureValidationResult.Malformed;
        }

        var signature = signatureHeader[0];
        if (signature is null ||
            signature.Length != SignaturePrefix.Length + (Sha256DigestLength * 2) ||
            !signature.StartsWith(SignaturePrefix, StringComparison.Ordinal))
        {
            return GitHubWebhookSignatureValidationResult.Malformed;
        }

        byte[] providedDigest;
        try
        {
            providedDigest = Convert.FromHexString(signature[SignaturePrefix.Length..]);
        }
        catch (FormatException)
        {
            return GitHubWebhookSignatureValidationResult.Malformed;
        }

        using var hmac = new HMACSHA256(_secret);
        var expectedDigest = await hmac.ComputeHashAsync(requestBody, cancellationToken);

        return CryptographicOperations.FixedTimeEquals(expectedDigest, providedDigest)
            ? GitHubWebhookSignatureValidationResult.Valid
            : GitHubWebhookSignatureValidationResult.Invalid;
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_secret);
    }
}

public enum GitHubWebhookSignatureValidationResult
{
    Valid,
    Missing,
    Malformed,
    Invalid
}
