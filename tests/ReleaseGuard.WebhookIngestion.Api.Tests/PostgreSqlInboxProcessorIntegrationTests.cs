using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

[Collection(OutboxDispatcherIntegrationCollection.CollectionName)]
public sealed class PostgreSqlInboxProcessorIntegrationTests
{
    private readonly PostgreSqlIntegrationFixture _postgresql;
    private readonly KafkaIntegrationFixture _kafka;

    public PostgreSqlInboxProcessorIntegrationTests(
        PostgreSqlIntegrationFixture postgresql,
        KafkaIntegrationFixture kafka)
    {
        _postgresql = postgresql;
        _kafka = kafka;
    }

    [Fact]
    public async Task ProcessNextAsync_PersistsExactRecordThenCommitsOffset()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        var topic = await _kafka.CreateTopicAsync();
        var groupId = $"releaseguard-inbox-success-{Guid.NewGuid():N}";
        var envelope = CreateEnvelope();
        using var producer = CreateProducer(topic);
        await producer.PublishAsync(envelope, CancellationToken.None);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new PostgreSqlReleaseRiskInboxStore(dataSource);

        ReleaseRiskInboxProcessingResult? result;
        using (var consumer = CreateConsumer(topic, groupId))
        using (var processor = CreateProcessor(consumer, store))
        {
            result = await processor.ProcessNextAsync(CancellationToken.None);
        }

        Assert.NotNull(result);
        Assert.Equal(ReleaseRiskInboxAcceptance.Accepted, result.Acceptance);
        var inbox = await ReadInboxAsync(
            connectionString,
            envelope.EventId,
            envelope.Serialize());
        Assert.Equal(envelope.EventId, inbox.MessageKey);
        Assert.Equal(topic, inbox.Topic);
        Assert.Equal(result.ConsumedEvent.Partition, inbox.Partition);
        Assert.Equal(result.ConsumedEvent.Offset, inbox.Offset);
        Assert.Equal(envelope.SerializeToUtf8Bytes(), inbox.Payload);
        Assert.True(inbox.EnvelopeMatches);

