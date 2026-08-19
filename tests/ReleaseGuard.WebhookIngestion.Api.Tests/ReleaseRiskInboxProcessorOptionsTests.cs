using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class ReleaseRiskInboxProcessorOptionsTests
{
    [Fact]
    public void Defaults_AreValidAndProcessorIsExplicitlyDisabled()
    {
        var options = new ReleaseRiskInboxProcessorOptions();

        Assert.True(ReleaseRiskInboxProcessorOptions.IsValid(options));
        Assert.False(options.Enabled);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(30_001)]
    public void IsValid_RejectsUnboundedPersistenceTimeout(
        int persistenceTimeoutMilliseconds)
    {
        var options = new ReleaseRiskInboxProcessorOptions
        {
            PersistenceTimeoutMilliseconds = persistenceTimeoutMilliseconds
        };

        Assert.False(ReleaseRiskInboxProcessorOptions.IsValid(options));
    }
}
