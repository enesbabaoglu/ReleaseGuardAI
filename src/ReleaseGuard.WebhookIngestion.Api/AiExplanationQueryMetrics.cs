using System.Diagnostics.Metrics;

namespace ReleaseGuard.WebhookIngestion.Api;

public enum AiExplanationQueryOutcome
{
    Pending,
    Completed,
    Failed,
    NotFound,
    Timeout
}

public interface IAiExplanationQueryMetrics
{
    void RecordAuthenticationFailure();

    void RecordRateLimitPermit();

    void RecordRateLimitRejection();

    void RecordOutcome(AiExplanationQueryOutcome outcome);

    void RecordDatabaseReadDuration(TimeSpan duration);
}

public sealed class AiExplanationQueryMetrics : IAiExplanationQueryMetrics
{
    public const string MeterName = "ReleaseGuard.WebhookIngestion.Api";
    public const string AuthenticationFailuresInstrumentName =
        "releaseguard.ai_explanation_query.authentication_failures";
    public const string RateLimitPermitsInstrumentName =
        "releaseguard.ai_explanation_query.rate_limit_permits";
    public const string RateLimitRejectionsInstrumentName =
        "releaseguard.ai_explanation_query.rate_limit_rejections";
    public const string OutcomesInstrumentName =
        "releaseguard.ai_explanation_query.outcomes";
    public const string DatabaseReadDurationInstrumentName =
        "releaseguard.ai_explanation_query.database_read_duration";
    public const string OutcomeTagName = "outcome";

    private static readonly KeyValuePair<string, object?> PendingOutcomeTag =
        new(OutcomeTagName, "pending");
    private static readonly KeyValuePair<string, object?> CompletedOutcomeTag =
        new(OutcomeTagName, "completed");
    private static readonly KeyValuePair<string, object?> FailedOutcomeTag =
        new(OutcomeTagName, "failed");
    private static readonly KeyValuePair<string, object?> NotFoundOutcomeTag =
        new(OutcomeTagName, "not_found");
    private static readonly KeyValuePair<string, object?> TimeoutOutcomeTag =
        new(OutcomeTagName, "timeout");

    private readonly Meter _meter;
    private readonly Counter<long> _authenticationFailures;
    private readonly Counter<long> _rateLimitPermits;
    private readonly Counter<long> _rateLimitRejections;
    private readonly Counter<long> _outcomes;
    private readonly Histogram<double> _databaseReadDuration;

    public AiExplanationQueryMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _meter = meterFactory.Create(MeterName);
        _authenticationFailures = _meter.CreateCounter<long>(
            AuthenticationFailuresInstrumentName,
            unit: "{request}",
            description: "AI explanation query authentication failures.");
        _rateLimitPermits = _meter.CreateCounter<long>(
            RateLimitPermitsInstrumentName,
            unit: "{request}",
            description: "AI explanation query requests admitted by the rate limit boundary.");
        _rateLimitRejections = _meter.CreateCounter<long>(
            RateLimitRejectionsInstrumentName,
            unit: "{request}",
            description: "AI explanation query requests rejected by the rate limit boundary.");
        _outcomes = _meter.CreateCounter<long>(
            OutcomesInstrumentName,
            unit: "{request}",
            description: "AI explanation query outcomes with a bounded outcome tag.");
        _databaseReadDuration = _meter.CreateHistogram<double>(
            DatabaseReadDurationInstrumentName,
            unit: "ms",
            description: "AI explanation PostgreSQL read duration in milliseconds.");
    }

    public void RecordAuthenticationFailure() =>
        _authenticationFailures.Add(1);

    public void RecordRateLimitPermit() => _rateLimitPermits.Add(1);

    public void RecordRateLimitRejection() => _rateLimitRejections.Add(1);

    public void RecordOutcome(AiExplanationQueryOutcome outcome) =>
        _outcomes.Add(
            1,
            outcome switch
            {
                AiExplanationQueryOutcome.Pending => PendingOutcomeTag,
                AiExplanationQueryOutcome.Completed => CompletedOutcomeTag,
                AiExplanationQueryOutcome.Failed => FailedOutcomeTag,
                AiExplanationQueryOutcome.NotFound => NotFoundOutcomeTag,
                AiExplanationQueryOutcome.Timeout => TimeoutOutcomeTag,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(outcome),
                    outcome,
                    "Unsupported AI explanation query outcome.")
            });

    public void RecordDatabaseReadDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        _databaseReadDuration.Record(duration.TotalMilliseconds);
    }
}
