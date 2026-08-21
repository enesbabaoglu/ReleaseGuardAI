using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace ReleaseGuard.WebhookIngestion.Api;

public static class ReleaseRiskExplanationListEndpoint
{
    public const string Route = "/v1/release-risk-events/ai-explanations";
    public const string InvalidQueryCode = "ai_explanation_list_query_invalid";

    public static async Task<IResult> HandleAsync(
        HttpRequest request,
        AiExplanationQueryAuthenticator authenticator,
        AiExplanationQueryRateLimitBoundary rateLimitBoundary,
        IAiExplanationQueryMetrics metrics,
        IReleaseRiskExplanationCollectionQuery query,
        IOptions<AiExplanationQueryOptions> options,
        CancellationToken cancellationToken)
    {
        var admissionFailure = AiExplanationCollectionEndpointExecution
            .TryAdmit(
            request,
            authenticator,
            rateLimitBoundary,
            metrics);
        if (admissionFailure is not null)
        {
            return admissionFailure;
        }

        if (!TryParseParameters(
                request.Query,
                out var limit,
                out var cursor))
        {
            return AiExplanationCollectionEndpointExecution.Problem(
                StatusCodes.Status400BadRequest,
                InvalidQueryCode,
                "Invalid AI explanation list query.",
                "The list query must contain only a bounded limit and a valid opaque cursor.");
        }

        return await AiExplanationCollectionEndpointExecution.ExecuteDatabaseAsync(
            metrics,
            options.Value,
            cancellationToken,
            async queryToken =>
            {
                var page = await query.ReadPageAsync(
                    limit,
                    cursor,
                    queryToken);
                return Results.Ok(page);
            });
    }

    private static bool TryParseParameters(
        IQueryCollection query,
        out int limit,
        out ReleaseRiskExplanationListCursor? cursor)
    {
        limit = PostgreSqlReleaseRiskExplanationCollectionQuery.DefaultLimit;
        cursor = null;

        if (query.Keys.Any(key =>
                !string.Equals(key, "limit", StringComparison.Ordinal) &&
                !string.Equals(key, "cursor", StringComparison.Ordinal)))
        {
            return false;
        }

        if (query.TryGetValue("limit", out var limitValues) &&
            (!TryGetSingle(limitValues, out var limitValue) ||
             !int.TryParse(
                 limitValue,
                 NumberStyles.None,
                 CultureInfo.InvariantCulture,
                 out limit) ||
             limit is < 1 or
                 > PostgreSqlReleaseRiskExplanationCollectionQuery.MaximumLimit))
        {
            return false;
        }

        if (query.TryGetValue("cursor", out var cursorValues) &&
            (!TryGetSingle(cursorValues, out var cursorValue) ||
             !ReleaseRiskExplanationListCursorCodec.TryDecode(
                 cursorValue,
                 out cursor)))
        {
            return false;
        }

        return true;
    }

    private static bool TryGetSingle(
        StringValues values,
        out string value)
    {
        value = values.Count == 1 ? values[0] ?? string.Empty : string.Empty;
        return values.Count == 1 && value.Length > 0;
    }
}

public static class LatestAcceptedReleaseRiskExplanationEndpoint
{
    public const string Route =
        "/v1/repositories/{owner}/{repository}/changes/{changeNumber}/ai-explanation/latest-accepted";
    public const string InvalidRouteCode =
        "latest_ai_explanation_route_invalid";
    public const string NotFoundCode =
        "latest_ai_explanation_not_found";

    public static async Task<IResult> HandleAsync(
        HttpRequest request,
        string owner,
        string repository,
        string changeNumber,
        AiExplanationQueryAuthenticator authenticator,
        AiExplanationQueryRateLimitBoundary rateLimitBoundary,
        IAiExplanationQueryMetrics metrics,
        IReleaseRiskExplanationCollectionQuery query,
        IOptions<AiExplanationQueryOptions> options,
        CancellationToken cancellationToken)
    {
        var admissionFailure = AiExplanationCollectionEndpointExecution
            .TryAdmit(
            request,
            authenticator,
            rateLimitBoundary,
            metrics);
        if (admissionFailure is not null)
        {
            return admissionFailure;
        }

        if (!IsValidRepositoryPart(owner) ||
            !IsValidRepositoryPart(repository) ||
            !long.TryParse(
                changeNumber,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedChangeNumber) ||
            parsedChangeNumber < 1)
        {
            return AiExplanationCollectionEndpointExecution.Problem(
                StatusCodes.Status400BadRequest,
                InvalidRouteCode,
                "Invalid latest AI explanation route.",
                "Repository owner, repository name, and positive change number must use their canonical route forms.");
        }

        var fullRepository = string.Concat(owner, "/", repository);
        return await AiExplanationCollectionEndpointExecution.ExecuteDatabaseAsync(
            metrics,
            options.Value,
            cancellationToken,
            async queryToken =>
            {
                var latest = await query.ReadLatestAcceptedAsync(
                    fullRepository,
                    parsedChangeNumber,
                    queryToken);
                if (latest is null)
                {
                    metrics.RecordOutcome(AiExplanationQueryOutcome.NotFound);
                    return AiExplanationCollectionEndpointExecution.Problem(
                        StatusCodes.Status404NotFound,
                        NotFoundCode,
                        "Latest accepted AI explanation was not found.",
                        "No durable inbox event exists for the supplied repository and change number.");
                }

                metrics.RecordOutcome(
                    latest.Snapshot switch
                    {
                        PendingReleaseRiskExplanationQuerySnapshot =>
                            AiExplanationQueryOutcome.Pending,
                        CompletedReleaseRiskExplanationQuerySnapshot =>
                            AiExplanationQueryOutcome.Completed,
                        FailedReleaseRiskExplanationQuerySnapshot =>
                            AiExplanationQueryOutcome.Failed,
                        _ => throw new InvalidOperationException(
                            "The latest accepted AI explanation has no observable outcome.")
                    });
                return Results.Ok(
                    LatestAcceptedReleaseRiskExplanationResponse.From(latest));
            });
    }

