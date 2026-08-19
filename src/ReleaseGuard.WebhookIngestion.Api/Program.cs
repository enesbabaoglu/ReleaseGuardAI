using ReleaseGuard.WebhookIngestion.Api;
using Microsoft.Extensions.Options;
using Npgsql;

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

builder.Services
    .AddOptions<PostgreSqlOptions>()
    .BindConfiguration(PostgreSqlOptions.SectionName)
    .Validate(
        PostgreSqlOptions.HasValidConnectionString,
        $"{PostgreSqlOptions.SectionName}:ConnectionString must be a valid PostgreSQL connection string with Host and Database.")
    .ValidateOnStart();

builder.Services.AddSingleton<GitHubWebhookSignatureValidator>();
builder.Services.AddSingleton<GitHubRiskInputMapper>();
builder.Services.AddSingleton<ReleaseRiskEvaluator>();
builder.Services.AddSingleton<NpgsqlDataSource>(serviceProvider =>
{
    var options = serviceProvider
        .GetRequiredService<IOptions<PostgreSqlOptions>>()
        .Value;
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(options.ConnectionString);
    dataSourceBuilder.UseLoggerFactory(
        serviceProvider.GetRequiredService<ILoggerFactory>());
    return dataSourceBuilder.Build();
});
builder.Services.AddSingleton<
    IGitHubWebhookDeliveryStore,
    PostgreSqlGitHubWebhookDeliveryStore>();
builder.Services.AddHostedService<PostgreSqlSchemaInitializer>();

var app = builder.Build();

app.MapGet("/health", () => TypedResults.Ok(ServiceStatus.Ready()));
app.MapPost(GitHubWebhookEndpoint.Route, GitHubWebhookEndpoint.HandleAsync);

app.Run();

public partial class Program;
