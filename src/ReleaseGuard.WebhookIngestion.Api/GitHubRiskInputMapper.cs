using System.Text.Json;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed class GitHubRiskInputMapper
{
    public const string SupportedEventName = "pull_request";
    public const string OpenedAction = "opened";
    public const string SynchronizeAction = "synchronize";
    public const string ChangeOpenedKind = "change_opened";
    public const string ChangeUpdatedKind = "change_updated";

    public GitHubRiskInputMappingResult Map(VerifiedGitHubWebhook webhook)
    {
        ArgumentNullException.ThrowIfNull(webhook);

        if (!string.Equals(
                webhook.EventName,
                SupportedEventName,
                StringComparison.Ordinal))
        {
            return GitHubRiskInputMappingResult.Unsupported();
        }

        if (!TryGetRequiredString(webhook.Payload, "action", out var action))
        {
            return GitHubRiskInputMappingResult.Invalid();
        }

        var kind = action switch
        {
            OpenedAction => ChangeOpenedKind,
            SynchronizeAction => ChangeUpdatedKind,
            _ => null
        };

        if (kind is null)
        {
            return GitHubRiskInputMappingResult.Unsupported();
        }

        if (!TryGetRequiredObject(webhook.Payload, "repository", out var repository) ||
            !TryGetRequiredString(repository, "full_name", out var repositoryName) ||
            !TryGetPositiveInt64(webhook.Payload, "number", out var changeNumber) ||
            !TryGetRequiredObject(webhook.Payload, "pull_request", out var pullRequest) ||
            !TryGetRequiredString(pullRequest, "title", out var title) ||
            !TryGetRequiredObject(pullRequest, "user", out var user) ||
            !TryGetRequiredString(user, "login", out var author) ||
            !TryGetRequiredObject(pullRequest, "base", out var baseReference) ||
            !TryGetRequiredString(baseReference, "ref", out var baseBranch) ||
            !TryGetRequiredObject(pullRequest, "head", out var headReference) ||
            !TryGetRequiredString(headReference, "ref", out var headBranch) ||
            !TryGetBoolean(pullRequest, "draft", out var isDraft) ||
            !TryGetNonNegativeInt32(pullRequest, "changed_files", out var changedFiles) ||
            !TryGetNonNegativeInt32(pullRequest, "additions", out var additions) ||
            !TryGetNonNegativeInt32(pullRequest, "deletions", out var deletions))
        {
            return GitHubRiskInputMappingResult.Invalid();
        }

        var riskInput = new ReleaseRiskInput(
            webhook.DeliveryId,
            SourceProvider: "github",
            Kind: kind,
            repositoryName,
            changeNumber,
            title,
            author,
            baseBranch,
            headBranch,
            isDraft,
            changedFiles,
            additions,
            deletions);

        return GitHubRiskInputMappingResult.Mapped(riskInput);
    }

    private static bool TryGetRequiredObject(
        JsonElement parent,
        string propertyName,
        out JsonElement value)
    {
        return parent.TryGetProperty(propertyName, out value) &&
               value.ValueKind == JsonValueKind.Object;
    }

    private static bool TryGetRequiredString(
        JsonElement parent,
        string propertyName,
        out string value)
    {
        value = string.Empty;

        if (!parent.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = property.GetString();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        value = candidate;
        return true;
    }

    private static bool TryGetPositiveInt64(
        JsonElement parent,
        string propertyName,
        out long value)
    {
        value = 0;

        return parent.TryGetProperty(propertyName, out var property) &&
               property.TryGetInt64(out value) &&
               value > 0;
    }

    private static bool TryGetNonNegativeInt32(
        JsonElement parent,
        string propertyName,
        out int value)
    {
        value = 0;

        return parent.TryGetProperty(propertyName, out var property) &&
               property.TryGetInt32(out value) &&
               value >= 0;
    }

    private static bool TryGetBoolean(
        JsonElement parent,
        string propertyName,
        out bool value)
    {
        value = false;

        if (!parent.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }
}

public sealed record GitHubRiskInputMappingResult(
    GitHubRiskInputMappingStatus Status,
    ReleaseRiskInput? RiskInput)
{
    public static GitHubRiskInputMappingResult Mapped(ReleaseRiskInput riskInput) =>
        new(GitHubRiskInputMappingStatus.Mapped, riskInput);

    public static GitHubRiskInputMappingResult Unsupported() =>
        new(GitHubRiskInputMappingStatus.Unsupported, null);

    public static GitHubRiskInputMappingResult Invalid() =>
        new(GitHubRiskInputMappingStatus.Invalid, null);
}

public enum GitHubRiskInputMappingStatus
{
    Mapped,
    Unsupported,
    Invalid
}
