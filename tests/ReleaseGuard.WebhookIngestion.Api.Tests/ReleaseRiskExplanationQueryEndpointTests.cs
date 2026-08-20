using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class ReleaseRiskExplanationQueryEndpointTests :
    IClassFixture<TestApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly TestApplicationFactory.TestExplanationQuery _query;

    public ReleaseRiskExplanationQueryEndpointTests(
        TestApplicationFactory application)
    {
        _client = application.CreateClient();
        _query = application.ExplanationQuery;
    }

    [Fact]
    public async Task Get_WithPendingEvent_ReturnsOnlyPendingStatus()
    {
        var eventId = Guid.NewGuid();
        _query.SetSnapshot(
            eventId,
            new PendingReleaseRiskExplanationQuerySnapshot(eventId));

        using var response = await GetAuthorizedAsync(Route(eventId));
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            ["eventId", "status"],
            body.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .ToArray());
        Assert.Equal(eventId, body.RootElement.GetProperty("eventId").GetGuid());
        Assert.Equal("pending", body.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Get_WithCompletedEvent_ReturnsEventBoundExplanationOnly()
    {
        var eventId = Guid.NewGuid();
        _query.SetSnapshot(
            eventId,
            new CompletedReleaseRiskExplanationQuerySnapshot(
                eventId,
                CreateExplanation(eventId, "durable explanation")));

        using var response = await GetAuthorizedAsync(Route(eventId));
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            ["eventId", "status", "explanation"],
            body.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .ToArray());
        Assert.Equal("completed", body.RootElement.GetProperty("status").GetString());
        var explanation = body.RootElement.GetProperty("explanation");
        Assert.Equal(eventId, explanation.GetProperty("eventId").GetGuid());
        Assert.Equal(
            "durable explanation",
            explanation.GetProperty("summary").GetString());
        Assert.Equal(
            ["review durable explanation"],
            explanation.GetProperty("recommendations")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToArray());
    }

    [Fact]
    public async Task Get_WithFailedEvent_ReturnsStableFailureOnly()
    {
        var eventId = Guid.NewGuid();
        _query.SetSnapshot(
            eventId,
            new FailedReleaseRiskExplanationQuerySnapshot(
                eventId,
                new ReleaseRiskExplanationTerminalFailure(
                    "response_contract_invalid",
                    "AI explanation response violated the required response contract.")));

        using var response = await GetAuthorizedAsync(Route(eventId));
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            ["eventId", "status", "failure"],
            body.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .ToArray());
        Assert.Equal("failed", body.RootElement.GetProperty("status").GetString());
        var failure = body.RootElement.GetProperty("failure");
        Assert.Equal(
            "response_contract_invalid",
            failure.GetProperty("code").GetString());
        Assert.Equal(
            "AI explanation response violated the required response contract.",
            failure.GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00112233445566778899aabbccddeeff")]
    [InlineData("{00112233-4455-6677-8899-aabbccddeeff}")]
    public async Task Get_WithMalformedEventId_ReturnsStableBadRequest(
        string eventId)
    {
        using var response = await GetAuthorizedAsync(Route(eventId));
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            ReleaseRiskExplanationQueryEndpoint.MalformedEventIdCode,
            body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Get_WithUnknownEvent_ReturnsStableNotFound()
    {
        using var response = await GetAuthorizedAsync(Route(Guid.NewGuid()));
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            ReleaseRiskExplanationQueryEndpoint.NotFoundCode,
            body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Get_WithAuthenticationFailures_ReturnsIdenticalStableUnauthorizedBeforeQuery()
    {
        var eventId = Guid.NewGuid();
        _query.SetHandler(
            eventId,
            _ => throw new InvalidOperationException(
                "Authentication failures must not reach the query."));
        var authorizationValues = new string?[][]
        {
            [],
            ["Bearer"],
            ["Basic malformed-authorization-value"],
            ["Bearer wrong-credential"],
            [
                $"Bearer {TestApplicationFactory.AiExplanationQueryCredential}",
                $"Bearer {TestApplicationFactory.AiExplanationQueryCredential}"
            ]
        };
        string? expectedBody = null;

        foreach (var values in authorizationValues)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                Route(eventId));
            if (values.Length > 0)
            {
                request.Headers.TryAddWithoutValidation(
                    AiExplanationQueryAuthenticator.HeaderName,
                    values);
            }

            using var response = await _client.SendAsync(request);
            var bodyText = await response.Content.ReadAsStringAsync();
            using var body = JsonDocument.Parse(bodyText);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(
                AiExplanationQueryAuthenticator.Challenge,
                Assert.Single(response.Headers.WwwAuthenticate).Scheme);
            Assert.Equal(
                ReleaseRiskExplanationQueryEndpoint.AuthenticationFailedCode,
                body.RootElement.GetProperty("code").GetString());
            Assert.Equal("Authentication failed.",
                body.RootElement.GetProperty("title").GetString());
            Assert.Equal("The request could not be authenticated.",
                body.RootElement.GetProperty("detail").GetString());

            expectedBody ??= bodyText;
            Assert.Equal(expectedBody, bodyText);
        }
    }

    [Fact]
    public async Task Get_WhenReadDeadlineExpires_ReturnsServiceUnavailableAndCancelsRead()
    {
        var eventId = Guid.NewGuid();
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _query.SetHandler(
            eventId,
            async cancellationToken =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return null;
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult();
                    throw;
                }
            });

        using var response = await GetAuthorizedAsync(Route(eventId));
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            ReleaseRiskExplanationQueryEndpoint.QueryTimeoutCode,
            body.RootElement.GetProperty("code").GetString());
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task HandleAsync_WhenCallerCancels_PropagatesCancellation()
    {
        var eventId = Guid.NewGuid();
        var query = new TestApplicationFactory.TestExplanationQuery();
        query.SetHandler(
            eventId,
            async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return null;
            });
        using var cancellation = new CancellationTokenSource();
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization =
            $"Bearer {TestApplicationFactory.AiExplanationQueryCredential}";
        using var authenticator = CreateAuthenticator();

        var handling = ReleaseRiskExplanationQueryEndpoint.HandleAsync(
            context.Request,
            eventId.ToString("D"),
            authenticator,
            query,
            Options.Create(new AiExplanationQueryOptions()),
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handling);
    }

    [Fact]
    public void Response_CopiesCompletedRecommendationsIntoImmutableSnapshot()
    {
        var eventId = Guid.NewGuid();
        var recommendations = new List<string> { "first recommendation" };
        var explanation = new ReleaseRiskExplanation
        {
            EventId = eventId,
            Summary = "immutable result",
            Recommendations = recommendations
        };

        var response = ReleaseRiskExplanationQueryResponse.From(
            new CompletedReleaseRiskExplanationQuerySnapshot(
                eventId,
                explanation));
        recommendations[0] = "mutated source";

        Assert.NotNull(response.Explanation);
        Assert.Equal(
            "first recommendation",
            Assert.Single(response.Explanation.Recommendations));
        Assert.IsAssignableFrom<System.Collections.ObjectModel.ReadOnlyCollection<string>>(
            response.Explanation.Recommendations);
    }

    [Fact]
    public void Response_RejectsExplanationBoundToAnotherEvent()
    {
        var eventId = Guid.NewGuid();
        var snapshot = new CompletedReleaseRiskExplanationQuerySnapshot(
            eventId,
            CreateExplanation(Guid.NewGuid(), "wrong event"));

        Assert.Throws<InvalidOperationException>(
            () => ReleaseRiskExplanationQueryResponse.From(snapshot));
    }

    private static string Route(Guid eventId) => Route(eventId.ToString("D"));

    private static string Route(string eventId) =>
        $"/v1/release-risk-events/{eventId}/ai-explanation";

    private async Task<HttpResponseMessage> GetAuthorizedAsync(string route)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.TryAddWithoutValidation(
            AiExplanationQueryAuthenticator.HeaderName,
            $"Bearer {TestApplicationFactory.AiExplanationQueryCredential}");
        return await _client.SendAsync(request);
    }

    private static AiExplanationQueryAuthenticator CreateAuthenticator() =>
        new(Options.Create(
            new AiExplanationQueryAuthenticationOptions
            {
                Credential = TestApplicationFactory
                    .AiExplanationQueryCredential
            }));

    private static ReleaseRiskExplanation CreateExplanation(
        Guid eventId,
        string summary) =>
        new()
        {
            EventId = eventId,
            Summary = summary,
            Recommendations = [$"review {summary}"]
        };

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}
