using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class AiExplanationQueryOptionsTests
{
    [Theory]
    [InlineData(AiExplanationQueryOptions.MinimumReadTimeoutMilliseconds)]
    [InlineData(5_000)]
    [InlineData(AiExplanationQueryOptions.MaximumReadTimeoutMilliseconds)]
    public void ThrowIfInvalid_AcceptsBoundedReadTimeout(
        int readTimeoutMilliseconds)
    {
        var options = new AiExplanationQueryOptions
        {
            ReadTimeoutMilliseconds = readTimeoutMilliseconds
        };

        AiExplanationQueryOptions.ThrowIfInvalid(options);
    }

    [Theory]
    [InlineData(AiExplanationQueryOptions.MinimumReadTimeoutMilliseconds - 1)]
    [InlineData(AiExplanationQueryOptions.MaximumReadTimeoutMilliseconds + 1)]
    public void ThrowIfInvalid_RejectsUnboundedReadTimeout(
        int readTimeoutMilliseconds)
    {
        var options = new AiExplanationQueryOptions
        {
            ReadTimeoutMilliseconds = readTimeoutMilliseconds
        };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => AiExplanationQueryOptions.ThrowIfInvalid(options));
    }
}
