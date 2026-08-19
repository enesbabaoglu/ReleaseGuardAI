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

        const string expectedJson =
            "{\"eventId\":\"0b989ba4-242f-11e5-81e1-c7b6966d2516\"," +
            "\"eventType\":\"releaseguard.release-risk-assessed\"," +
            "\"schemaVersion\":1," +
            "\"sourceProvider\":\"github\"," +
            "\"kind\":\"change_opened\"," +
            "\"riskInput\":{" +
            "\"sourceDeliveryId\":\"0b989ba4-242f-11e5-81e1-c7b6966d2516\"," +
            "\"sourceProvider\":\"github\"," +
            "\"kind\":\"change_opened\"," +
            "\"repository\":\"acme/ReleaseGuard\"," +
            "\"changeNumber\":42," +
            "\"title\":\"Protect production releases\"," +
            "\"author\":\"octocat\"," +
            "\"baseBranch\":\"main\"," +
            "\"headBranch\":\"feature/release-guard\"," +
            "\"isDraft\":false," +
            "\"changedFiles\":4," +
            "\"additions\":120," +
            "\"deletions\":15}," +
            "\"riskAssessment\":{" +
            "\"score\":20," +
            "\"level\":\"low\"," +
            "\"factors\":[{" +
            "\"code\":\"primary_target_branch\"," +
            "\"points\":20," +
            "\"reason\":\"The change targets the conventional primary branch \\u0027main\\u0027.\"}]}}";

        Assert.Equal(expectedJson, envelope.Serialize());
        Assert.Equal(expectedJson, envelope.Serialize());
    }
}
