using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class ReleaseRiskEvaluatorTests
{
    private readonly ReleaseRiskEvaluator _evaluator = new();

    [Fact]
    public void Evaluate_WithSmallChange_ReturnsLowRiskWithoutFactors()
    {
        var input = CreateInput(
            changedFiles: 4,
            additions: 150,
            deletions: 49);

        var assessment = _evaluator.Evaluate(input);

        Assert.Equal(0, assessment.Score);
        Assert.Equal(ReleaseRiskPolicy.LowLevel, assessment.Level);
        Assert.Empty(assessment.Factors);
    }

    [Fact]
    public void Evaluate_WithBroadChange_ReturnsMediumRiskAtMinimumScore()
    {
        var input = CreateInput(
            changedFiles: ReleaseRiskEvaluator.BroadChangeMinimumFiles,
            additions: 0,
            deletions: 0);

        var assessment = _evaluator.Evaluate(input);

        var factor = Assert.Single(assessment.Factors);
        Assert.Equal(ReleaseRiskPolicy.MediumRiskMinimumScore, assessment.Score);
        Assert.Equal(ReleaseRiskPolicy.MediumLevel, assessment.Level);
        Assert.Equal("broad_change", factor.Code);
        Assert.Equal(ReleaseRiskEvaluator.BroadChangePoints, factor.Points);
    }

    [Fact]
    public void Evaluate_WithWiderHighChurnChange_ReturnsHighRiskAtMinimumScore()
    {
        var input = CreateInput(
            changedFiles: ReleaseRiskEvaluator.WiderChangeMinimumFiles,
            additions: ReleaseRiskEvaluator.HighChurnMinimumLines,
            deletions: 0);

        var assessment = _evaluator.Evaluate(input);

        Assert.Equal(ReleaseRiskPolicy.HighRiskMinimumScore, assessment.Score);
        Assert.Equal(ReleaseRiskPolicy.HighLevel, assessment.Level);
        Assert.Collection(
            assessment.Factors,
            factor => Assert.Equal("wider_change", factor.Code),
            factor => Assert.Equal("high_change_churn", factor.Code));
    }

    [Theory]
    [InlineData(4, null, 0)]
    [InlineData(5, "wider_change", ReleaseRiskEvaluator.WiderChangePoints)]
    [InlineData(19, "wider_change", ReleaseRiskEvaluator.WiderChangePoints)]
    [InlineData(20, "broad_change", ReleaseRiskEvaluator.BroadChangePoints)]
    public void Evaluate_AtChangedFileBoundaries_AppliesOnlyOneBreadthTier(
        int changedFiles,
        string? expectedCode,
        int expectedScore)
    {
        var assessment = _evaluator.Evaluate(CreateInput(changedFiles, 0, 0));

        Assert.Equal(expectedScore, assessment.Score);

        if (expectedCode is null)
        {
            Assert.Empty(assessment.Factors);
            return;
        }

        var factor = Assert.Single(assessment.Factors);
        Assert.Equal(expectedCode, factor.Code);
    }

    [Theory]
    [InlineData(199, null, 0)]
    [InlineData(200, "elevated_change_churn", ReleaseRiskEvaluator.ElevatedChurnPoints)]
    [InlineData(999, "elevated_change_churn", ReleaseRiskEvaluator.ElevatedChurnPoints)]
    [InlineData(1000, "high_change_churn", ReleaseRiskEvaluator.HighChurnPoints)]
    public void Evaluate_AtChangedLineBoundaries_AppliesOnlyOneChurnTier(
        int additions,
        string? expectedCode,
        int expectedScore)
    {
        var assessment = _evaluator.Evaluate(CreateInput(0, additions, 0));

        Assert.Equal(expectedScore, assessment.Score);

        if (expectedCode is null)
        {
            Assert.Empty(assessment.Factors);
            return;
        }

        var factor = Assert.Single(assessment.Factors);
        Assert.Equal(expectedCode, factor.Code);
    }

    [Theory]
    [InlineData(0, ReleaseRiskPolicy.LowLevel)]
    [InlineData(29, ReleaseRiskPolicy.LowLevel)]
    [InlineData(30, ReleaseRiskPolicy.MediumLevel)]
    [InlineData(64, ReleaseRiskPolicy.MediumLevel)]
    [InlineData(65, ReleaseRiskPolicy.HighLevel)]
    [InlineData(100, ReleaseRiskPolicy.HighLevel)]
    public void ClassifyScore_AtLevelBoundaries_ReturnsExpectedLevel(
        int score,
        string expectedLevel)
    {
        Assert.Equal(expectedLevel, ReleaseRiskPolicy.ClassifyScore(score));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void ClassifyScore_OutsideSupportedRange_Throws(int score)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ReleaseRiskPolicy.ClassifyScore(score));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("master")]
    public void Evaluate_WhenPrimaryBranchIsTargeted_AddsBranchFactor(string baseBranch)
    {
        var assessment = _evaluator.Evaluate(
            CreateInput(0, 0, 0, baseBranch));

        var factor = Assert.Single(assessment.Factors);
        Assert.Equal(ReleaseRiskEvaluator.PrimaryBranchPoints, assessment.Score);
        Assert.Equal("primary_target_branch", factor.Code);
        Assert.Contains(baseBranch, factor.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_WithMaximumLineCounts_DoesNotOverflowChurn()
    {
        var assessment = _evaluator.Evaluate(
            CreateInput(0, int.MaxValue, int.MaxValue));

        var factor = Assert.Single(assessment.Factors);
        Assert.Equal(ReleaseRiskEvaluator.HighChurnPoints, assessment.Score);
        Assert.Equal("high_change_churn", factor.Code);
    }

    [Fact]
    public void Evaluate_WithSameInput_ReturnsSameScoreLevelAndFactors()
    {
        var input = CreateInput(20, 800, 200, "main");

        var first = _evaluator.Evaluate(input);
        var second = _evaluator.Evaluate(input);

        Assert.Equal(ReleaseRiskPolicy.MaximumScore, first.Score);
        Assert.Equal(ReleaseRiskPolicy.HighLevel, first.Level);
        Assert.Equal(first.Score, second.Score);
        Assert.Equal(first.Level, second.Level);
        Assert.Equal(first.Factors, second.Factors);
        Assert.Equal(first.Score, first.Factors.Sum(factor => factor.Points));
    }

    private static ReleaseRiskInput CreateInput(
        int changedFiles,
        int additions,
        int deletions,
        string baseBranch = "develop") =>
        new(
            Guid.Parse("18d0a74d-026b-4aaa-bcc4-393eab10bf56"),
            SourceProvider: "github",
            Kind: "change_opened",
            Repository: "acme/ReleaseGuard",
            ChangeNumber: 42,
            Title: "A change",
            Author: "octocat",
            BaseBranch: baseBranch,
            HeadBranch: "feature/a-change",
            IsDraft: false,
            ChangedFiles: changedFiles,
            Additions: additions,
            Deletions: deletions);
}
