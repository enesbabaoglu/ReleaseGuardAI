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
                ActiveCredential = credential
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
            ActiveCredential = credential
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
            ActiveCredential = new string(
                'a',
                AiExplanationQueryAuthenticationOptions
                    .MaximumCredentialLength + 1)
        };

        var result = new AiExplanationQueryAuthenticationOptionsValidator()
            .Validate(null, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validator_AcceptsDistinctValidRotationCredentials()
    {
        var options = CreateValidOptions();
        options.PreviousCredential =
            TestApplicationFactory.PreviousAiExplanationQueryCredential;

        var result = new AiExplanationQueryAuthenticationOptionsValidator()
            .Validate(null, options);

        Assert.True(result.Succeeded);
        AiExplanationQueryAuthenticationOptions.ThrowIfInvalid(options);
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("abcdefghijklmnopqrstuvwxyz01234 6789")]
    public void Validator_RejectsMalformedPreviousCredential(
        string previousCredential)
    {
        var options = CreateValidOptions();
        options.PreviousCredential = previousCredential;

        var result = new AiExplanationQueryAuthenticationOptionsValidator()
            .Validate(null, options);

        Assert.True(result.Failed);
        Assert.Throws<OptionsValidationException>(
            () => AiExplanationQueryAuthenticationOptions.ThrowIfInvalid(
                options));
    }

    [Fact]
    public void Validator_RejectsMatchingActiveAndPreviousCredentials()
    {
        var options = CreateValidOptions();
        options.PreviousCredential = options.ActiveCredential;

        var result = new AiExplanationQueryAuthenticationOptionsValidator()
            .Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains(
                "must differ",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsPreviousCredentialAboveMaximumLength()
    {
        var options = CreateValidOptions();
        options.PreviousCredential = new string(
            'b',
            AiExplanationQueryAuthenticationOptions
                .MaximumCredentialLength + 1);

        var result = new AiExplanationQueryAuthenticationOptionsValidator()
            .Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains(
                "PreviousCredential",
                StringComparison.Ordinal));
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

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    public void ApplicationStartup_FailsFastForInvalidPreviousCredential(
        string previousCredential)
    {
        using var application = new TestApplicationFactory(
            TestApplicationFactory.AiExplanationQueryCredential,
            previousCredential);

        var exception = Assert.Throws<OptionsValidationException>(
            () => application.CreateClient());

        Assert.Contains(
            "PreviousCredential",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationStartup_FailsFastForMatchingRotationCredentials()
    {
        using var application = new TestApplicationFactory(
            TestApplicationFactory.AiExplanationQueryCredential,
            TestApplicationFactory.AiExplanationQueryCredential);

        var exception = Assert.Throws<OptionsValidationException>(
            () => application.CreateClient());

        Assert.Contains("must differ", exception.Message, StringComparison.Ordinal);
    }

    private static AiExplanationQueryAuthenticationOptions CreateValidOptions() =>
        new()
        {
            ActiveCredential =
                TestApplicationFactory.AiExplanationQueryCredential
        };
}
