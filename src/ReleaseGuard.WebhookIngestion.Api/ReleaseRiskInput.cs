namespace ReleaseGuard.WebhookIngestion.Api;

public sealed record ReleaseRiskInput(
    Guid SourceDeliveryId,
    string SourceProvider,
    string Kind,
    string Repository,
    long ChangeNumber,
    string Title,
    string Author,
    string BaseBranch,
    string HeadBranch,
    bool IsDraft,
    int ChangedFiles,
    int Additions,
    int Deletions);
