using System.Net;

namespace ReleaseGuard.WebhookIngestion.Api;

public enum AiExplanationFailureDisposition
{
    Retryable,
    Terminal
}

public sealed record AiExplanationFailureClassification(
    AiExplanationFailureDisposition Disposition,
    string Code,
    string Reason);

public static class AiExplanationFailureClassifier
{
    public const string RequestTimeoutCode = "request_timeout";
    public const string TransportErrorCode = "transport_error";
    public const string RemoteTimeoutCode = "remote_timeout";
    public const string RemoteThrottledCode = "remote_throttled";
    public const string RemoteServerErrorCode = "remote_server_error";
    public const string UnexpectedErrorCode = "unexpected_error";
    public const string EventIdConflictCode = "event_id_conflict";
    public const string ResponseContractInvalidCode = "response_contract_invalid";
    public const string RemoteClientErrorCode = "remote_client_error";
    public const string RequestContractInvalidCode = "request_contract_invalid";
    public const string AttemptLimitExhaustedCode = "attempt_limit_exhausted";

    public static AiExplanationFailureClassification Classify(
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            ReleaseRiskExplanationEventIdConflictException => Terminal(
                EventIdConflictCode,
                "AI explanation response event ID did not match the claimed event."),
            ReleaseRiskExplanationContractException => Terminal(
                ResponseContractInvalidCode,
                "AI explanation response violated the required response contract."),
            ArgumentException => Terminal(
                RequestContractInvalidCode,
                "The claimed event did not satisfy the AI explanation request contract."),
            TimeoutException => Retryable(
                RequestTimeoutCode,
                "AI explanation request timed out."),
            HttpRequestException { StatusCode: HttpStatusCode.RequestTimeout } =>
                Retryable(
                    RemoteTimeoutCode,
                    "AI explanation service reported a timeout."),
            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } =>
                Retryable(
                    RemoteThrottledCode,
                    "AI explanation service throttled the request."),
            HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError } =>
                Retryable(
                    RemoteServerErrorCode,
                    "AI explanation service returned a server error."),
            HttpRequestException { StatusCode: null } => Retryable(
                TransportErrorCode,
                "AI explanation service could not be reached."),
            HttpRequestException => Terminal(
                RemoteClientErrorCode,
                "AI explanation service rejected the valid V1 request with a non-retryable status."),
            _ => Retryable(
                UnexpectedErrorCode,
                "AI explanation processing failed unexpectedly.")
        };
    }

    public static ReleaseRiskExplanationTerminalFailure ToTerminalFailure(
        AiExplanationFailureClassification classification,
        int attemptCount,
        int maximumAttempts)
    {
        ArgumentNullException.ThrowIfNull(classification);

        if (attemptCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptCount));
        }

        if (maximumAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        if (classification.Disposition ==
                AiExplanationFailureDisposition.Retryable &&
            attemptCount < maximumAttempts)
        {
            throw new InvalidOperationException(
                "A retryable failure cannot become terminal before the configured attempt limit.");
        }

        var reason = classification.Disposition ==
                     AiExplanationFailureDisposition.Retryable
            ? $"{classification.Reason} Attempt {attemptCount} reached the configured maximum of {maximumAttempts} attempts."
            : classification.Reason;

        return new ReleaseRiskExplanationTerminalFailure(
            classification.Code,
            reason);
    }

    private static AiExplanationFailureClassification Retryable(
        string code,
        string reason) =>
        new(AiExplanationFailureDisposition.Retryable, code, reason);

    private static AiExplanationFailureClassification Terminal(
        string code,
        string reason) =>
        new(AiExplanationFailureDisposition.Terminal, code, reason);
}
