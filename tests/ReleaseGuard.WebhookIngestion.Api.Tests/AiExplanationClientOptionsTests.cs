using Microsoft.Extensions.Options;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class AiExplanationClientOptionsTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8090")]
    [InlineData("https://ai.internal.example/releaseguard")]
    public void ThrowIfInvalid_AcceptsSupportedBaseUrl(string baseUrl)
    {
        var options = new AiExplanationClientOptions
        {
            BaseUrl = baseUrl,
            RequestTimeoutMilliseconds = 5_000
        };

        AiExplanationClientOptions.ThrowIfInvalid(options);
    }

    [Theory]
    [InlineData(99)]
    [InlineData(60_001)]
    public void ThrowIfInvalid_RejectsUnboundedTimeout(int timeoutMilliseconds)
    {
        var options = new AiExplanationClientOptions
        {
            BaseUrl = "http://127.0.0.1:8090",
            RequestTimeoutMilliseconds = timeoutMilliseconds
        };

        Assert.Throws<OptionsValidationException>(
            () => AiExplanationClientOptions.ThrowIfInvalid(options));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData("https://example.com ")]
    [InlineData("ftp://127.0.0.1:8090")]
    [InlineData("https://user:secret@example.com")]
    [InlineData("https://example.com?model=secret")]
    [InlineData("https://example.com#fragment")]
    public void ThrowIfInvalid_RejectsUnsafeBaseUrl(string baseUrl)
    {
        var options = new AiExplanationClientOptions
        {
            BaseUrl = baseUrl,
            RequestTimeoutMilliseconds = 5_000
        };

        Assert.Throws<OptionsValidationException>(
            () => AiExplanationClientOptions.ThrowIfInvalid(options));
    }
}
