using Microsoft.Extensions.Options;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class AiExplanationQueryRateLimitOptionsTests
{
    [Fact]
    public void Defaults_AreBoundedAndValid()
    {
        var options = new AiExplanationQueryRateLimitOptions();

        AiExplanationQueryRateLimitOptions.ThrowIfInvalid(options);
        Assert.Equal(60, options.PermitLimit);
        Assert.Equal(60_000, options.WindowMilliseconds);
    }

    [Theory]
    [InlineData(
        AiExplanationQueryRateLimitOptions.MinimumPermitLimit,
        AiExplanationQueryRateLimitOptions.MinimumWindowMilliseconds)]
    [InlineData(60, 60_000)]
    [InlineData(
        AiExplanationQueryRateLimitOptions.MaximumPermitLimit,
        AiExplanationQueryRateLimitOptions.MaximumWindowMilliseconds)]
    public void Validator_AcceptsBoundedBudget(
        int permitLimit,
        int windowMilliseconds)
    {
        var options = new AiExplanationQueryRateLimitOptions
        {
            PermitLimit = permitLimit,
            WindowMilliseconds = windowMilliseconds
        };

        var result = new AiExplanationQueryRateLimitOptionsValidator()
            .Validate(null, options);

        Assert.True(result.Succeeded);
        AiExplanationQueryRateLimitOptions.ThrowIfInvalid(options);
    }

    [Theory]
    [InlineData(
        AiExplanationQueryRateLimitOptions.MinimumPermitLimit - 1,
        60_000)]
    [InlineData(
        AiExplanationQueryRateLimitOptions.MaximumPermitLimit + 1,
        60_000)]
    [InlineData(
        60,
        AiExplanationQueryRateLimitOptions.MinimumWindowMilliseconds - 1)]
    [InlineData(
        60,
        AiExplanationQueryRateLimitOptions.MaximumWindowMilliseconds + 1)]
    public void Validator_RejectsUnboundedBudget(
        int permitLimit,
        int windowMilliseconds)
    {
        var options = new AiExplanationQueryRateLimitOptions
        {
            PermitLimit = permitLimit,
            WindowMilliseconds = windowMilliseconds
        };

        var result = new AiExplanationQueryRateLimitOptionsValidator()
            .Validate(null, options);

        Assert.True(result.Failed);
        Assert.Throws<OptionsValidationException>(
            () => AiExplanationQueryRateLimitOptions.ThrowIfInvalid(options));
    }

    [Theory]
    [InlineData(0, 60_000)]
    [InlineData(10_001, 60_000)]
    [InlineData(60, 99)]
    [InlineData(60, 3_600_001)]
    public void ApplicationStartup_FailsFastForInvalidBudget(
        int permitLimit,
        int windowMilliseconds)
    {
        using var application = new TestApplicationFactory(
            TestApplicationFactory.AiExplanationQueryCredential,
            rateLimitPermitLimit: permitLimit,
            rateLimitWindowMilliseconds: windowMilliseconds);

        var exception = Assert.Throws<OptionsValidationException>(
            () => application.CreateClient());

        Assert.Contains(
            AiExplanationQueryRateLimitOptions.SectionName,
            exception.Message,
            StringComparison.Ordinal);
    }
}
