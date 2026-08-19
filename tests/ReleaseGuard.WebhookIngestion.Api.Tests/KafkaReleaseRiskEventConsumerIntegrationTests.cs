using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

[Collection(KafkaIntegrationCollection.CollectionName)]
public sealed class KafkaReleaseRiskEventConsumerIntegrationTests
{
    private readonly KafkaIntegrationFixture _kafka;

    public KafkaReleaseRiskEventConsumerIntegrationTests(
        KafkaIntegrationFixture kafka)
    {
        _kafka = kafka;
    }

    [Fact]
    public async Task Consume_ReturnsExpectedTopicKeyOffsetAndExactVersionOnePayload()
    {
        var topic = await _kafka.CreateTopicAsync();
        var envelope = CreateEnvelope();
        using var producer = CreateProducer(topic);
        await producer.PublishAsync(envelope, CancellationToken.None);
        using var consumer = CreateConsumer(topic);

        var consumed = consumer.Consume(CancellationToken.None);

        Assert.NotNull(consumed);
        Assert.Equal(topic, consumed.Topic);
        Assert.Equal(0, consumed.Partition);
        Assert.True(consumed.Offset >= 0);
        Assert.Equal(envelope.EventId, consumed.MessageKey);
        Assert.Equal(envelope.SerializeToUtf8Bytes(), consumed.Payload);
        Assert.Equal(envelope.Serialize(), consumed.Envelope.Serialize());
    }

    [Fact]
    public async Task Consume_WithMalformedMessageKey_RejectsRecord()
    {
        var topic = await _kafka.CreateTopicAsync();
        var envelope = CreateEnvelope();
        await ProduceRawAsync(topic, "not-a-guid", envelope.SerializeToUtf8Bytes());
        using var consumer = CreateConsumer(topic);

        var exception = Assert.Throws<ReleaseRiskEventContractException>(
            () => consumer.Consume(CancellationToken.None));

        Assert.Contains("key", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"eventId\":")]
    public async Task Consume_WithMalformedPayload_RejectsRecord(string payload)
    {
        var topic = await _kafka.CreateTopicAsync();
        var envelope = CreateEnvelope();
        await ProduceRawAsync(
            topic,
            envelope.EventId.ToString("D"),
            Encoding.UTF8.GetBytes(payload));
        using var consumer = CreateConsumer(topic);

        Assert.Throws<ReleaseRiskEventContractException>(
            () => consumer.Consume(CancellationToken.None));
    }

    [Fact]
    public async Task Consume_WithWrongSchemaVersion_RejectsRecord()
    {
        var topic = await _kafka.CreateTopicAsync();
        var envelope = CreateEnvelope() with { SchemaVersion = 2 };
        await ProduceRawAsync(
            topic,
            envelope.EventId.ToString("D"),
            envelope.SerializeToUtf8Bytes());
        using var consumer = CreateConsumer(topic);

        Assert.Throws<ReleaseRiskEventContractException>(
            () => consumer.Consume(CancellationToken.None));
    }

    [Fact]
    public async Task Consume_WithWrongEventType_RejectsRecord()
    {
        var topic = await _kafka.CreateTopicAsync();
        var envelope = CreateEnvelope() with
        {
            EventType = "releaseguard.release-risk-changed"
        };
        await ProduceRawAsync(
            topic,
            envelope.EventId.ToString("D"),
            envelope.SerializeToUtf8Bytes());
        using var consumer = CreateConsumer(topic);

        Assert.Throws<ReleaseRiskEventContractException>(
            () => consumer.Consume(CancellationToken.None));
    }

    [Fact]
    public async Task Consume_WithKeyDifferentFromEventId_RejectsRecord()
    {
        var topic = await _kafka.CreateTopicAsync();
        var envelope = CreateEnvelope();
        await ProduceRawAsync(
            topic,
            Guid.NewGuid().ToString("D"),
            envelope.SerializeToUtf8Bytes());
        using var consumer = CreateConsumer(topic);

        Assert.Throws<ReleaseRiskEventContractException>(
            () => consumer.Consume(CancellationToken.None));
    }

    [Fact]
    public async Task Consume_WithDifferentConfiguredTopic_DoesNotReadOtherTopic()
    {
        var sourceTopic = await _kafka.CreateTopicAsync();
        var configuredTopic = await _kafka.CreateTopicAsync();
        var envelope = CreateEnvelope();
        using var producer = CreateProducer(sourceTopic);
        await producer.PublishAsync(envelope, CancellationToken.None);
        using var consumer = CreateConsumer(
            configuredTopic,
            timeoutMilliseconds: 500);

        var consumed = consumer.Consume(CancellationToken.None);

        Assert.Null(consumed);
    }

    [Fact]
    public void Consume_WithUnavailableBroker_ReturnsNullAtBoundedTimeout()
    {
        var unavailablePort = KafkaIntegrationFixture.FindAvailableTcpPort();
        using var consumer = CreateConsumer(
            "releaseguard.release-risk-assessed",
            timeoutMilliseconds: 500,
            bootstrapServers: $"127.0.0.1:{unavailablePort}");

        var consumed = consumer.Consume(CancellationToken.None);

        Assert.Null(consumed);
    }

