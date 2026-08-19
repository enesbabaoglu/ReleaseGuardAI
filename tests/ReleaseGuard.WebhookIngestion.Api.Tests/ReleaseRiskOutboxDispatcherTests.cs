using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class ReleaseRiskOutboxDispatcherTests
{
    [Fact]
    public async Task StopAsync_CancelsInFlightPublishAndReleasesClaim()
    {
        var envelope = CreateEnvelope();
        var claim = new ReleaseRiskOutboxClaim(
            envelope.EventId,
            "test-claim",
            1,
            envelope);
        var store = new SingleClaimStore(claim);
        var producer = new CancelableProducer();
        using var dispatcher = new ReleaseRiskOutboxDispatcher(
            store,
            producer,
            Options.Create(new OutboxDispatcherOptions
            {
                Enabled = true,
                BatchSize = 1,
                PollIntervalMilliseconds = 100,
                LeaseDurationMilliseconds = 5_000,
                StateUpdateTimeoutMilliseconds = 1_000
            }),
            NullLogger<ReleaseRiskOutboxDispatcher>.Instance);

        await dispatcher.StartAsync(CancellationToken.None);
        await producer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await dispatcher.StopAsync(stopTimeout.Token);

        Assert.True(await store.Released.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.False(store.MarkedPublished);
        Assert.False(store.MarkedFailed);
    }

    private static ReleaseRiskOutboxEnvelope CreateEnvelope()
    {
        var eventId = Guid.NewGuid();
        var input = new ReleaseRiskInput(
            eventId,
            "github",
            GitHubRiskInputMapper.ChangeOpenedKind,
            "acme/ReleaseGuard",
            42,
            "Protect production releases",
            "octocat",
            "main",
            "feature/release-guard",
            false,
            4,
            120,
            15);

        return ReleaseRiskOutboxEnvelope.Create(
            eventId,
            input,
            new ReleaseRiskEvaluator().Evaluate(input));
    }

    private sealed class CancelableProducer : IReleaseRiskEventProducer
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PublishAsync(
            ReleaseRiskOutboxEnvelope envelope,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class SingleClaimStore : IReleaseRiskOutboxStore
    {
        private readonly ReleaseRiskOutboxClaim _claim;
        private int _claimReturned;

        public SingleClaimStore(ReleaseRiskOutboxClaim claim)
        {
            _claim = claim;
        }

        public TaskCompletionSource<bool> Released { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool MarkedPublished { get; private set; }

        public bool MarkedFailed { get; private set; }

        public Task<IReadOnlyList<ReleaseRiskOutboxClaim>> ClaimPendingAsync(
            string claimOwner,
            int batchSize,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<ReleaseRiskOutboxClaim> claims =
                Interlocked.Exchange(ref _claimReturned, 1) == 0
                    ? [_claim]
                    : [];
            return Task.FromResult(claims);
        }

        public Task<bool> MarkPublishedAsync(
            ReleaseRiskOutboxClaim claim,
            CancellationToken cancellationToken)
        {
            MarkedPublished = true;
            return Task.FromResult(true);
        }

        public Task<bool> MarkFailedAsync(
            ReleaseRiskOutboxClaim claim,
            TimeSpan retryDelay,
            CancellationToken cancellationToken)
        {
            MarkedFailed = true;
            return Task.FromResult(true);
        }

        public Task<bool> ReleaseClaimAsync(
            ReleaseRiskOutboxClaim claim,
            CancellationToken cancellationToken)
        {
            Released.TrySetResult(true);
            return Task.FromResult(true);
        }
    }
}
