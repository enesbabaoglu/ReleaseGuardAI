using System.Globalization;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed class ReleaseRiskEvaluator
{
    public const int WiderChangeMinimumFiles = 5;
    public const int BroadChangeMinimumFiles = 20;
    public const int WiderChangePoints = 15;
    public const int BroadChangePoints = 30;

    public const int ElevatedChurnMinimumLines = 200;
    public const int HighChurnMinimumLines = 1_000;
    public const int ElevatedChurnPoints = 20;
    public const int HighChurnPoints = 50;

    public const int PrimaryBranchPoints = 20;

    public ReleaseRiskAssessment Evaluate(ReleaseRiskInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var factors = new List<ReleaseRiskFactor>(capacity: 3);

        AddChangeBreadthFactor(input.ChangedFiles, factors);
        AddChangeChurnFactor((long)input.Additions + input.Deletions, factors);
        AddPrimaryBranchFactor(input.BaseBranch, factors);

        var score = factors.Sum(factor => factor.Points);

        return new ReleaseRiskAssessment(
            score,
            ReleaseRiskPolicy.ClassifyScore(score),
            factors.AsReadOnly());
    }

    private static void AddChangeBreadthFactor(
        int changedFiles,
        ICollection<ReleaseRiskFactor> factors)
    {
        if (changedFiles >= BroadChangeMinimumFiles)
        {
            factors.Add(new ReleaseRiskFactor(
                "broad_change",
                BroadChangePoints,
                FormattableString.Invariant(
                    $"{changedFiles} changed files meets the broad-change threshold of {BroadChangeMinimumFiles} files.")));
            return;
        }

        if (changedFiles >= WiderChangeMinimumFiles)
        {
            factors.Add(new ReleaseRiskFactor(
                "wider_change",
                WiderChangePoints,
                FormattableString.Invariant(
                    $"{changedFiles} changed files meets the wider-change threshold of {WiderChangeMinimumFiles} files.")));
        }
    }

    private static void AddChangeChurnFactor(
        long changedLines,
        ICollection<ReleaseRiskFactor> factors)
    {
        if (changedLines >= HighChurnMinimumLines)
        {
            factors.Add(new ReleaseRiskFactor(
                "high_change_churn",
                HighChurnPoints,
                FormattableString.Invariant(
                    $"{changedLines.ToString("N0", CultureInfo.InvariantCulture)} changed lines meets the high-churn threshold of {HighChurnMinimumLines:N0} lines.")));
            return;
        }

        if (changedLines >= ElevatedChurnMinimumLines)
        {
            factors.Add(new ReleaseRiskFactor(
                "elevated_change_churn",
                ElevatedChurnPoints,
                FormattableString.Invariant(
                    $"{changedLines.ToString("N0", CultureInfo.InvariantCulture)} changed lines meets the elevated-churn threshold of {ElevatedChurnMinimumLines:N0} lines.")));
        }
    }

    private static void AddPrimaryBranchFactor(
        string baseBranch,
        ICollection<ReleaseRiskFactor> factors)
    {
        if (!string.Equals(baseBranch, "main", StringComparison.Ordinal) &&
            !string.Equals(baseBranch, "master", StringComparison.Ordinal))
        {
            return;
        }

        factors.Add(new ReleaseRiskFactor(
            "primary_target_branch",
            PrimaryBranchPoints,
            FormattableString.Invariant(
                $"The change targets the conventional primary branch '{baseBranch}'.")));
    }
}
