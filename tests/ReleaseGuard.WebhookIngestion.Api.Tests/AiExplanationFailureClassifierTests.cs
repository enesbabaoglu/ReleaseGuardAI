using System.Net;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class AiExplanationFailureClassifierTests
{
    public static TheoryData<Exception, string> RetryableFailures => new()
    {
        { new TimeoutException(), AiExplanationFailureClassifier.RequestTimeoutCode },
        { new HttpRequestException(), AiExplanationFailureClassifier.TransportErrorCode },
        {
            new HttpRequestException(
                "timeout",
                inner: null,
                HttpStatusCode.RequestTimeout),
            AiExplanationFailureClassifier.RemoteTimeoutCode
        },
        {
            new HttpRequestException(
                "throttled",
                inner: null,
                HttpStatusCode.TooManyRequests),
            AiExplanationFailureClassifier.RemoteThrottledCode
        },
        {
            new HttpRequestException(
                "unavailable",
                inner: null,
                HttpStatusCode.ServiceUnavailable),
            AiExplanationFailureClassifier.RemoteServerErrorCode
        },
        { new InvalidOperationException(), AiExplanationFailureClassifier.UnexpectedErrorCode }
    };

    public static TheoryData<Exception, string> TerminalFailures => new()
    {
        {
            new ReleaseRiskExplanationEventIdConflictException(
                Guid.NewGuid(),
                Guid.NewGuid()),
            AiExplanationFailureClassifier.EventIdConflictCode
        },
        {
            new ReleaseRiskExplanationContractException("invalid"),
            AiExplanationFailureClassifier.ResponseContractInvalidCode
        },
        {
            new HttpRequestException(
                "bad request",
                inner: null,
                HttpStatusCode.BadRequest),
            AiExplanationFailureClassifier.RemoteClientErrorCode
        },
        {
            new ArgumentException("invalid request"),
            AiExplanationFailureClassifier.RequestContractInvalidCode
        }
    };

    [Theory]
    [MemberData(nameof(RetryableFailures))]
    public void Classify_RetryableFailures_ReturnsStableCode(
        Exception exception,
        string expectedCode)
    {
        var classification = AiExplanationFailureClassifier.Classify(exception);

        Assert.Equal(AiExplanationFailureDisposition.Retryable, classification.Disposition);
        Assert.Equal(expectedCode, classification.Code);
        Assert.False(string.IsNullOrWhiteSpace(classification.Reason));
    }

    [Theory]
    [MemberData(nameof(TerminalFailures))]
    public void Classify_TerminalFailures_ReturnsStableCode(
        Exception exception,
        string expectedCode)
    {
        var classification = AiExplanationFailureClassifier.Classify(exception);

        Assert.Equal(AiExplanationFailureDisposition.Terminal, classification.Disposition);
        Assert.Equal(expectedCode, classification.Code);
        Assert.False(string.IsNullOrWhiteSpace(classification.Reason));
    }

    [Fact]
    public void ToTerminalFailure_RejectsRetryableFailureBeforeAttemptLimit()
    {
        var classification = AiExplanationFailureClassifier.Classify(
            new TimeoutException());

        Assert.Throws<InvalidOperationException>(() =>
            AiExplanationFailureClassifier.ToTerminalFailure(
                classification,
                attemptCount: 4,
                maximumAttempts: 5));
    }
}
