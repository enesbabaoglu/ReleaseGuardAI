using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class GitHubWebhookEndpointTests : IClassFixture<TestApplicationFactory>
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes(
        """{"action":"opened","repository":{"full_name":"acme/ReleaseGuard"}}""");

    private readonly HttpClient _client;

    public GitHubWebhookEndpointTests(TestApplicationFactory application)
    {
        _client = application.CreateClient();
    }

    [Fact]
    public async Task PostWebhook_WithValidSignature_ReturnsAccepted()
    {
        using var request = CreateRequest(CreateSignature(Payload));

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task PostWebhook_WithInvalidSignature_ReturnsUnauthorized()
    {
        using var request = CreateRequest($"sha256={new string('0', 64)}");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostWebhook_WithoutSignature_ReturnsUnauthorized()
    {
        using var request = CreateRequest();

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("sha1=0123456789abcdef")]
    [InlineData("sha256=not-a-hex-digest")]
    public async Task PostWebhook_WithMalformedSignature_ReturnsBadRequest(string signature)
    {
        using var request = CreateRequest(signature);

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static HttpRequestMessage CreateRequest(string? signature = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, GitHubWebhookEndpoint.Route)
        {
            Content = new ByteArrayContent(Payload)
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        if (signature is not null)
        {
            request.Headers.Add(GitHubWebhookSignatureValidator.SignatureHeaderName, signature);
        }

        return request;
    }

    private static string CreateSignature(byte[] requestBody)
    {
        var secret = Encoding.UTF8.GetBytes(TestApplicationFactory.GitHubWebhookSecret);
        var digest = HMACSHA256.HashData(secret, requestBody);

        return $"sha256={Convert.ToHexString(digest).ToLowerInvariant()}";
    }
}
