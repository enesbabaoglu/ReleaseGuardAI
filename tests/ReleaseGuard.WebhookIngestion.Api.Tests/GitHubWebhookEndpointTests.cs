using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
    public async Task PostWebhook_WithValidContract_ReturnsAcceptedReceipt()
    {
        var deliveryId = Guid.NewGuid();
        using var request = CreateRequest(
            Payload,
            CreateSignature(Payload),
            deliveryId: deliveryId.ToString());

        using var response = await _client.SendAsync(request);
        var receipt = await response.Content.ReadFromJsonAsync<GitHubWebhookReceipt>();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(receipt);
        Assert.Equal(deliveryId, receipt.DeliveryId);
        Assert.Equal("pull_request", receipt.EventName);
        Assert.Equal("accepted", receipt.Status);
    }

    [Fact]
    public async Task PostWebhook_WithInvalidSignature_ReturnsUnauthorized()
    {
        using var request = CreateRequest(
            Payload,
            $"sha256={new string('0', 64)}");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostWebhook_WithoutSignature_ReturnsUnauthorized()
    {
        using var request = CreateRequest(Payload, signature: null);

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("sha1=0123456789abcdef")]
    [InlineData("sha256=not-a-hex-digest")]
    public async Task PostWebhook_WithMalformedSignature_ReturnsBadRequest(string signature)
    {
        using var request = CreateRequest(Payload, signature);

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostWebhook_WhenDeliveryIsRepeated_ReturnsDuplicateWithoutAcceptingAgain()
    {
        var deliveryId = Guid.NewGuid();
        using var firstRequest = CreateRequest(
            Payload,
            CreateSignature(Payload),
            deliveryId: deliveryId.ToString());
        using var repeatedRequest = CreateRequest(
            Payload,
            CreateSignature(Payload),
            deliveryId: deliveryId.ToString());

        using var firstResponse = await _client.SendAsync(firstRequest);
        using var repeatedResponse = await _client.SendAsync(repeatedRequest);
        var repeatedReceipt =
            await repeatedResponse.Content.ReadFromJsonAsync<GitHubWebhookReceipt>();

        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeatedResponse.StatusCode);
        Assert.NotNull(repeatedReceipt);
        Assert.Equal(deliveryId, repeatedReceipt.DeliveryId);
        Assert.Equal("duplicate", repeatedReceipt.Status);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task PostWebhook_WithoutRequiredMetadata_ReturnsBadRequest(
        bool includeDeliveryHeader,
        bool includeEventHeader)
    {
        using var request = CreateRequest(
            Payload,
            CreateSignature(Payload),
            includeDeliveryHeader: includeDeliveryHeader,
            includeEventHeader: includeEventHeader);

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostWebhook_WithNonGuidDeliveryId_ReturnsBadRequest()
    {
        using var request = CreateRequest(
            Payload,
            CreateSignature(Payload),
            deliveryId: "not-a-guid");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostWebhook_WithMalformedJson_DoesNotReserveDeliveryId()
    {
        var deliveryId = Guid.NewGuid();
        var malformedPayload = Encoding.UTF8.GetBytes("{not-json}");
        using var malformedRequest = CreateRequest(
            malformedPayload,
            CreateSignature(malformedPayload),
            deliveryId: deliveryId.ToString());
        using var validRequest = CreateRequest(
            Payload,
            CreateSignature(Payload),
            deliveryId: deliveryId.ToString());

        using var malformedResponse = await _client.SendAsync(malformedRequest);
        using var validResponse = await _client.SendAsync(validRequest);

        Assert.Equal(HttpStatusCode.BadRequest, malformedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, validResponse.StatusCode);
    }

    [Fact]
    public async Task PostWebhook_WithNonObjectJson_ReturnsBadRequest()
    {
        var arrayPayload = Encoding.UTF8.GetBytes("[]");
        using var request = CreateRequest(
            arrayPayload,
            CreateSignature(arrayPayload));

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static HttpRequestMessage CreateRequest(
        byte[] requestBody,
        string? signature,
        string? deliveryId = null,
        bool includeDeliveryHeader = true,
        bool includeEventHeader = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, GitHubWebhookEndpoint.Route)
        {
            Content = new ByteArrayContent(requestBody)
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        if (signature is not null)
        {
            request.Headers.Add(GitHubWebhookSignatureValidator.SignatureHeaderName, signature);
        }

        if (includeDeliveryHeader)
        {
            request.Headers.Add(
                GitHubWebhookEndpoint.DeliveryHeaderName,
                deliveryId ?? Guid.NewGuid().ToString());
        }

        if (includeEventHeader)
        {
            request.Headers.Add(GitHubWebhookEndpoint.EventHeaderName, "pull_request");
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
