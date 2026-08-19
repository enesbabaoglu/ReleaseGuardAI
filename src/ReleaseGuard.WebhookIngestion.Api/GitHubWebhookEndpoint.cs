using System.Text.Json;

namespace ReleaseGuard.WebhookIngestion.Api;

public static class GitHubWebhookEndpoint
{
    public const string Route = "/webhooks/github";
    public const string DeliveryHeaderName = "X-GitHub-Delivery";
    public const string EventHeaderName = "X-GitHub-Event";

    public static async Task<IResult> HandleAsync(
        HttpRequest request,
        GitHubWebhookSignatureValidator signatureValidator,
        IGitHubWebhookDeliveryRegistry deliveryRegistry,
        CancellationToken cancellationToken)
    {
        if (!request.Headers.TryGetValue(
                GitHubWebhookSignatureValidator.SignatureHeaderName,
                out var signatureHeader))
        {
            return Results.Unauthorized();
        }

        request.EnableBuffering();

        var validationResult = await signatureValidator.ValidateAsync(
            request.Body,
            signatureHeader,
            cancellationToken);

        var validationFailure = validationResult switch
        {
            GitHubWebhookSignatureValidationResult.Valid => null,
            GitHubWebhookSignatureValidationResult.Malformed => Results.BadRequest(),
            _ => Results.Unauthorized()
        };

        if (validationFailure is not null)
        {
            return validationFailure;
        }

        if (!TryGetSingleHeader(request, DeliveryHeaderName, out var deliveryHeader) ||
            !Guid.TryParse(deliveryHeader, out var deliveryId) ||
            !TryGetSingleHeader(request, EventHeaderName, out var eventName))
        {
            return Results.BadRequest();
        }

        request.Body.Position = 0;

        JsonElement payload;
        try
        {
            using var document = await JsonDocument.ParseAsync(
                request.Body,
                cancellationToken: cancellationToken);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Results.BadRequest();
            }

            payload = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }

        var webhook = new VerifiedGitHubWebhook(deliveryId, eventName, payload);

        return deliveryRegistry.TryRegister(webhook)
            ? Results.Accepted(value: GitHubWebhookReceipt.Accepted(webhook))
            : Results.Ok(GitHubWebhookReceipt.Duplicate(webhook));
    }

    private static bool TryGetSingleHeader(
        HttpRequest request,
        string headerName,
        out string headerValue)
    {
        headerValue = string.Empty;

        if (!request.Headers.TryGetValue(headerName, out var values) || values.Count != 1)
        {
            return false;
        }

        var value = values[0];
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        headerValue = value;
        return true;
    }
}
