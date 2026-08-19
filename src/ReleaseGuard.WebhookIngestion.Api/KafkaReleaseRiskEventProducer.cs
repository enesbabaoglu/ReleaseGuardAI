using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace ReleaseGuard.WebhookIngestion.Api;

public interface IReleaseRiskEventProducer
{
    Task PublishAsync(
        ReleaseRiskOutboxEnvelope envelope,
        CancellationToken cancellationToken);
}

public sealed class KafkaReleaseRiskEventProducer :
    IReleaseRiskEventProducer,
    IDisposable
{
    private readonly IProducer<string, byte[]> _producer;
    private readonly string _topic;

    public KafkaReleaseRiskEventProducer(IOptions<KafkaProducerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var value = options.Value;
        KafkaProducerOptions.ThrowIfInvalid(value);
        _topic = value.Topic;
        _producer = new ProducerBuilder<string, byte[]>(new ProducerConfig
        {
            BootstrapServers = value.BootstrapServers,
            ClientId = value.ClientId,
            Acks = Acks.All,
            AllowAutoCreateTopics = false,
            EnableDeliveryReports = true,
            EnableIdempotence = true,
            MessageTimeoutMs = value.DeliveryTimeoutMilliseconds,
            RequestTimeoutMs = value.RequestTimeoutMilliseconds,
            MessageSendMaxRetries = value.MaximumRetries
        }).Build();
    }

    public async Task PublishAsync(
        ReleaseRiskOutboxEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();
        if (!envelope.IsValidVersionOneContract())
        {
            throw new ArgumentException(
                "Only a consistent V1 releaseguard.release-risk-assessed envelope can be published.",
                nameof(envelope));
        }

        var deliveryReport = await _producer.ProduceAsync(
            _topic,
            new Message<string, byte[]>
            {
                Key = envelope.EventId.ToString("D"),
                Value = envelope.SerializeToUtf8Bytes()
            },
            cancellationToken);

        if (deliveryReport.Status != PersistenceStatus.Persisted)
        {
            throw new InvalidOperationException(
                $"Kafka did not acknowledge event '{envelope.EventId}' as persisted; reported status was '{deliveryReport.Status}'.");
        }
    }

    public void Dispose() => _producer.Dispose();
}
