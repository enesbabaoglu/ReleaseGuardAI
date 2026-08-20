using Microsoft.Extensions.Options;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class AiExplanationQueryRateLimitBoundaryTests
{
    [Fact]
    public void AttemptAcquire_UsesOneBoundedQueueFreeBudget()
    {
        var timeProvider = new ManualTimeProvider();
        var boundary = new AiExplanationQueryRateLimitBoundary(
            Options.Create(
                new AiExplanationQueryRateLimitOptions
                {
                    PermitLimit = 2,
                    WindowMilliseconds = 60_000
                }),
            timeProvider);
        var first = boundary.AttemptAcquire();
        var second = boundary.AttemptAcquire();
        var rejected = boundary.AttemptAcquire();

        Assert.True(first.IsAcquired);
        Assert.True(second.IsAcquired);
        Assert.False(rejected.IsAcquired);
        Assert.Equal(60, rejected.RetryAfterSeconds);

        timeProvider.Advance(TimeSpan.FromMilliseconds(59_999));
        var immediatelyBeforeReset = boundary.AttemptAcquire();
        Assert.False(immediatelyBeforeReset.IsAcquired);
        Assert.Equal(1, immediatelyBeforeReset.RetryAfterSeconds);

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(boundary.AttemptAcquire().IsAcquired);
    }

    [Fact]
    public void AttemptAcquire_ConcurrentRequestsNeverExceedConfiguredBudget()
    {
        var boundary = new AiExplanationQueryRateLimitBoundary(
            Options.Create(
                new AiExplanationQueryRateLimitOptions
                {
                    PermitLimit = 10,
                    WindowMilliseconds = 60_000
                }),
            new ManualTimeProvider());
        var acquired = 0;

        Parallel.For(
            0,
            100,
            _ =>
            {
                if (boundary.AttemptAcquire().IsAcquired)
                {
                    Interlocked.Increment(ref acquired);
                }
            });

        Assert.Equal(10, acquired);
    }
}
