using System.Globalization;
using Microsoft.Extensions.Options;

namespace ReleaseGuard.WebhookIngestion.Api;

public static class ReleaseRiskExplanationReplayEndpoint
{
    public const string Route =
        "/v1/release-risk-events/{eventId}/ai-explanation/replays";
    public const string IdempotencyHeaderName = "Idempotency-Key";
    public const string AuthenticationFailedCode =
        "ai_explanation_replay_authentication_failed";
    public const string MalformedRequestCode =
        "ai_explanation_replay_request_malformed";
    public const string NotFoundCode = "ai_explanation_replay_event_not_found";
    public const string NotEligibleCode =
        "ai_explanation_replay_not_eligible";
    public const string ReplayIdConflictCode =
        "ai_explanation_replay_id_conflict";
    public const string RateLimitExceededCode =
        "ai_explanation_replay_rate_limit_exceeded";
    public const string TimeoutCode = "ai_explanation_replay_timeout";

    public static async Task<IResult> HandleAsync(
        HttpRequest request,
        string eventId,
        AiExplanationReplayAuthenticator authenticator,
        AiExplanationReplayRateLimitBoundary rateLimitBoundary,
        IReleaseRiskExplanationReplayStore store,
        IOptions<AiExplanationReplayOptions> options,
        CancellationToken cancellationToken)
    {
        if (!authenticator.IsAuthorized(
                request.Headers[AiExplanationQueryAuthenticator.HeaderName]))
        {
            request.HttpContext.Response.Headers.WWWAuthenticate =
                AiExplanationQueryAuthenticator.Challenge;
            return Problem(
                StatusCodes.Status401Unauthorized,
                AuthenticationFailedCode,
                "Authentication failed.",
                "The replay request could not be authenticated.");
        }

        var decision = rateLimitBoundary.AttemptAcquire();
        if (!decision.IsAcquired)
        {
            request.HttpContext.Response.Headers.RetryAfter =
                decision.RetryAfterSeconds.ToString(
                    CultureInfo.InvariantCulture);
            return Problem(
                StatusCodes.Status429TooManyRequests,
                RateLimitExceededCode,
                "AI explanation replay rate limit exceeded.",
                "The replay request rate limit was exceeded. Retry after the indicated delay.");
        }

        if (!Guid.TryParseExact(eventId, "D", out var parsedEventId) ||
            request.Headers[IdempotencyHeaderName].Count != 1 ||
            !Guid.TryParseExact(
                request.Headers[IdempotencyHeaderName][0],
                "D",
                out var replayId))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                MalformedRequestCode,
                "Malformed AI explanation replay request.",
                "The eventId and single Idempotency-Key must be GUID values in canonical D format.");
        }

        AiExplanationReplayOptions.ThrowIfInvalid(options.Value);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(
            TimeSpan.FromMilliseconds(options.Value.RequestTimeoutMilliseconds));

        ReleaseRiskExplanationReplayReceipt receipt;
        try
        {
            receipt = await store.RequestReplayAsync(
                parsedEventId,
                replayId,
                timeout.Token);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested &&
                  timeout.IsCancellationRequested)
        {
            return Problem(
                StatusCodes.Status503ServiceUnavailable,
                TimeoutCode,
                "AI explanation replay timed out.",
                "The replay request could not be persisted within the configured database deadline.");
        }

        return receipt.Disposition switch
        {
            ReleaseRiskExplanationReplayDisposition.Accepted or
                ReleaseRiskExplanationReplayDisposition.Duplicate =>
                Results.Json(
                    new ReleaseRiskExplanationReplayResponse(
                        receipt.ReplayId,
                        receipt.EventId,
                        receipt.Generation,
                        receipt.RequestedAt,
                        "pending"),
                    statusCode: StatusCodes.Status202Accepted),
            ReleaseRiskExplanationReplayDisposition.EventNotFound => Problem(
                StatusCodes.Status404NotFound,
                NotFoundCode,
                "AI explanation replay event was not found.",
                "No durable inbox event exists for the supplied eventId."),
            ReleaseRiskExplanationReplayDisposition.NotEligible => Problem(
                StatusCodes.Status409Conflict,
                NotEligibleCode,
                "AI explanation replay is not eligible.",
                "Only the latest terminally failed AI explanation generation can be replayed."),
            ReleaseRiskExplanationReplayDisposition.ReplayIdConflict => Problem(
                StatusCodes.Status409Conflict,
                ReplayIdConflictCode,
                "AI explanation replay ID conflicts.",
                "The supplied Idempotency-Key is already bound to another replay request."),
            _ => throw new InvalidOperationException(
                "Replay store returned an unknown disposition.")
        };
    }

    private static IResult Problem(
        int statusCode,
        string code,
        string title,
        string detail) =>
        Results.Problem(
            statusCode: statusCode,
            title: title,
            detail: detail,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code
            });
}

public sealed record ReleaseRiskExplanationReplayResponse(
    Guid ReplayId,
    Guid EventId,
    int Generation,
    DateTimeOffset RequestedAt,
    string Status);
