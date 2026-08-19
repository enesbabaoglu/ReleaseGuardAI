using Microsoft.Extensions.Options;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class AiExplanationProcessorOptionsTests
{
    [Fact]
    public void Defaults_AreValidAndDisabled()
    {
        var options = new AiExplanationProcessorOptions();

        Assert.False(options.Enabled);
        Assert.True(AiExplanationProcessorOptions.IsValid(options));
    }

    [Theory]
    [InlineData(0, 1_000, 30_000, 1_000, 60_000, 5_000)]
    [InlineData(10, 99, 30_000, 1_000, 60_000, 5_000)]
    [InlineData(10, 1_000, 999, 1_000, 60_000, 100)]
    [InlineData(10, 1_000, 30_000, 99, 60_000, 5_000)]
    [InlineData(10, 1_000, 30_000, 60_001, 60_000, 5_000)]
    [InlineData(10, 1_000, 30_000, 1_000, 60_000, 30_000)]
    public void InvalidBounds_AreRejected(
        int batchSize,
        int pollInterval,
        int leaseDuration,
        int initialRetryDelay,
        int maximumRetryDelay,
        int stateUpdateTimeout)
    {
        var options = new AiExplanationProcessorOptions
        {
            BatchSize = batchSize,
            PollIntervalMilliseconds = pollInterval,
            LeaseDurationMilliseconds = leaseDuration,
            InitialRetryDelayMilliseconds = initialRetryDelay,
            MaximumRetryDelayMilliseconds = maximumRetryDelay,
            StateUpdateTimeoutMilliseconds = stateUpdateTimeout
        };

        Assert.False(AiExplanationProcessorOptions.IsValid(options));
    }

    [Theory]
    [InlineData(1, 1_000)]
    [InlineData(2, 2_000)]
    [InlineData(3, 4_000)]
    [InlineData(10, 60_000)]
    [InlineData(1_000, 60_000)]
    public void RetryDelay_IsExponentialAndCapped(
        int attemptCount,
        int expectedMilliseconds)
    {
        var delay = AiExplanationProcessorOptions.CalculateRetryDelay(
            new AiExplanationProcessorOptions(),
            attemptCount);

        Assert.Equal(
            TimeSpan.FromMilliseconds(expectedMilliseconds),
            delay);
    }

    [Fact]
    public void EnabledProcessor_RequiresLeaseLongerThanClientAndStateUpdate()
    {
        var validator = new AiExplanationProcessorOptionsValidator(
            Options.Create(new AiExplanationClientOptions
            {
                BaseUrl = "http://127.0.0.1:8090",
                RequestTimeoutMilliseconds = 5_000
            }));
        var options = new AiExplanationProcessorOptions
        {
            Enabled = true,
            LeaseDurationMilliseconds = 10_000,
            StateUpdateTimeoutMilliseconds = 5_000
        };

        var result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(
            "must exceed AiExplanationClient:RequestTimeoutMilliseconds",
            Assert.Single(result.Failures ?? []));
    }
}
