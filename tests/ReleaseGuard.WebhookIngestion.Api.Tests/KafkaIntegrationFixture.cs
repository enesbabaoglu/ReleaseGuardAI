using System.Net;
using System.Net.Sockets;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

[CollectionDefinition(CollectionName)]
public sealed class KafkaIntegrationCollection :
    ICollectionFixture<KafkaIntegrationFixture>
{
    public const string CollectionName = "Kafka integration";
}

public sealed class KafkaIntegrationFixture : IAsyncLifetime
{
    private const string RedpandaImage =
        "docker.redpanda.com/redpandadata/redpanda:v26.1.14";

    private readonly int _kafkaPort = FindAvailableTcpPort();
    private readonly IContainer _container;

    public KafkaIntegrationFixture()
    {
        _container = new ContainerBuilder(RedpandaImage)
            .WithPortBinding(_kafkaPort, _kafkaPort)
            .WithCommand(
                "redpanda",
                "start",
                $"--kafka-addr=0.0.0.0:{_kafkaPort}",
                $"--advertise-kafka-addr=127.0.0.1:{_kafkaPort}",
                "--rpc-addr=0.0.0.0:33145",
                "--advertise-rpc-addr=127.0.0.1:33145",
                "--mode=dev-container",
                "--smp=1",
                "--default-log-level=warn")
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilInternalTcpPortIsAvailable(_kafkaPort))
            .Build();
    }

    public string BootstrapServers => $"127.0.0.1:{_kafkaPort}";

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<string> CreateTopicAsync()
    {
        var topic = $"releaseguard.release-risk-assessed-{Guid.NewGuid():N}";
        using var adminClient = new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = BootstrapServers,
            ClientId = "releaseguard-kafka-integration-admin"
        }).Build();

        await adminClient.CreateTopicsAsync(
        [
            new TopicSpecification
            {
                Name = topic,
                NumPartitions = 1,
                ReplicationFactor = 1
            }
        ]);

        return topic;
    }

    public static int FindAvailableTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
