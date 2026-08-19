using Microsoft.Extensions.Options;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class OutboxDispatcherOptionsTests
{
    [Fact]
    public void Defaults_AreValidAndDispatcherIsExplicitlyDisabled()
    {
        var options = new OutboxDispatcherOptions();

        Assert.True(OutboxDispatcherOptions.IsValid(options));
        Assert.False(options.Enabled);
    }

    [Theory]
    [InlineData(0, 1_000, 30_000, 1_000, 60_000, 5_000)]
    [InlineData(101, 1_000, 30_000, 1_000, 60_000, 5_000)]
    [InlineData(10, 99, 30_000, 1_000, 60_000, 5_000)]
    [InlineData(10, 1_000, 4_999, 1_000, 60_000, 1_000)]
    [InlineData(10, 1_000, 30_000, 60_001, 60_000, 5_000)]
    [InlineData(10, 1_000, 5_000, 1_000, 60_000, 5_000)]
    public void IsValid_RejectsUnsafeLifecycleBounds(
        int batchSize,
        int pollIntervalMilliseconds,
        int leaseDurationMilliseconds,
        int initialRetryDelayMilliseconds,
        int maximumRetryDelayMilliseconds,
        int stateUpdateTimeoutMilliseconds)
    {
        var options = new OutboxDispatcherOptions
        {
            BatchSize = batchSize,
            PollIntervalMilliseconds = pollIntervalMilliseconds,
            LeaseDurationMilliseconds = leaseDurationMilliseconds,
            InitialRetryDelayMilliseconds = initialRetryDelayMilliseconds,
            MaximumRetryDelayMilliseconds = maximumRetryDelayMilliseconds,
            StateUpdateTimeoutMilliseconds = stateUpdateTimeoutMilliseconds
        };

        Assert.False(OutboxDispatcherOptions.IsValid(options));
    }

    [Theory]
    [InlineData(1, 1_000)]
    [InlineData(2, 2_000)]
    [InlineData(3, 4_000)]
    [InlineData(4, 5_000)]
    [InlineData(30, 5_000)]
    public void CalculateRetryDelay_UsesCappedExponentialBackoff(
        int attemptCount,
        int expectedMilliseconds)
    {
        var options = new OutboxDispatcherOptions
        {
            InitialRetryDelayMilliseconds = 1_000,
            MaximumRetryDelayMilliseconds = 5_000
        };

        var delay = OutboxDispatcherOptions.CalculateRetryDelay(
            options,
            attemptCount);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), delay);
    }

    [Fact]
    public void Validator_RejectsEnabledLeaseThatCannotCoverPublishAndStateUpdate()
    {
        var validator = new OutboxDispatcherOptionsValidator(
            Options.Create(new KafkaProducerOptions
            {
                BootstrapServers = "localhost:9092",
                Topic = "releaseguard.release-risk-assessed",
                DeliveryTimeoutMilliseconds = 10_000
            }));
        var options = new OutboxDispatcherOptions
        {
            Enabled = true,
            LeaseDurationMilliseconds = 15_000,
            StateUpdateTimeoutMilliseconds = 5_000
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            "must exceed",
            result.FailureMessage,
            StringComparison.Ordinal);
    }
}