    private static bool IsValidRepositoryPart(string value) =>
        value.Length is >= 1 and <= 100 &&
        value is not "." and not ".." &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_' or '.');
}

public sealed record LatestAcceptedReleaseRiskExplanationResponse(
    string Selection,
    DateTimeOffset AcceptedAt,
    string Repository,
    long ChangeNumber,
    string Kind,
    Guid EventId,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ReleaseRiskExplanation? Explanation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ReleaseRiskExplanationTerminalFailure? Failure)
{
    public static LatestAcceptedReleaseRiskExplanationResponse From(
        LatestAcceptedReleaseRiskExplanation latest)
    {
        ArgumentNullException.ThrowIfNull(latest);
        var state = ReleaseRiskExplanationQueryResponse.From(latest.Snapshot);
        return new LatestAcceptedReleaseRiskExplanationResponse(
            "latestAccepted",
            latest.AcceptedAt,
            latest.Repository,
            latest.ChangeNumber,
            latest.Kind,
            state.EventId,
            state.Status,
            state.Explanation,
            state.Failure);
    }
}

internal static class AiExplanationCollectionEndpointExecution
{
    public static IResult? TryAdmit(
        HttpRequest request,
        AiExplanationQueryAuthenticator authenticator,
        AiExplanationQueryRateLimitBoundary rateLimitBoundary,
        IAiExplanationQueryMetrics metrics)
    {
        if (!authenticator.IsAuthorized(
                request.Headers[AiExplanationQueryAuthenticator.HeaderName]))
        {
            metrics.RecordAuthenticationFailure();
            request.HttpContext.Response.Headers.WWWAuthenticate =
                AiExplanationQueryAuthenticator.Challenge;
            return Problem(
                StatusCodes.Status401Unauthorized,
                ReleaseRiskExplanationQueryEndpoint.AuthenticationFailedCode,
                "Authentication failed.",
                "The request could not be authenticated.");
        }

        var rateLimitDecision = rateLimitBoundary.AttemptAcquire();
        if (!rateLimitDecision.IsAcquired)
        {
            metrics.RecordRateLimitRejection();
            request.HttpContext.Response.Headers.RetryAfter =
                rateLimitDecision.RetryAfterSeconds.ToString(
                    CultureInfo.InvariantCulture);
            return Problem(
                StatusCodes.Status429TooManyRequests,
                ReleaseRiskExplanationQueryEndpoint.RateLimitExceededCode,
                "AI explanation request rate limit exceeded.",
                "The request rate limit was exceeded. Retry after the indicated delay.");
        }

        metrics.RecordRateLimitPermit();
        return null;
    }

    public static async Task<IResult> ExecuteDatabaseAsync(
        IAiExplanationQueryMetrics metrics,
        AiExplanationQueryOptions options,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<IResult>> executeQuery)
    {
        AiExplanationQueryOptions.ThrowIfInvalid(options);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(
            TimeSpan.FromMilliseconds(options.ReadTimeoutMilliseconds));
        var databaseReadStarted = Stopwatch.GetTimestamp();
        try
        {
            return await executeQuery(timeoutSource.Token);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested &&
                  timeoutSource.IsCancellationRequested)
        {
            metrics.RecordOutcome(AiExplanationQueryOutcome.Timeout);
            return Problem(
                StatusCodes.Status503ServiceUnavailable,
                ReleaseRiskExplanationQueryEndpoint.QueryTimeoutCode,
                "AI explanation query timed out.",
                "The AI explanation state could not be read within the configured database deadline.");
        }
        finally
        {
            metrics.RecordDatabaseReadDuration(
                Stopwatch.GetElapsedTime(databaseReadStarted));
        }
    }

    public static IResult Problem(
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
