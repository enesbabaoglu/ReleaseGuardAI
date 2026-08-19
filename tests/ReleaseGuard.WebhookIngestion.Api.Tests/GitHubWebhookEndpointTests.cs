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
        """
        {
          "action": "opened",
          "number": 42,
          "repository": {
            "full_name": "acme/ReleaseGuard"
          },
          "pull_request": {
            "title": "Protect production releases",
            "user": {
              "login": "octocat"
            },
            "base": {
              "ref": "main"
            },
            "head": {
              "ref": "feature/release-guard"
            },
            "draft": false,
            "changed_files": 4,
            "additions": 120,
            "deletions": 15
          }
        }
        """);

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
        Assert.NotNull(receipt.RiskInput);
        Assert.Equal(deliveryId, receipt.RiskInput.SourceDeliveryId);
        Assert.Equal("github", receipt.RiskInput.SourceProvider);
        Assert.Equal("change_opened", receipt.RiskInput.Kind);
        Assert.Equal("acme/ReleaseGuard", receipt.RiskInput.Repository);
        Assert.Equal(42, receipt.RiskInput.ChangeNumber);
        Assert.Equal("Protect production releases", receipt.RiskInput.Title);
        Assert.Equal("octocat", receipt.RiskInput.Author);
        Assert.Equal("main", receipt.RiskInput.BaseBranch);
        Assert.Equal("feature/release-guard", receipt.RiskInput.HeadBranch);
        Assert.False(receipt.RiskInput.IsDraft);
        Assert.Equal(4, receipt.RiskInput.ChangedFiles);
        Assert.Equal(120, receipt.RiskInput.Additions);
        Assert.Equal(15, receipt.RiskInput.Deletions);
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
        Assert.Null(repeatedReceipt.RiskInput);
    }

    [Theory]
    [InlineData("pull_request", "closed")]
    [InlineData("push", "opened")]
    public async Task PostWebhook_WithUnsupportedEventOrAction_ReturnsIgnoredReceipt(
        string eventName,
        string action)
    {
        var payload = Encoding.UTF8.GetBytes($$"""
            {
              "action": "{{action}}"
            }
            """);
        using var request = CreateRequest(
            payload,
            CreateSignature(payload),
            eventName: eventName);

        using var response = await _client.SendAsync(request);
        var receipt = await response.Content.ReadFromJsonAsync<GitHubWebhookReceipt>();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(receipt);
        Assert.Equal(eventName, receipt.EventName);
        Assert.Equal("ignored", receipt.Status);
        Assert.Null(receipt.RiskInput);
    }

    [Fact]
    public async Task PostWebhook_WithInvalidOpenedPullRequest_DoesNotReserveDeliveryId()
    {
        var deliveryId = Guid.NewGuid();
        var incompletePayload = Encoding.UTF8.GetBytes(
            """{"action":"opened","number":42}""");
        using var incompleteRequest = CreateRequest(
            incompletePayload,
            CreateSignature(incompletePayload),
            deliveryId: deliveryId.ToString());
        using var validRequest = CreateRequest(
            Payload,
            CreateSignature(Payload),
            deliveryId: deliveryId.ToString());

        using var incompleteResponse = await _client.SendAsync(incompleteRequest);
        using var validResponse = await _client.SendAsync(validRequest);

        Assert.Equal(HttpStatusCode.BadRequest, incompleteResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, validResponse.StatusCode);
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
        string eventName = "pull_request",
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
            request.Headers.Add(GitHubWebhookEndpoint.EventHeaderName, eventName);
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
