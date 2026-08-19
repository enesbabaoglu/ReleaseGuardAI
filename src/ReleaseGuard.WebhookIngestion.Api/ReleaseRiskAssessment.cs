namespace ReleaseGuard.WebhookIngestion.Api;

public sealed record ReleaseRiskAssessment(
    int Score,
    string Level,
    IReadOnlyList<ReleaseRiskFactor> Factors);

public sealed record ReleaseRiskFactor(
    string Code,
    int Points,
    string Reason);

public static class ReleaseRiskPolicy
{
    public const int MinimumScore = 0;
    public const int MaximumScore = 100;
    public const int MediumRiskMinimumScore = 30;
    public const int HighRiskMinimumScore = 65;

    public const string LowLevel = "low";
    public const string MediumLevel = "medium";
    public const string HighLevel = "high";

    public static string ClassifyScore(int score)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(score, MinimumScore);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(score, MaximumScore);

        return score switch
        {
            >= HighRiskMinimumScore => HighLevel,
            >= MediumRiskMinimumScore => MediumLevel,
            _ => LowLevel
        };
    }
}
