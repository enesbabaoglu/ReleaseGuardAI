using System.Collections.Concurrent;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

internal sealed class TestAiExplanationQueryMetrics :
    IAiExplanationQueryMetrics
{
    private readonly ConcurrentQueue<AiExplanationQueryOutcome> _outcomes = new();
    private readonly ConcurrentQueue<TimeSpan> _databaseReadDurations = new();
    private int _authenticationFailures;
    private int _rateLimitPermits;
    private int _rateLimitRejections;

    public int AuthenticationFailures =>
        Volatile.Read(ref _authenticationFailures);

    public int RateLimitPermits => Volatile.Read(ref _rateLimitPermits);

    public int RateLimitRejections => Volatile.Read(ref _rateLimitRejections);

    public IReadOnlyList<AiExplanationQueryOutcome> Outcomes =>
        _outcomes.ToArray();

    public IReadOnlyList<TimeSpan> DatabaseReadDurations =>
        _databaseReadDurations.ToArray();

    public void RecordAuthenticationFailure() =>
        Interlocked.Increment(ref _authenticationFailures);

    public void RecordRateLimitPermit() =>
        Interlocked.Increment(ref _rateLimitPermits);

    public void RecordRateLimitRejection() =>
        Interlocked.Increment(ref _rateLimitRejections);

    public void RecordOutcome(AiExplanationQueryOutcome outcome) =>
        _outcomes.Enqueue(outcome);

    public void RecordDatabaseReadDuration(TimeSpan duration) =>
        _databaseReadDurations.Enqueue(duration);
}
