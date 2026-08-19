using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class ReleaseRiskInboxProcessorTests
{
    [Theory]
    [InlineData(ReleaseRiskInboxAcceptance.Accepted)]
    [InlineData(ReleaseRiskInboxAcceptance.Duplicate)]
    public async Task ProcessNextAsync_PersistsBeforeCommittingOffset(
        ReleaseRiskInboxAcceptance acceptance)
    {
        var operations = new List<string>();
        var consumedEvent = CreateConsumedEvent();
        var consumer = new RecordingConsumer(consumedEvent, operations);
        var store = new RecordingStore(acceptance, operations);
        using var processor = CreateProcessor(consumer, store);

        var result = await processor.ProcessNextAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(acceptance, result.Acceptance);
        Assert.Equal(new[] { "consume", "persist", "commit" }, operations);
    }

    [Fact]
    public async Task ProcessNextAsync_WhenPersistenceFails_DoesNotCommit()
    {
        var operations = new List<string>();
        var consumer = new RecordingConsumer(CreateConsumedEvent(), operations);
        var store = new ThrowingStore(operations);
        using var processor = CreateProcessor(consumer, store);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.ProcessNextAsync(CancellationToken.None));

        Assert.Equal(new[] { "consume", "persist" }, operations);
        Assert.Equal(0, consumer.CommitCount);
    }

    [Fact]
    public async Task ProcessNextAsync_WhenCanceledAfterPersistence_DoesNotCommit()
    {
        var operations = new List<string>();
        var consumer = new RecordingConsumer(CreateConsumedEvent(), operations);
        using var cancellation = new CancellationTokenSource();
        var store = new CancelAfterPersistenceStore(cancellation, operations);
        using var processor = CreateProcessor(consumer, store);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => processor.ProcessNextAsync(cancellation.Token));

        Assert.Equal(new[] { "consume", "persist" }, operations);
        Assert.Equal(0, consumer.CommitCount);
    }

    [Fact]
    public async Task ProcessNextAsync_WhenPersistenceTimesOut_DoesNotCommit()
    {
        var operations = new List<string>();
        var consumer = new RecordingConsumer(CreateConsumedEvent(), operations);
        var store = new BlockingStore(operations);
        using var processor = CreateProcessor(
            consumer,
            store,
            persistenceTimeoutMilliseconds: 1_000);

        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => processor.ProcessNextAsync(CancellationToken.None));

        Assert.Contains("offset was not committed", exception.Message);
        Assert.Equal(new[] { "consume", "persist" }, operations);
        Assert.Equal(0, consumer.CommitCount);
    }

    [Fact]
    public async Task DisabledProcessor_DoesNotCreateConsumer()
    {
        var consumerCreated = false;
        var store = new RecordingStore(
            ReleaseRiskInboxAcceptance.Accepted,
            []);
        using var processor = new ReleaseRiskInboxProcessor(
            () =>
            {
                consumerCreated = true;
                return new RecordingConsumer(null, []);
            },
            store,
            Options.Create(new ReleaseRiskInboxProcessorOptions()),
            NullLogger<ReleaseRiskInboxProcessor>.Instance);

        await processor.StartAsync(CancellationToken.None);
        await processor.StopAsync(CancellationToken.None);

        Assert.False(consumerCreated);
    }

    private static ReleaseRiskInboxProcessor CreateProcessor(
        IReleaseRiskEventConsumer consumer,
        IReleaseRiskInboxStore store,
        int persistenceTimeoutMilliseconds = 5_000) =>
        new(
            () => consumer,
            store,
            Options.Create(new ReleaseRiskInboxProcessorOptions
            {
                Enabled = true,
                PersistenceTimeoutMilliseconds = persistenceTimeoutMilliseconds
            }),
            NullLogger<ReleaseRiskInboxProcessor>.Instance);

    private static ConsumedReleaseRiskEvent CreateConsumedEvent()
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
        return new ConsumedReleaseRiskEvent(
            "releaseguard.release-risk-assessed",
            0,
            12,
            eventId,
            envelope.SerializeToUtf8Bytes(),
            envelope);
    }

    private sealed class RecordingConsumer : IReleaseRiskEventConsumer
    {
        private readonly ConsumedReleaseRiskEvent? _consumedEvent;
        private readonly List<string> _operations;

        public RecordingConsumer(
            ConsumedReleaseRiskEvent? consumedEvent,
            List<string> operations)
        {
            _consumedEvent = consumedEvent;
            _operations = operations;
        }

        public int CommitCount { get; private set; }

        public ConsumedReleaseRiskEvent? Consume(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _operations.Add("consume");
            return _consumedEvent;
        }

        public void Commit(ConsumedReleaseRiskEvent consumedEvent)
        {
            _operations.Add("commit");
            CommitCount++;
        }
    }

    private sealed class RecordingStore : IReleaseRiskInboxStore
    {
        private readonly ReleaseRiskInboxAcceptance _acceptance;
        private readonly List<string> _operations;

        public RecordingStore(
            ReleaseRiskInboxAcceptance acceptance,
            List<string> operations)
        {
            _acceptance = acceptance;
            _operations = operations;
        }

        public Task<ReleaseRiskInboxAcceptance> AcceptAsync(
            ConsumedReleaseRiskEvent consumedEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _operations.Add("persist");
            return Task.FromResult(_acceptance);
        }
    }

    private sealed class ThrowingStore : IReleaseRiskInboxStore
    {
        private readonly List<string> _operations;

        public ThrowingStore(List<string> operations)
        {
            _operations = operations;
        }

        public Task<ReleaseRiskInboxAcceptance> AcceptAsync(
            ConsumedReleaseRiskEvent consumedEvent,
            CancellationToken cancellationToken)
        {
            _operations.Add("persist");
            throw new InvalidOperationException("Simulated persistence failure.");
        }
    }

    private sealed class CancelAfterPersistenceStore : IReleaseRiskInboxStore
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly List<string> _operations;

        public CancelAfterPersistenceStore(
            CancellationTokenSource cancellation,
            List<string> operations)
        {
            _cancellation = cancellation;
            _operations = operations;
        }

        public Task<ReleaseRiskInboxAcceptance> AcceptAsync(
            ConsumedReleaseRiskEvent consumedEvent,
            CancellationToken cancellationToken)
        {
            _operations.Add("persist");
            _cancellation.Cancel();
            return Task.FromResult(ReleaseRiskInboxAcceptance.Accepted);
        }
    }

    private sealed class BlockingStore : IReleaseRiskInboxStore
    {
        private readonly List<string> _operations;

        public BlockingStore(List<string> operations)
        {
            _operations = operations;
        }

        public async Task<ReleaseRiskInboxAcceptance> AcceptAsync(
            ConsumedReleaseRiskEvent consumedEvent,
            CancellationToken cancellationToken)
        {
            _operations.Add("persist");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ReleaseRiskInboxAcceptance.Accepted;
        }
    }
}
