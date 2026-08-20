using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace ReleaseGuard.WebhookIngestion.Api;

public static class ReleaseRiskExplanationQueryEndpoint
{
    public const string Route =
        "/v1/release-risk-events/{eventId}/ai-explanation";
    public const string MalformedEventIdCode = "malformed_event_id";
    public const string NotFoundCode = "ai_explanation_not_found";
    public const string QueryTimeoutCode = "ai_explanation_query_timeout";
    public const string AuthenticationFailedCode =
        "ai_explanation_authentication_failed";
    public const string RateLimitExceededCode =
        "ai_explanation_rate_limit_exceeded";

    public static async Task<IResult> HandleAsync(
        HttpRequest request,
        string eventId,
        AiExplanationQueryAuthenticator authenticator,
        AiExplanationQueryRateLimitBoundary rateLimitBoundary,
        IReleaseRiskExplanationQuery query,
        IOptions<AiExplanationQueryOptions> options,
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
                "The request could not be authenticated.");
        }

        var rateLimitDecision = rateLimitBoundary.AttemptAcquire();
        if (!rateLimitDecision.IsAcquired)
        {
            request.HttpContext.Response.Headers.RetryAfter =
                rateLimitDecision.RetryAfterSeconds
                    .ToString(CultureInfo.InvariantCulture);
            return Problem(
                StatusCodes.Status429TooManyRequests,
                RateLimitExceededCode,
                "AI explanation request rate limit exceeded.",
                "The request rate limit was exceeded. Retry after the indicated delay.");
        }

        if (!Guid.TryParseExact(eventId, "D", out var parsedEventId))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                MalformedEventIdCode,
                "Malformed event ID.",
                "The eventId route value must be a GUID in canonical D format.");
        }

        AiExplanationQueryOptions.ThrowIfInvalid(options.Value);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(
            TimeSpan.FromMilliseconds(options.Value.ReadTimeoutMilliseconds));

        ReleaseRiskExplanationQuerySnapshot? snapshot;
        try
        {
            snapshot = await query.ReadAsync(
                parsedEventId,
                timeoutSource.Token);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested &&
                  timeoutSource.IsCancellationRequested)
        {
            return Problem(
                StatusCodes.Status503ServiceUnavailable,
                QueryTimeoutCode,
                "AI explanation query timed out.",
                "The AI explanation state could not be read within the configured database deadline.");
        }

        if (snapshot is null)
        {
            return Problem(
                StatusCodes.Status404NotFound,
                NotFoundCode,
                "AI explanation event was not found.",
                "No durable inbox event exists for the supplied eventId.");
        }

        return Results.Ok(ReleaseRiskExplanationQueryResponse.From(snapshot));
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

public sealed class ReleaseRiskExplanationQueryResponse
{
    private ReleaseRiskExplanationQueryResponse(
        Guid eventId,
        string status,
        ReleaseRiskExplanation? explanation,
        ReleaseRiskExplanationTerminalFailure? failure)
    {
        EventId = eventId;
        Status = status;
        Explanation = explanation;
        Failure = failure;
    }

    public Guid EventId { get; }

    public string Status { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReleaseRiskExplanation? Explanation { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReleaseRiskExplanationTerminalFailure? Failure { get; }

    public static ReleaseRiskExplanationQueryResponse From(
        ReleaseRiskExplanationQuerySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return snapshot switch
        {
            PendingReleaseRiskExplanationQuerySnapshot pending =>
                new(pending.EventId, pending.Status, null, null),
            CompletedReleaseRiskExplanationQuerySnapshot completed
                when completed.Explanation is not null &&
                     completed.Explanation.IsValidFor(completed.EventId) =>
                new(
                    completed.EventId,
                    completed.Status,
                    completed.Explanation with
                    {
                        Recommendations = Array.AsReadOnly(
                            completed.Explanation.Recommendations.ToArray())
                    },
                    null),
            FailedReleaseRiskExplanationQuerySnapshot failed
                when IsValidFailure(failed.Failure) =>
                new(failed.EventId, failed.Status, null, failed.Failure),
            _ => throw new InvalidOperationException(
                "The AI explanation query snapshot violates its status contract.")
        };
    }

    private static bool IsValidFailure(
        ReleaseRiskExplanationTerminalFailure? failure) =>
        failure is not null &&
        !string.IsNullOrWhiteSpace(failure.Code) &&
        !string.IsNullOrWhiteSpace(failure.Reason);
}
