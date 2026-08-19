using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class ReleaseRiskExplanationProcessorTests
{
    [Fact]
    public async Task ProcessPendingBatch_CallsClientWithClaimedSnapshotThenCompletes()
    {
        var claim = CreateClaim();
        var store = new RecordingStore(claim);
        var explanation = CreateExplanation(claim.EventId);
        var client = new DelegateClient((envelope, _) =>
        {
            Assert.Equal(claim.Envelope.Serialize(), envelope.Serialize());
            return Task.FromResult(explanation);
        });
        using var processor = CreateProcessor(store, client);

        var processed = await processor.ProcessPendingBatchAsync(
            CancellationToken.None);

        Assert.Equal(1, processed);
        Assert.Same(explanation, store.CompletedExplanation);
        Assert.False(store.MarkedFailed);
        Assert.False(store.Released);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("conflict")]
    public async Task RetryableFailure_SchedulesExplicitBackoff(string failure)
    {
        var claim = CreateClaim(attemptCount: 3);
        var store = new RecordingStore(claim);
        var client = new DelegateClient((_, _) => failure switch
        {
            "timeout" => throw new TimeoutException("Simulated timeout."),
            _ => throw new ReleaseRiskExplanationEventIdConflictException(
                claim.EventId,
                Guid.NewGuid())
        });
        using var processor = CreateProcessor(store, client);

        var processed = await processor.ProcessPendingBatchAsync(
            CancellationToken.None);

        Assert.Equal(1, processed);
        Assert.True(store.MarkedFailed);
        Assert.Equal(TimeSpan.FromSeconds(4), store.RetryDelay);
        Assert.Null(store.CompletedExplanation);
        Assert.False(store.Released);
    }

    [Fact]
    public async Task CallerCancellation_ReleasesClaimForRestartAndPropagates()
    {
        var claim = CreateClaim();
        var store = new RecordingStore(claim);
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new DelegateClient(async (_, cancellationToken) =>
        {
            started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });
        using var processor = CreateProcessor(store, client);
        using var cancellation = new CancellationTokenSource();

        var processing = processor.ProcessPendingBatchAsync(cancellation.Token);
        await started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        Assert.True(store.Released);
        Assert.False(store.MarkedFailed);
        Assert.Null(store.CompletedExplanation);
    }

    [Fact]
    public async Task LostOwnership_RejectsStaleCompletionWithoutSchedulingFailure()
    {
        var claim = CreateClaim();
        var store = new RecordingStore(claim)
        {
            CompleteResult = false
        };
        var client = new DelegateClient((_, _) =>
            Task.FromResult(CreateExplanation(claim.EventId)));
        using var processor = CreateProcessor(store, client);

        var processed = await processor.ProcessPendingBatchAsync(
            CancellationToken.None);

        Assert.Equal(1, processed);
        Assert.Equal(1, store.CompletionAttempts);
        Assert.False(store.MarkedFailed);
        Assert.False(store.Released);
    }

    [Fact]
    public async Task DisabledProcessor_DoesNotClaimWork()
    {
        var store = new RecordingStore(CreateClaim());
        var client = new DelegateClient((_, _) =>
            throw new InvalidOperationException("Client must not be called."));
        using var processor = new ReleaseRiskExplanationProcessor(
            store,
            client,
            Options.Create(new AiExplanationProcessorOptions()),
            NullLogger<ReleaseRiskExplanationProcessor>.Instance);

        await processor.StartAsync(CancellationToken.None);
        await processor.StopAsync(CancellationToken.None);

        Assert.Equal(0, store.ClaimCalls);
    }

    private static ReleaseRiskExplanationProcessor CreateProcessor(
        IReleaseRiskExplanationStore store,
        IReleaseRiskExplanationClient client) =>
        new(
            store,
            client,
            Options.Create(new AiExplanationProcessorOptions
            {
                Enabled = true,
                BatchSize = 10,
                PollIntervalMilliseconds = 100,
                LeaseDurationMilliseconds = 30_000,
                InitialRetryDelayMilliseconds = 1_000,
                MaximumRetryDelayMilliseconds = 60_000,
                StateUpdateTimeoutMilliseconds = 1_000
            }),
            NullLogger<ReleaseRiskExplanationProcessor>.Instance);

    private static ReleaseRiskExplanationClaim CreateClaim(int attemptCount = 1)
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
        var envelope = ReleaseRiskOutboxEnvelope.Create(
            eventId,
            input,
            new ReleaseRiskEvaluator().Evaluate(input));
        return new ReleaseRiskExplanationClaim(
            eventId,
            "test-owner",
            attemptCount,
            envelope);
    }

    private static ReleaseRiskExplanation CreateExplanation(Guid eventId) =>
        new()
        {
            EventId = eventId,
            Summary = "This change has elevated release risk.",
            Recommendations = ["Require a focused review."]
        };

    private sealed class DelegateClient : IReleaseRiskExplanationClient
    {
        private readonly Func<
            ReleaseRiskOutboxEnvelope,
            CancellationToken,
            Task<ReleaseRiskExplanation>> _explain;

        public DelegateClient(
            Func<
                ReleaseRiskOutboxEnvelope,
                CancellationToken,
                Task<ReleaseRiskExplanation>> explain)
        {
            _explain = explain;
        }

        public Task<ReleaseRiskExplanation> ExplainAsync(
            ReleaseRiskOutboxEnvelope envelope,
            CancellationToken cancellationToken) =>
            _explain(envelope, cancellationToken);
    }

    private sealed class RecordingStore : IReleaseRiskExplanationStore
    {
        private readonly ReleaseRiskExplanationClaim _claim;

        public RecordingStore(ReleaseRiskExplanationClaim claim)
        {
            _claim = claim;
        }

        public int ClaimCalls { get; private set; }

        public int CompletionAttempts { get; private set; }

        public bool CompleteResult { get; init; } = true;

        public ReleaseRiskExplanation? CompletedExplanation { get; private set; }

        public bool MarkedFailed { get; private set; }

        public TimeSpan? RetryDelay { get; private set; }

        public bool Released { get; private set; }

        public Task<IReadOnlyList<ReleaseRiskExplanationClaim>> ClaimPendingAsync(
            string claimOwner,
            int batchSize,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            ClaimCalls++;
            IReadOnlyList<ReleaseRiskExplanationClaim> claims = [_claim];
            return Task.FromResult(claims);
        }

        public Task<bool> MarkCompletedAsync(
            ReleaseRiskExplanationClaim claim,
            ReleaseRiskExplanation explanation,
            CancellationToken cancellationToken)
        {
            CompletionAttempts++;
            CompletedExplanation = explanation;
            return Task.FromResult(CompleteResult);
        }

        public Task<bool> MarkFailedAsync(
            ReleaseRiskExplanationClaim claim,
            TimeSpan retryDelay,
            CancellationToken cancellationToken)
        {
            MarkedFailed = true;
            RetryDelay = retryDelay;
            return Task.FromResult(true);
        }

        public Task<bool> ReleaseClaimAsync(
            ReleaseRiskExplanationClaim claim,
            CancellationToken cancellationToken)
        {
            Released = true;
            return Task.FromResult(true);
        }
    }
}
