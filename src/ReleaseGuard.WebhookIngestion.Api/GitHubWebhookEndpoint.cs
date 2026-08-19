namespace ReleaseGuard.WebhookIngestion.Api;

public static class GitHubWebhookEndpoint
{
    public const string Route = "/webhooks/github";

    public static async Task<IResult> HandleAsync(
        HttpRequest request,
        GitHubWebhookSignatureValidator signatureValidator,
        CancellationToken cancellationToken)
    {
        if (!request.Headers.TryGetValue(
                GitHubWebhookSignatureValidator.SignatureHeaderName,
                out var signatureHeader))
        {
            return Results.Unauthorized();
        }

        var validationResult = await signatureValidator.ValidateAsync(
            request.Body,
            signatureHeader,
            cancellationToken);

        return validationResult switch
        {
            GitHubWebhookSignatureValidationResult.Valid => Results.Accepted(),
            GitHubWebhookSignatureValidationResult.Malformed => Results.BadRequest(),
            _ => Results.Unauthorized()
        };
    }
}
