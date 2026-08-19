using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

[Collection(KafkaIntegrationCollection.CollectionName)]
public sealed class KafkaReleaseRiskEventProducerIntegrationTests
{
    private readonly KafkaIntegrationFixture _kafka;

    public KafkaReleaseRiskEventProducerIntegrationTests(
        KafkaIntegrationFixture kafka)
    {
        _kafka = kafka;
    }

    [Fact]
    public async Task PublishAsync_ProducesExpectedTopicKeyAndExactVersionOnePayload()
    {
        var topic = await _kafka.CreateTopicAsync();
        var envelope = CreateEnvelope();
        using var producer = CreateProducer(_kafka.BootstrapServers, topic);

        await producer.PublishAsync(envelope, CancellationToken.None);

        using var consumer = CreateConsumer(topic);
        var consumed = consumer.Consume(TimeSpan.FromSeconds(10));

        Assert.NotNull(consumed);
        Assert.Equal(topic, consumed.Topic);
        Assert.Equal(envelope.EventId.ToString("D"), consumed.Message.Key);
        Assert.Equal(envelope.SerializeToUtf8Bytes(), consumed.Message.Value);
        Assert.Equal(
            envelope.Serialize(),
            Encoding.UTF8.GetString(consumed.Message.Value));
    }

    [Fact]
    public async Task PublishAsync_WithUnknownTopic_PropagatesDeliveryFailure()
    {
        var missingTopic = $"missing-releaseguard-topic-{Guid.NewGuid():N}";
        var envelope = CreateEnvelope();
        using var producer = CreateProducer(
            _kafka.BootstrapServers,
            missingTopic,
            timeoutMilliseconds: 1_000);

        await Assert.ThrowsAsync<ProduceException<string, byte[]>>(
            () => producer.PublishAsync(envelope, CancellationToken.None));
    }

    [Fact]
    public async Task PublishAsync_WithUnavailableBroker_PropagatesDeliveryFailure()
    {
        var unavailablePort = KafkaIntegrationFixture.FindAvailableTcpPort();
        var envelope = CreateEnvelope();
        using var producer = CreateProducer(
            $"127.0.0.1:{unavailablePort}",
            "releaseguard.release-risk-assessed",
            timeoutMilliseconds: 1_000);

        await Assert.ThrowsAsync<ProduceException<string, byte[]>>(
            () => producer.PublishAsync(envelope, CancellationToken.None));
    }

    [Fact]
    public async Task PublishAsync_WhenCanceledWhileWaitingForBroker_PropagatesCancellation()
    {
        var unavailablePort = KafkaIntegrationFixture.FindAvailableTcpPort();
        var envelope = CreateEnvelope();
        using var producer = CreateProducer(
            $"127.0.0.1:{unavailablePort}",
            "releaseguard.release-risk-assessed",
            timeoutMilliseconds: 30_000);
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => producer.PublishAsync(envelope, cancellation.Token));
    }

    [Fact]
    public async Task PublishAsync_WithPreCanceledToken_DoesNotEnqueueMessage()
    {
        var topic = await _kafka.CreateTopicAsync();
        var envelope = CreateEnvelope();
        using var producer = CreateProducer(_kafka.BootstrapServers, topic);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => producer.PublishAsync(envelope, cancellation.Token));

        using var consumer = CreateConsumer(topic);
        Assert.Null(consumer.Consume(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task PublishAsync_WithChangedEventContract_RejectsBeforeProduce()
    {
        var envelope = CreateEnvelope() with { SchemaVersion = 2 };
        using var producer = CreateProducer(
            "127.0.0.1:1",
            "releaseguard.release-risk-assessed");

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => producer.PublishAsync(envelope, CancellationToken.None));

        Assert.Equal("envelope", exception.ParamName);
    }

    private KafkaReleaseRiskEventProducer CreateProducer(
        string bootstrapServers,
        string topic,
        int timeoutMilliseconds = 5_000) =>
        new(Options.Create(new KafkaProducerOptions
        {
            BootstrapServers = bootstrapServers,
            Topic = topic,
            ClientId = $"releaseguard-kafka-integration-{Guid.NewGuid():N}",
            DeliveryTimeoutMilliseconds = timeoutMilliseconds,
            RequestTimeoutMilliseconds = Math.Min(timeoutMilliseconds, 1_000),
            MaximumRetries = 2
        }));

    private IConsumer<string, byte[]> CreateConsumer(string topic)
    {
        var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = _kafka.BootstrapServers,
            GroupId = $"releaseguard-kafka-integration-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            AllowAutoCreateTopics = false
        }).Build();
        consumer.Subscribe(topic);
        return consumer;
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
        var riskAssessment = new ReleaseRiskEvaluator().Evaluate(riskInput);

        return ReleaseRiskOutboxEnvelope.Create(
            deliveryId,
            riskInput,
            riskAssessment);
    }
}
