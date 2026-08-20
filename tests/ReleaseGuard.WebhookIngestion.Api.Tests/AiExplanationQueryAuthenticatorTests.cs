using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class AiExplanationQueryAuthenticatorTests
{
    [Fact]
    public void IsAuthorized_AcceptsOnlyMatchingBearerCredential()
    {
        using var authenticator = CreateAuthenticator();

        Assert.True(authenticator.IsAuthorized(
            $"Bearer {TestApplicationFactory.AiExplanationQueryCredential}"));
        Assert.True(authenticator.IsAuthorized(
            $"bearer {TestApplicationFactory.AiExplanationQueryCredential}"));
        Assert.False(authenticator.IsAuthorized(
            $"Basic {TestApplicationFactory.AiExplanationQueryCredential}"));
        Assert.False(authenticator.IsAuthorized("Bearer wrong-credential"));
        Assert.False(authenticator.IsAuthorized(
            $"Bearer {TestApplicationFactory.PreviousAiExplanationQueryCredential}"));
    }

    [Fact]
    public void IsAuthorized_DuringRotationAcceptsActiveAndPreviousCredential()
    {
        using var authenticator = CreateAuthenticator(
            TestApplicationFactory.PreviousAiExplanationQueryCredential);

        Assert.True(authenticator.IsAuthorized(
            $"Bearer {TestApplicationFactory.AiExplanationQueryCredential}"));
        Assert.True(authenticator.IsAuthorized(
            $"Bearer {TestApplicationFactory.PreviousAiExplanationQueryCredential}"));
        Assert.False(authenticator.IsAuthorized("Bearer wrong-credential"));
    }

    [Fact]
    public void IsAuthorized_RejectsMissingMalformedAndDuplicateHeader()
    {
        using var authenticator = CreateAuthenticator();

        Assert.False(authenticator.IsAuthorized(StringValues.Empty));
        Assert.False(authenticator.IsAuthorized("Bearer"));
        Assert.False(authenticator.IsAuthorized("Bearer "));
        Assert.False(authenticator.IsAuthorized(
            $"Bearer  {TestApplicationFactory.AiExplanationQueryCredential}"));
        Assert.False(authenticator.IsAuthorized(
            new StringValues(
            [
                $"Bearer {TestApplicationFactory.AiExplanationQueryCredential}",
                $"Bearer {TestApplicationFactory.AiExplanationQueryCredential}"
            ])));
        Assert.False(authenticator.IsAuthorized(
            $"Bearer {TestApplicationFactory.AiExplanationQueryCredential}, Bearer {TestApplicationFactory.AiExplanationQueryCredential}"));
    }

    [Fact]
    public void Constructor_FailsFastForInvalidConfiguration()
    {
        var options = Options.Create(
            new AiExplanationQueryAuthenticationOptions());

        Assert.Throws<OptionsValidationException>(
            () => new AiExplanationQueryAuthenticator(options));
    }

    private static AiExplanationQueryAuthenticator CreateAuthenticator(
        string? previousCredential = null) =>
        new(Options.Create(
            new AiExplanationQueryAuthenticationOptions
            {
                ActiveCredential =
                    TestApplicationFactory.AiExplanationQueryCredential,
                PreviousCredential = previousCredential
            }));
}
