using ReleaseGuard.WebhookIngestion.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<GitHubWebhookOptions>()
    .BindConfiguration(GitHubWebhookOptions.SectionName)
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.Secret),
        $"{GitHubWebhookOptions.SectionName}:Secret must be configured.")
    .Validate(
        options => options.Secret is { Length: >= GitHubWebhookOptions.MinimumSecretLength },
        $"{GitHubWebhookOptions.SectionName}:Secret must contain at least {GitHubWebhookOptions.MinimumSecretLength} characters.")
    .ValidateOnStart();

builder.Services.AddSingleton<GitHubWebhookSignatureValidator>();
builder.Services.AddSingleton<GitHubRiskInputMapper>();
builder.Services.AddSingleton<ReleaseRiskEvaluator>();
builder.Services.AddSingleton<
    IGitHubWebhookDeliveryRegistry,
    InMemoryGitHubWebhookDeliveryRegistry>();

var app = builder.Build();

app.MapGet("/health", () => TypedResults.Ok(ServiceStatus.Ready()));
app.MapPost(GitHubWebhookEndpoint.Route, GitHubWebhookEndpoint.HandleAsync);

app.Run();

public partial class Program;
