using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class TestApplicationFactory : WebApplicationFactory<Program>
{
    public const string GitHubWebhookSecret = "releaseguard-checkpoint-2-test-secret";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(configuration =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{GitHubWebhookOptions.SectionName}:Secret"] = GitHubWebhookSecret
            });
        });
    }
}
