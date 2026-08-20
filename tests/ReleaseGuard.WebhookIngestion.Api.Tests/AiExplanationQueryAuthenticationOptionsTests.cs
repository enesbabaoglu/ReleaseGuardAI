using Microsoft.Extensions.Options;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class AiExplanationQueryAuthenticationOptionsTests
{
    [Theory]
    [InlineData("abcdefghijklmnopqrstuvwxyzABCDEF")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789-._~+/")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789==")]
    public void Validator_AcceptsBoundedBearerTokenCredential(
        string credential)
    {
        var validator = new AiExplanationQueryAuthenticationOptionsValidator();

        var result = validator.Validate(
            null,
            new AiExplanationQueryAuthenticationOptions
            {
                Credential = credential
            });

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("abcdefghijklmnopqrstuvwxyz01234 6789")]
    [InlineData("abcdefghijklmnopqrstuvwxyz01234,6789")]
    [InlineData("abcdefghijklmnopqrstuvwxyz01234=6789")]
    [InlineData("================================")]
    public void Validator_RejectsMissingOrMalformedCredential(
        string credential)
    {
        var options = new AiExplanationQueryAuthenticationOptions
        {
            Credential = credential
        };
        var validator = new AiExplanationQueryAuthenticationOptionsValidator();

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Throws<OptionsValidationException>(
            () => AiExplanationQueryAuthenticationOptions.ThrowIfInvalid(
                options));
    }

    [Fact]
    public void Validator_RejectsCredentialAboveMaximumLength()
    {
        var options = new AiExplanationQueryAuthenticationOptions
        {
            Credential = new string(
                'a',
                AiExplanationQueryAuthenticationOptions
                    .MaximumCredentialLength + 1)
        };

        var result = new AiExplanationQueryAuthenticationOptionsValidator()
            .Validate(null, options);

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("too-short")]
    public void ApplicationStartup_FailsFastForMissingOrInvalidCredential(
        string? credential)
    {
        using var application = new TestApplicationFactory(credential);

        var exception = Assert.Throws<OptionsValidationException>(
            () => application.CreateClient());

        Assert.Contains(
            AiExplanationQueryAuthenticationOptions.SectionName,
            exception.Message,
            StringComparison.Ordinal);
    }
}
