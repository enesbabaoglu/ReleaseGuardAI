using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace ReleaseGuard.WebhookIngestion.Api;

public interface IReleaseRiskEventConsumer
{
    ConsumedReleaseRiskEvent? Consume(CancellationToken cancellationToken);

    void Commit(ConsumedReleaseRiskEvent consumedEvent);
}

public sealed record ConsumedReleaseRiskEvent(
    string Topic,
    int Partition,
    long Offset,
    Guid MessageKey,
    byte[] Payload,
    ReleaseRiskOutboxEnvelope Envelope);

public sealed class ReleaseRiskEventContractException : Exception
{
    public ReleaseRiskEventContractException(string message)
        : base(message)
    {
    }

    public ReleaseRiskEventContractException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class KafkaReleaseRiskEventConsumer :
    IReleaseRiskEventConsumer,
    IDisposable
{
    private readonly IConsumer<string, byte[]> _consumer;
    private readonly string _topic;
    private readonly TimeSpan _consumeTimeout;
    private ConsumedReleaseRiskEvent? _lastDeliveredEvent;

    public KafkaReleaseRiskEventConsumer(
        IOptions<KafkaConsumerOptions> options,
        IOptions<KafkaProducerOptions> producerOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(producerOptions);

        var value = options.Value;
        KafkaConsumerOptions.ThrowIfInvalid(value, producerOptions.Value);
        _topic = value.Topic;
        _consumeTimeout = TimeSpan.FromMilliseconds(
            value.ConsumeTimeoutMilliseconds);
        _consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = value.BootstrapServers,
            GroupId = value.GroupId,
            ClientId = value.ClientId,
            SocketTimeoutMs = value.BrokerRequestTimeoutMilliseconds,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AllowAutoCreateTopics = false
        }).Build();
        _consumer.Subscribe(_topic);
    }

    public ConsumedReleaseRiskEvent? Consume(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = new CancellationTokenSource(_consumeTimeout);
        using var linkedCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken, timeout.Token);

        ConsumeResult<string, byte[]> consumed;
        try
        {
            consumed = _consumer.Consume(linkedCancellation.Token);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested &&
                  timeout.IsCancellationRequested)
        {
            return null;
        }

        var validated = Validate(consumed);
        _lastDeliveredEvent = validated;
        return validated;
    }

    public void Commit(ConsumedReleaseRiskEvent consumedEvent)
    {
        ArgumentNullException.ThrowIfNull(consumedEvent);
        if (!ReferenceEquals(consumedEvent, _lastDeliveredEvent) ||
            consumedEvent.MessageKey != consumedEvent.Envelope.EventId ||
            !consumedEvent.Envelope.IsValidVersionOneContract())
        {
            throw new ArgumentException(
                "Only the last validated record returned by this consumer can be committed.",
                nameof(consumedEvent));
        }

        var nextOffset = checked(consumedEvent.Offset + 1);
        _consumer.Commit(
        [
            new TopicPartitionOffset(
                consumedEvent.Topic,
                new Partition(consumedEvent.Partition),
                new Offset(nextOffset))
        ]);
        _lastDeliveredEvent = null;
    }

    public void Dispose()
    {
        _consumer.Close();
        _consumer.Dispose();
    }

    private ConsumedReleaseRiskEvent Validate(
        ConsumeResult<string, byte[]> consumed)
    {
        if (!string.Equals(consumed.Topic, _topic, StringComparison.Ordinal))
        {
            throw new ReleaseRiskEventContractException(
                $"Kafka returned topic '{consumed.Topic}' while '{_topic}' was configured.");
        }

        if (!Guid.TryParseExact(consumed.Message.Key, "D", out var messageKey))
        {
            throw new ReleaseRiskEventContractException(
                "The Kafka message key must be a GUID in D format.");
        }

        var payload = consumed.Message.Value;
        if (payload is null)
        {
            throw new ReleaseRiskEventContractException(
                "The Kafka message payload must contain a V1 release risk envelope.");
        }

        ReleaseRiskOutboxEnvelope envelope;
        try
        {
            envelope = ReleaseRiskOutboxEnvelope.Deserialize(payload);
        }
        catch (Exception exception)
            when (exception is System.Text.Json.JsonException or
                  NotSupportedException)
        {
            throw new ReleaseRiskEventContractException(
                "The Kafka message payload is not a valid release risk envelope.",
                exception);
        }

        if (!envelope.IsValidVersionOneContract())
        {
            throw new ReleaseRiskEventContractException(
                "The Kafka message payload is not the consistent V1 releaseguard.release-risk-assessed contract.");
        }

        if (messageKey != envelope.EventId)
        {
            throw new ReleaseRiskEventContractException(
                "The Kafka message key must match the envelope eventId.");
        }

        return new ConsumedReleaseRiskEvent(
            consumed.Topic,
            consumed.Partition.Value,
            consumed.Offset.Value,
            messageKey,
            payload.ToArray(),
            envelope);
    }
}
