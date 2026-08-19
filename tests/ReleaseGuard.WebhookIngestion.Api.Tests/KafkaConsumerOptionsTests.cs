using Microsoft.Extensions.Options;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class KafkaConsumerOptionsTests
{
    [Theory]
    [InlineData("", "releaseguard.release-risk-assessed", "releaseguard-tests", 5_000)]
    [InlineData("localhost", "releaseguard.release-risk-assessed", "releaseguard-tests", 5_000)]
    [InlineData("localhost:9092", "bad topic", "releaseguard-tests", 5_000)]
    [InlineData("localhost:9092", "releaseguard.release-risk-assessed", "", 5_000)]
    [InlineData("localhost:9092", "releaseguard.release-risk-assessed", "releaseguard-tests", 99)]
    [InlineData("localhost:9092", "releaseguard.release-risk-assessed", "releaseguard-tests", 60_001)]
    public void ThrowIfInvalid_RejectsMissingOrUnboundedConfiguration(
        string bootstrapServers,
        string topic,
        string groupId,
        int consumeTimeoutMilliseconds)
    {
        var options = CreateValidOptions(
            bootstrapServers,
            topic,
            groupId,
            consumeTimeoutMilliseconds);

        Assert.Throws<OptionsValidationException>(
            () => KafkaConsumerOptions.ThrowIfInvalid(
                options,
                CreateProducerOptions(topic)));
    }

    [Theory]
    [InlineData(" leading-space")]
    [InlineData("trailing-space ")]
    [InlineData("line\nbreak")]
    public void HasValidGroupId_RejectsWhitespaceBoundariesAndControlCharacters(
        string groupId)
    {
        var options = CreateValidOptions(groupId: groupId);

        Assert.False(KafkaConsumerOptions.HasValidGroupId(options));
    }

    [Fact]
    public void HasValidGroupId_RejectsValueBeyondUtf8Limit()
    {
        var options = CreateValidOptions(groupId: new string('\u00E7', 128));

        Assert.False(KafkaConsumerOptions.HasValidGroupId(options));
    }

    [Fact]
    public void Validator_RejectsTopicThatDiffersFromProducerTopic()
    {
        var validator = new KafkaConsumerOptionsValidator(
            Options.Create(CreateProducerOptions(
                "releaseguard.release-risk-assessed")));
        var options = CreateValidOptions(
            topic: "releaseguard.release-risk-assessed-other");

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("must exactly match", result.FailureMessage);
    }

    [Fact]
    public void ConsumerConstructor_WithMissingRequiredConfiguration_FailsBeforeClientCreation()
    {
        var options = Options.Create(new KafkaConsumerOptions());
        var producerOptions = Options.Create(CreateProducerOptions(
            "releaseguard.release-risk-assessed"));

        Assert.Throws<OptionsValidationException>(
            () => new KafkaReleaseRiskEventConsumer(
                options,
                producerOptions));
    }

    [Fact]
    public void ValidOptions_AreAccepted()
    {
        var options = CreateValidOptions();

        KafkaConsumerOptions.ThrowIfInvalid(
            options,
            CreateProducerOptions(options.Topic));
    }

    [Theory]
    [InlineData(999)]
    [InlineData(300_001)]
    public void ThrowIfInvalid_RejectsUnboundedBrokerRequestTimeout(
        int brokerRequestTimeoutMilliseconds)
    {
        var options = CreateValidOptions(
            brokerRequestTimeoutMilliseconds: brokerRequestTimeoutMilliseconds);

        Assert.Throws<OptionsValidationException>(
            () => KafkaConsumerOptions.ThrowIfInvalid(
                options,
                CreateProducerOptions(options.Topic)));
    }

    private static KafkaConsumerOptions CreateValidOptions(
        string bootstrapServers = "localhost:9092",
        string topic = "releaseguard.release-risk-assessed",
        string groupId = "releaseguard-release-risk-tests",
        int consumeTimeoutMilliseconds = 5_000,
        int brokerRequestTimeoutMilliseconds = 5_000) =>
        new()
        {
            BootstrapServers = bootstrapServers,
            Topic = topic,
            GroupId = groupId,
            ClientId = "releaseguard-consumer-tests",
            ConsumeTimeoutMilliseconds = consumeTimeoutMilliseconds,
            BrokerRequestTimeoutMilliseconds = brokerRequestTimeoutMilliseconds
        };

    private static KafkaProducerOptions CreateProducerOptions(string topic) =>
        new()
        {
            BootstrapServers = "localhost:9092",
            Topic = topic
        };
}