    [Fact]
    public async Task Consume_WithPreCanceledToken_PropagatesCancellation()
    {
        var topic = await _kafka.CreateTopicAsync();
        using var consumer = CreateConsumer(topic);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => consumer.Consume(cancellation.Token));
    }

    [Fact]
    public async Task Consume_WhenCanceledWhileWaiting_PropagatesCancellation()
    {
        var topic = await _kafka.CreateTopicAsync();
        using var consumer = CreateConsumer(topic, timeoutMilliseconds: 10_000);
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(250));

        Assert.ThrowsAny<OperationCanceledException>(
            () => consumer.Consume(cancellation.Token));
    }

    [Fact]
    public async Task Dispose_WithoutCommit_AllowsSameGroupToReadRecordAgain()
    {
        var topic = await _kafka.CreateTopicAsync();
        var groupId = $"releaseguard-no-commit-{Guid.NewGuid():N}";
        var envelope = CreateEnvelope();
        using var producer = CreateProducer(topic);
        await producer.PublishAsync(envelope, CancellationToken.None);

        using (var firstConsumer = CreateConsumer(topic, groupId))
        {
            var first = firstConsumer.Consume(CancellationToken.None);
            Assert.NotNull(first);
            Assert.Equal(envelope.EventId, first.MessageKey);
        }

        using var replacementConsumer = CreateConsumer(topic, groupId);
        var replayed = replacementConsumer.Consume(CancellationToken.None);

        Assert.NotNull(replayed);
        Assert.Equal(envelope.EventId, replayed.MessageKey);
        Assert.Equal(envelope.SerializeToUtf8Bytes(), replayed.Payload);
    }

    [Fact]
    public async Task Commit_AfterConsume_PreventsSameGroupReplay()
    {
        var topic = await _kafka.CreateTopicAsync();
        var groupId = $"releaseguard-committed-{Guid.NewGuid():N}";
        var envelope = CreateEnvelope();
        using var producer = CreateProducer(topic);
        await producer.PublishAsync(envelope, CancellationToken.None);

        using (var firstConsumer = CreateConsumer(topic, groupId))
        {
            var consumed = firstConsumer.Consume(CancellationToken.None);
            Assert.NotNull(consumed);
            firstConsumer.Commit(consumed);
        }

        using var replacementConsumer = CreateConsumer(
            topic,
            groupId,
            timeoutMilliseconds: 1_000);

        Assert.Null(replacementConsumer.Consume(CancellationToken.None));
    }

    [Fact]
    public async Task Commit_WithRecordNotReturnedByConsumer_RejectsBeforeBrokerCall()
    {
        var topic = await _kafka.CreateTopicAsync();
        var envelope = CreateEnvelope();
        using var consumer = CreateConsumer(topic);
        var unconsumed = new ConsumedReleaseRiskEvent(
            topic,
            0,
            0,
            envelope.EventId,
            envelope.SerializeToUtf8Bytes(),
            envelope);

        var exception = Assert.Throws<ArgumentException>(
            () => consumer.Commit(unconsumed));

        Assert.Equal("consumedEvent", exception.ParamName);
    }

    private KafkaReleaseRiskEventProducer CreateProducer(string topic) =>
        new(Options.Create(new KafkaProducerOptions
        {
            BootstrapServers = _kafka.BootstrapServers,
            Topic = topic,
            ClientId = $"releaseguard-consumer-test-producer-{Guid.NewGuid():N}",
            DeliveryTimeoutMilliseconds = 5_000,
            RequestTimeoutMilliseconds = 1_000,
            MaximumRetries = 2
        }));

    private KafkaReleaseRiskEventConsumer CreateConsumer(
        string topic,
        string? groupId = null,
        int timeoutMilliseconds = 5_000,
        string? bootstrapServers = null)
    {
        var configuredBootstrapServers = bootstrapServers ??
            _kafka.BootstrapServers;
        var producerOptions = new KafkaProducerOptions
        {
            BootstrapServers = configuredBootstrapServers,
            Topic = topic
        };
        return new KafkaReleaseRiskEventConsumer(
            Options.Create(new KafkaConsumerOptions
            {
                BootstrapServers = configuredBootstrapServers,
                Topic = topic,
                GroupId = groupId ??
                    $"releaseguard-consumer-test-{Guid.NewGuid():N}",
                ClientId = $"releaseguard-consumer-test-{Guid.NewGuid():N}",
                ConsumeTimeoutMilliseconds = timeoutMilliseconds
            }),
            Options.Create(producerOptions));
    }

    private async Task ProduceRawAsync(
        string topic,
        string key,
        byte[] payload)
    {
        using var producer = new ProducerBuilder<string, byte[]>(
            new ProducerConfig
            {
                BootstrapServers = _kafka.BootstrapServers,
                ClientId = $"releaseguard-raw-test-producer-{Guid.NewGuid():N}",
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
        var deliveryId = Guid.NewGuid();
        var riskInput = new ReleaseRiskInput(
            deliveryId,
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
            deliveryId,
            riskInput,
            new ReleaseRiskEvaluator().Evaluate(riskInput));
    }
}
