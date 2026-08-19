using Microsoft.Extensions.Options;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class KafkaProducerOptionsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("localhost:0")]
    [InlineData("localhost:65536")]
    [InlineData("localhost:9092,")]
    [InlineData("user:password@localhost:9092")]
    public void HasValidBootstrapServers_RejectsMissingOrMalformedEndpoints(
        string bootstrapServers)
    {
        var options = CreateValidOptions(bootstrapServers: bootstrapServers);

        Assert.False(KafkaProducerOptions.HasValidBootstrapServers(options));
    }

    [Theory]
    [InlineData("localhost:9092")]
    [InlineData("broker-one:9092,broker-two:9093")]
    [InlineData("[::1]:9092")]
    public void HasValidBootstrapServers_AcceptsExplicitHostAndPortEndpoints(
        string bootstrapServers)
    {
        var options = CreateValidOptions(bootstrapServers: bootstrapServers);

        Assert.True(KafkaProducerOptions.HasValidBootstrapServers(options));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("releaseguard risk")]
    [InlineData("releaseguard/risk")]
    public void HasValidTopic_RejectsInvalidNames(string topic)
    {
        var options = CreateValidOptions(topic: topic);

        Assert.False(KafkaProducerOptions.HasValidTopic(options));
    }

    [Fact]
    public void HasValidTopic_RejectsNamesLongerThanKafkaLimit()
    {
        var options = CreateValidOptions(topic: new string('a', 250));

        Assert.False(KafkaProducerOptions.HasValidTopic(options));
    }

    [Fact]
    public void ThrowIfInvalid_ReportsAllMissingRequiredConfiguration()
    {
        var options = new KafkaProducerOptions();

        var exception = Assert.Throws<OptionsValidationException>(
            () => KafkaProducerOptions.ThrowIfInvalid(options));

        Assert.Contains(
            $"{KafkaProducerOptions.SectionName}:BootstrapServers",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            $"{KafkaProducerOptions.SectionName}:Topic",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProducerConstructor_WithMissingRequiredConfiguration_FailsBeforeClientCreation()
    {
        var options = Options.Create(new KafkaProducerOptions());

        Assert.Throws<OptionsValidationException>(
            () => new KafkaReleaseRiskEventProducer(options));
    }

    [Theory]
    [InlineData(999, 999, 1)]
    [InlineData(10_000, 10_001, 1)]
    [InlineData(300_001, 5_000, 1)]
    [InlineData(10_000, 5_000, 0)]
    [InlineData(10_000, 5_000, 101)]
    public void ThrowIfInvalid_RejectsUnboundedOrInconsistentProducerSettings(
        int deliveryTimeoutMilliseconds,
        int requestTimeoutMilliseconds,
        int maximumRetries)
    {
        var options = new KafkaProducerOptions
        {
            BootstrapServers = "localhost:9092",
            Topic = "releaseguard.release-risk-assessed",
            DeliveryTimeoutMilliseconds = deliveryTimeoutMilliseconds,
            RequestTimeoutMilliseconds = requestTimeoutMilliseconds,
            MaximumRetries = maximumRetries
        };

        Assert.Throws<OptionsValidationException>(
            () => KafkaProducerOptions.ThrowIfInvalid(options));
    }

    private static KafkaProducerOptions CreateValidOptions(
        string bootstrapServers = "localhost:9092",
        string topic = "releaseguard.release-risk-assessed") =>
        new()
        {
            BootstrapServers = bootstrapServers,
            Topic = topic
        };
}
