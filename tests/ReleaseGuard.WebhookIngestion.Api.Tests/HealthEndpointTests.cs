using System.Net;
using System.Net.Http.Json;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class HealthEndpointTests : IClassFixture<TestApplicationFactory>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(TestApplicationFactory application)
    {
        _client = application.CreateClient();
    }

    [Fact]
    public async Task GetHealth_WhenApplicationIsRunning_ReturnsServiceStatus()
    {
        using var response = await _client.GetAsync("/health");
        var status = await response.Content.ReadFromJsonAsync<ServiceStatus>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(status);
        Assert.Equal("ok", status.Status);
        Assert.Equal("webhook-ingestion", status.Service);
    }
}
