using System.Text;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class ReleaseRiskOutboxEnvelopeTests
{
    [Fact]
    public void Serialize_ProducesStableVersionOneContract()
    {
        var deliveryId = Guid.Parse("0b989ba4-242f-11e5-81e1-c7b6966d2516");
        var riskInput = new ReleaseRiskInput(
            deliveryId,
            "github",
            GitHubRiskInputMapper.ChangeOpenedKind,
            "acme/ReleaseGuard",
            42,
            "Protect production releases",
            "octocat",
            "main",
            "feature/release-guard",
            false,
            4,
            120,
            15);
        var riskAssessment = new ReleaseRiskAssessment(
            20,
            ReleaseRiskPolicy.LowLevel,
            [
                new ReleaseRiskFactor(
                    "primary_target_branch",
                    20,
                    "The change targets the conventional primary branch 'main'.")
            ]);

        var envelope = ReleaseRiskOutboxEnvelope.Create(
            deliveryId,
            riskInput,
            riskAssessment);

        var expectedJson = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "contracts",
                "release-risk-assessed.v1.example.json"))
            .TrimEnd('\r', '\n');

        Assert.Equal(expectedJson, envelope.Serialize());
        Assert.Equal(expectedJson, envelope.Serialize());
        Assert.Equal(
            Encoding.UTF8.GetBytes(expectedJson),
            envelope.SerializeToUtf8Bytes());
    }
}