        using var replacement = CreateConsumer(
            topic,
            groupId,
            timeoutMilliseconds: 1_000);
        Assert.Null(replacement.Consume(CancellationToken.None));
    }

    [Fact]
    public async Task DuplicateEventAtSecondOffset_IsIdempotentAndBothOffsetsCommit()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        var topic = await _kafka.CreateTopicAsync();
        var groupId = $"releaseguard-inbox-duplicate-{Guid.NewGuid():N}";
        var envelope = CreateEnvelope();
        using var producer = CreateProducer(topic);
        await producer.PublishAsync(envelope, CancellationToken.None);
        await producer.PublishAsync(envelope, CancellationToken.None);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new PostgreSqlReleaseRiskInboxStore(dataSource);

        using (var consumer = CreateConsumer(topic, groupId))
        using (var processor = CreateProcessor(consumer, store))
        {
            var first = await processor.ProcessNextAsync(CancellationToken.None);
            var second = await processor.ProcessNextAsync(CancellationToken.None);

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(ReleaseRiskInboxAcceptance.Accepted, first.Acceptance);
            Assert.Equal(ReleaseRiskInboxAcceptance.Duplicate, second.Acceptance);
            Assert.True(second.ConsumedEvent.Offset > first.ConsumedEvent.Offset);
        }

        Assert.Equal(1, await CountInboxEventsAsync(connectionString));
        using var replacement = CreateConsumer(
            topic,
            groupId,
            timeoutMilliseconds: 1_000);
        Assert.Null(replacement.Consume(CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentStores_AcceptSameEventOnlyOnce()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        var envelope = CreateEnvelope();
        var topic = $"releaseguard.release-risk-assessed-{Guid.NewGuid():N}";
        var firstRecord = CreateConsumedEvent(envelope, topic, offset: 0);
        var secondRecord = CreateConsumedEvent(envelope, topic, offset: 0);
        await using var firstDataSource = NpgsqlDataSource.Create(connectionString);
        await using var secondDataSource = NpgsqlDataSource.Create(connectionString);
        var firstStore = new PostgreSqlReleaseRiskInboxStore(firstDataSource);
        var secondStore = new PostgreSqlReleaseRiskInboxStore(secondDataSource);

        var results = await Task.WhenAll(
            firstStore.AcceptAsync(firstRecord, CancellationToken.None),
            secondStore.AcceptAsync(secondRecord, CancellationToken.None));

        Assert.Equal(1, results.Count(
            result => result == ReleaseRiskInboxAcceptance.Accepted));
        Assert.Equal(1, results.Count(
            result => result == ReleaseRiskInboxAcceptance.Duplicate));
        Assert.Equal(1, await CountInboxEventsAsync(connectionString));
    }

    [Fact]
    public async Task DatabaseFailure_DoesNotCommitAndReplacementReplaysRecord()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        await CreateFailingInboxTriggerAsync(connectionString);
        var topic = await _kafka.CreateTopicAsync();
        var groupId = $"releaseguard-inbox-db-failure-{Guid.NewGuid():N}";
        var envelope = CreateEnvelope();
        using var producer = CreateProducer(topic);
        await producer.PublishAsync(envelope, CancellationToken.None);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new PostgreSqlReleaseRiskInboxStore(dataSource);
        long failedOffset;

        using (var consumer = CreateConsumer(topic, groupId))
        using (var processor = CreateProcessor(consumer, store))
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => processor.ProcessNextAsync(CancellationToken.None));
            Assert.Equal("P0001", exception.SqlState);
            failedOffset = consumer.LastConsumedOffset;
        }

        Assert.Equal(0, await CountInboxEventsAsync(connectionString));
        await DropFailingInboxTriggerAsync(connectionString);

        using var replacement = CreateConsumer(topic, groupId);
        using var replacementProcessor = CreateProcessor(replacement, store);
        var replayed = await replacementProcessor.ProcessNextAsync(
            CancellationToken.None);

        Assert.NotNull(replayed);
        Assert.Equal(failedOffset, replayed.ConsumedEvent.Offset);
        Assert.Equal(ReleaseRiskInboxAcceptance.Accepted, replayed.Acceptance);
        Assert.Equal(1, await CountInboxEventsAsync(connectionString));
    }

    [Fact]
    public async Task CommitFailureAfterDurableAcceptance_ReplayIsDuplicate()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        var topic = await _kafka.CreateTopicAsync();
        var groupId = $"releaseguard-inbox-ambiguous-commit-{Guid.NewGuid():N}";
        var envelope = CreateEnvelope();
        using var producer = CreateProducer(topic);
        await producer.PublishAsync(envelope, CancellationToken.None);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new PostgreSqlReleaseRiskInboxStore(dataSource);
        long failedOffset;

        using (var innerConsumer = CreateConsumer(topic, groupId))
        {
            var ambiguousConsumer = new ThrowBeforeCommitConsumer(innerConsumer);
            using var processor = CreateProcessor(ambiguousConsumer, store);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => processor.ProcessNextAsync(CancellationToken.None));
            Assert.Contains("ambiguous", exception.Message);
            failedOffset = innerConsumer.LastConsumedOffset;
        }

        Assert.Equal(1, await CountInboxEventsAsync(connectionString));

        using var replacement = CreateConsumer(topic, groupId);
        using var replacementProcessor = CreateProcessor(replacement, store);
        var replayed = await replacementProcessor.ProcessNextAsync(
            CancellationToken.None);

        Assert.NotNull(replayed);
        Assert.Equal(failedOffset, replayed.ConsumedEvent.Offset);
        Assert.Equal(ReleaseRiskInboxAcceptance.Duplicate, replayed.Acceptance);
        Assert.Equal(1, await CountInboxEventsAsync(connectionString));
    }

    [Fact]
    public async Task CancellationAfterConsume_DoesNotCommitAndReplacementReplaysRecord()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        var topic = await _kafka.CreateTopicAsync();
        var groupId = $"releaseguard-inbox-cancellation-{Guid.NewGuid():N}";
        var envelope = CreateEnvelope();
        using var producer = CreateProducer(topic);
        await producer.PublishAsync(envelope, CancellationToken.None);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var durableStore = new PostgreSqlReleaseRiskInboxStore(dataSource);
        using var cancellation = new CancellationTokenSource();
        long canceledOffset;

        using (var consumer = CreateConsumer(topic, groupId))
        using (var processor = CreateProcessor(
                   consumer,
                   new CancelWithoutPersistingStore(cancellation)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => processor.ProcessNextAsync(cancellation.Token));
            canceledOffset = consumer.LastConsumedOffset;
        }

        Assert.Equal(0, await CountInboxEventsAsync(connectionString));

        using var replacement = CreateConsumer(topic, groupId);
        using var replacementProcessor = CreateProcessor(
            replacement,
            durableStore);
        var replayed = await replacementProcessor.ProcessNextAsync(
            CancellationToken.None);

        Assert.NotNull(replayed);
        Assert.Equal(canceledOffset, replayed.ConsumedEvent.Offset);
        Assert.Equal(ReleaseRiskInboxAcceptance.Accepted, replayed.Acceptance);
    }

    [Fact]
    public async Task MalformedRecord_IsNotCommittedAndReplacementSeesSamePoisonRecord()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        var topic = await _kafka.CreateTopicAsync();
        var groupId = $"releaseguard-inbox-malformed-{Guid.NewGuid():N}";
        var key = Guid.NewGuid();
        await ProduceRawAsync(
            topic,
            key.ToString("D"),
            Encoding.UTF8.GetBytes("not-json"));
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new PostgreSqlReleaseRiskInboxStore(dataSource);

        using (var consumer = CreateConsumer(topic, groupId))
        using (var processor = CreateProcessor(consumer, store))
        {
            await Assert.ThrowsAsync<ReleaseRiskEventContractException>(
                () => processor.ProcessNextAsync(CancellationToken.None));
        }

        using (var replacement = CreateConsumer(topic, groupId))
        using (var replacementProcessor = CreateProcessor(replacement, store))
        {
            await Assert.ThrowsAsync<ReleaseRiskEventContractException>(
                () => replacementProcessor.ProcessNextAsync(
                    CancellationToken.None));
        }

        Assert.Equal(0, await CountInboxEventsAsync(connectionString));
    }

    [Fact]
    public async Task DuplicateEventIdWithDifferentPayload_IsConflictAndNotCommitted()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        var topic = await _kafka.CreateTopicAsync();
        var groupId = $"releaseguard-inbox-conflict-{Guid.NewGuid():N}";
        var firstEnvelope = CreateEnvelope();
        var conflictingInput = firstEnvelope.RiskInput with
        {
            Title = "Conflicting payload for the same delivery"
        };
        var conflictingEnvelope = ReleaseRiskOutboxEnvelope.Create(
            firstEnvelope.EventId,
            conflictingInput,
            new ReleaseRiskEvaluator().Evaluate(conflictingInput));
        using var producer = CreateProducer(topic);
        await producer.PublishAsync(firstEnvelope, CancellationToken.None);
        await producer.PublishAsync(conflictingEnvelope, CancellationToken.None);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new PostgreSqlReleaseRiskInboxStore(dataSource);

        using (var consumer = CreateConsumer(topic, groupId))
        using (var processor = CreateProcessor(consumer, store))
        {
            var first = await processor.ProcessNextAsync(CancellationToken.None);
            Assert.NotNull(first);
            Assert.Equal(ReleaseRiskInboxAcceptance.Accepted, first.Acceptance);

            await Assert.ThrowsAsync<ReleaseRiskInboxConflictException>(
                () => processor.ProcessNextAsync(CancellationToken.None));
        }

        Assert.Equal(1, await CountInboxEventsAsync(connectionString));
        using var replacement = CreateConsumer(topic, groupId);
        using var replacementProcessor = CreateProcessor(replacement, store);
        await Assert.ThrowsAsync<ReleaseRiskInboxConflictException>(
            () => replacementProcessor.ProcessNextAsync(CancellationToken.None));
    }

    private async Task<string> CreateInitializedDatabaseAsync()
    {
        var connectionString = await _postgresql.CreateIsolatedDatabaseAsync();
        using var application = new PostgreSqlTestApplicationFactory(
            connectionString,
            applyMigrationsOnStartup: true);
        using var client = application.CreateClient();
        using var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();
        return connectionString;
    }

    private KafkaReleaseRiskEventProducer CreateProducer(string topic) =>
        new(Options.Create(new KafkaProducerOptions
        {
            BootstrapServers = _kafka.BootstrapServers,
            Topic = topic,
            ClientId = $"releaseguard-inbox-producer-{Guid.NewGuid():N}",
            DeliveryTimeoutMilliseconds = 5_000,
            RequestTimeoutMilliseconds = 1_000,
            MaximumRetries = 2
        }));

    private TrackingConsumer CreateConsumer(
        string topic,
        string groupId,
        int timeoutMilliseconds = 5_000)
    {
        var consumer = new KafkaReleaseRiskEventConsumer(
            Options.Create(new KafkaConsumerOptions
            {
                BootstrapServers = _kafka.BootstrapServers,
                Topic = topic,
                GroupId = groupId,
                ClientId = $"releaseguard-inbox-consumer-{Guid.NewGuid():N}",
                ConsumeTimeoutMilliseconds = timeoutMilliseconds,
                BrokerRequestTimeoutMilliseconds = 5_000
            }),
            Options.Create(new KafkaProducerOptions
            {
                BootstrapServers = _kafka.BootstrapServers,
                Topic = topic
            }));
        return new TrackingConsumer(consumer);
    }

    private static ReleaseRiskInboxProcessor CreateProcessor(
        IReleaseRiskEventConsumer consumer,
        IReleaseRiskInboxStore store) =>
        new(
            () => consumer,
            store,
            Options.Create(new ReleaseRiskInboxProcessorOptions
            {
                Enabled = true,
                PersistenceTimeoutMilliseconds = 5_000
            }),
            NullLogger<ReleaseRiskInboxProcessor>.Instance);

    private async Task ProduceRawAsync(
        string topic,
        string key,
        byte[] payload)
    {
        using var producer = new ProducerBuilder<string, byte[]>(
            new ProducerConfig
            {
                BootstrapServers = _kafka.BootstrapServers,
                ClientId = $"releaseguard-inbox-raw-producer-{Guid.NewGuid():N}",
                Acks = Acks.All,
                AllowAutoCreateTopics = false
            }).Build();
        var result = await producer.ProduceAsync(
            topic,
            new Message<string, byte[]>
            {
                Key = key,
                Value = payload
            });
        Assert.Equal(PersistenceStatus.Persisted, result.Status);
    }

    private static ReleaseRiskOutboxEnvelope CreateEnvelope()
    {
        var eventId = Guid.NewGuid();
        var input = new ReleaseRiskInput(
            eventId,
            "github",
            GitHubRiskInputMapper.ChangeUpdatedKind,
            "acme/ReleaseGuard",
            42,
            "Protect production releases",
            "octocat",
            "main",
            "feature/release-guard",
            false,
            20,
            1_000,
            5);
        return ReleaseRiskOutboxEnvelope.Create(
            eventId,
            input,
            new ReleaseRiskEvaluator().Evaluate(input));
    }

    private static ConsumedReleaseRiskEvent CreateConsumedEvent(
        ReleaseRiskOutboxEnvelope envelope,
        string topic,
        long offset) =>
        new(
            topic,
            0,
            offset,
            envelope.EventId,
            envelope.SerializeToUtf8Bytes(),
            envelope);

    private static async Task<InboxRow> ReadInboxAsync(
        string connectionString,
        Guid eventId,
        string expectedEnvelope)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                message_key,
                topic,
                kafka_partition,
                kafka_offset,
                payload,
                envelope = @expected_envelope::jsonb
            FROM release_risk_event_inbox
            WHERE event_id = @event_id;
            """,
            connection);
        command.Parameters.AddWithValue("event_id", eventId);
        command.Parameters.AddWithValue("expected_envelope", expectedEnvelope);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new InboxRow(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetInt64(3),
            reader.GetFieldValue<byte[]>(4),
            reader.GetBoolean(5));
    }

    private static async Task<long> CountInboxEventsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM release_risk_event_inbox;",
            connection);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task CreateFailingInboxTriggerAsync(
        string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            CREATE FUNCTION reject_release_risk_inbox()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
                RAISE EXCEPTION 'forced inbox insert failure';
            END;
            $function$;

            CREATE TRIGGER reject_release_risk_inbox_insert
            BEFORE INSERT ON release_risk_event_inbox
            FOR EACH ROW
            EXECUTE FUNCTION reject_release_risk_inbox();
            """,
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropFailingInboxTriggerAsync(
        string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            DROP TRIGGER reject_release_risk_inbox_insert
            ON release_risk_event_inbox;
            DROP FUNCTION reject_release_risk_inbox();
            """,
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record InboxRow(
        Guid MessageKey,
        string Topic,
        int Partition,
        long Offset,
        byte[] Payload,
        bool EnvelopeMatches);

    private sealed class TrackingConsumer :
        IReleaseRiskEventConsumer,
        IDisposable
    {
        private readonly KafkaReleaseRiskEventConsumer _inner;

        public TrackingConsumer(KafkaReleaseRiskEventConsumer inner)
        {
            _inner = inner;
        }

        public long LastConsumedOffset { get; private set; } = -1;

        public ConsumedReleaseRiskEvent? Consume(
            CancellationToken cancellationToken)
        {
            var result = _inner.Consume(cancellationToken);
            if (result is not null)
            {
                LastConsumedOffset = result.Offset;
            }

            return result;
        }

        public void Commit(ConsumedReleaseRiskEvent consumedEvent) =>
            _inner.Commit(consumedEvent);

        public void Dispose() => _inner.Dispose();
    }

    private sealed class ThrowBeforeCommitConsumer : IReleaseRiskEventConsumer
    {
        private readonly IReleaseRiskEventConsumer _inner;

        public ThrowBeforeCommitConsumer(IReleaseRiskEventConsumer inner)
        {
            _inner = inner;
        }

        public ConsumedReleaseRiskEvent? Consume(
            CancellationToken cancellationToken) =>
            _inner.Consume(cancellationToken);

        public void Commit(ConsumedReleaseRiskEvent consumedEvent) =>
            throw new InvalidOperationException(
                "Simulated ambiguous Kafka offset commit result.");
    }

    private sealed class CancelWithoutPersistingStore : IReleaseRiskInboxStore
    {
        private readonly CancellationTokenSource _cancellation;

        public CancelWithoutPersistingStore(
            CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public Task<ReleaseRiskInboxAcceptance> AcceptAsync(
            ConsumedReleaseRiskEvent consumedEvent,
            CancellationToken cancellationToken)
        {
            _cancellation.Cancel();
            return Task.FromResult(ReleaseRiskInboxAcceptance.Accepted);
        }
    }
}
