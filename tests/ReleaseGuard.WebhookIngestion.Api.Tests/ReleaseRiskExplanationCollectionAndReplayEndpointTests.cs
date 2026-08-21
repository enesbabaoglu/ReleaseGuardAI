using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class ReleaseRiskExplanationCollectionAndReplayEndpointTests
{
    [Fact]
    public void CursorCodec_RoundTripsCanonicalCursorAndRejectsMalformedInput()
    {
        var cursor = new ReleaseRiskExplanationListCursor(
            DateTimeOffset.Parse("2026-08-21T10:20:30.1234567+00:00"),
            Guid.Parse("b3cd5e23-e249-4e0e-9c93-c3178a59228c"));

        var encoded = ReleaseRiskExplanationListCursorCodec.Encode(cursor);

        Assert.True(ReleaseRiskExplanationListCursorCodec.TryDecode(
            encoded,
            out var decoded));
        Assert.Equal(cursor, decoded);
        Assert.False(ReleaseRiskExplanationListCursorCodec.TryDecode(
            "not-a-cursor",
            out _));
        Assert.False(ReleaseRiskExplanationListCursorCodec.TryDecode(
            string.Empty,
            out _));
    }

    [Fact]
    public async Task List_AuthenticatesBeforeQueryValidationAndReturnsBoundedPage()
    {
        using var application = new TestApplicationFactory();
        var eventId = Guid.NewGuid();
        application.ExplanationCollectionQuery.ReadPageHandler =
            (limit, cursor, token) =>
            {
                token.ThrowIfCancellationRequested();
                Assert.Equal(1, limit);
                Assert.Null(cursor);
                return Task.FromResult(new ReleaseRiskExplanationListPage(
                    [
                        new ReleaseRiskExplanationListItem(
                            eventId,
                            "pending",
                            DateTimeOffset.UnixEpoch,
                            "octo/releaseguard",
                            42,
                            "change_opened")
                    ],
                    "next-page"));
            };
        using var client = application.CreateClient();

        using (var unauthorized = await client.GetAsync(
                   ReleaseRiskExplanationListEndpoint.Route + "?limit=invalid"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            Assert.Equal(
                "Bearer",
                Assert.Single(unauthorized.Headers.WwwAuthenticate).Scheme);
        }

        using (var invalid = await SendAuthorizedAsync(
                   client,
                   ReleaseRiskExplanationListEndpoint.Route + "?limit=101"))
        using (var invalidBody = await ReadJsonAsync(invalid))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal(
                ReleaseRiskExplanationListEndpoint.InvalidQueryCode,
                invalidBody.RootElement.GetProperty("code").GetString());
        }

        Assert.Equal(1, application.ExplanationQueryMetrics.AuthenticationFailures);
        Assert.Equal(1, application.ExplanationQueryMetrics.RateLimitPermits);
        Assert.Empty(
            application.ExplanationQueryMetrics.DatabaseReadDurations);

        using var response = await SendAuthorizedAsync(
            client,
            ReleaseRiskExplanationListEndpoint.Route + "?limit=1");
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            eventId,
            body.RootElement.GetProperty("items")[0]
                .GetProperty("eventId").GetGuid());
        Assert.Equal(
            "next-page",
            body.RootElement.GetProperty("nextCursor").GetString());
        Assert.Single(
            application.ExplanationQueryMetrics.DatabaseReadDurations);
    }

    [Fact]
    public async Task LatestAccepted_UsesExplicitAcceptanceMeaningAndStableStateShape()
    {
        using var application = new TestApplicationFactory();
        var eventId = Guid.NewGuid();
        application.ExplanationCollectionQuery.ReadLatestHandler =
            (repository, changeNumber, token) =>
            {
                token.ThrowIfCancellationRequested();
                Assert.Equal("octo/releaseguard", repository);
                Assert.Equal(42, changeNumber);
                return Task.FromResult<LatestAcceptedReleaseRiskExplanation?>(
                    new LatestAcceptedReleaseRiskExplanation(
                        DateTimeOffset.UnixEpoch,
                        repository,
                        changeNumber,
                        "change_updated",
                        new CompletedReleaseRiskExplanationQuerySnapshot(
                            eventId,
                            new ReleaseRiskExplanation
                            {
                                EventId = eventId,
                                Summary =
                                    "Completed after durable acceptance.",
                                Recommendations = ["Review the result."]
                            })));
            };
        using var client = application.CreateClient();
        var route =
            "/v1/repositories/octo/releaseguard/changes/42/ai-explanation/latest-accepted";

        using var response = await SendAuthorizedAsync(client, route);
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "latestAccepted",
            body.RootElement.GetProperty("selection").GetString());
        Assert.Equal(eventId, body.RootElement.GetProperty("eventId").GetGuid());
        Assert.Equal(
            "completed",
            body.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "octo/releaseguard",
            body.RootElement.GetProperty("repository").GetString());
    }

    [Fact]
    public async Task CollectionRoutes_ShareOneCredentialIndependentReadBudget()
    {
        using var application = new TestApplicationFactory(
            TestApplicationFactory.AiExplanationQueryCredential,
            TestApplicationFactory.PreviousAiExplanationQueryCredential,
            rateLimitPermitLimit: 1,
            rateLimitWindowMilliseconds: 60_000);
        using var client = application.CreateClient();

        using (var admitted = await SendAuthorizedAsync(
                   client,
                   ReleaseRiskExplanationListEndpoint.Route))
        {
            Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        }

        using (var rejected = await SendAuthorizedAsync(
                   client,
                   "/v1/repositories/acme/releaseguard/changes/42/ai-explanation/latest-accepted",
                   TestApplicationFactory.PreviousAiExplanationQueryCredential))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.NotNull(rejected.Headers.RetryAfter?.Delta);
        }

        using var unauthorized = await SendAuthorizedAsync(
            client,
            ReleaseRiskExplanationListEndpoint.Route + "?limit=invalid",
            "wrong-credential");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    [Fact]
    public async Task Replay_UsesSeparateCredentialCanonicalIdempotencyAndStableReceipt()
    {
        using var application = new TestApplicationFactory();
        var eventId = Guid.NewGuid();
        var replayId = Guid.NewGuid();
        application.ExplanationReplayStore.Handler =
            (requestedEventId, requestedReplayId, token) =>
            {
                token.ThrowIfCancellationRequested();
                Assert.Equal(eventId, requestedEventId);
                Assert.Equal(replayId, requestedReplayId);
                return Task.FromResult(
                    new ReleaseRiskExplanationReplayReceipt(
                        replayId,
                        eventId,
                        2,
                        DateTimeOffset.UnixEpoch,
                        ReleaseRiskExplanationReplayDisposition.Duplicate));
            };
        using var client = application.CreateClient();
        var route = $"/v1/release-risk-events/{eventId:D}/ai-explanation/replays";

        using (var queryCredential = CreatePost(
                   route,
                   TestApplicationFactory.AiExplanationQueryCredential,
                   replayId))
        using (var unauthorized = await client.SendAsync(queryCredential))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        }

        using (var malformed = CreatePost(
                   route,
                   TestApplicationFactory.AiExplanationReplayCredential,
                   replayId: null))
        using (var badRequest = await client.SendAsync(malformed))
        {
            Assert.Equal(HttpStatusCode.BadRequest, badRequest.StatusCode);
        }

        using var request = CreatePost(
            route,
            TestApplicationFactory.AiExplanationReplayCredential,
            replayId);
        using var response = await client.SendAsync(request);
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(replayId, body.RootElement.GetProperty("replayId").GetGuid());
        Assert.Equal(eventId, body.RootElement.GetProperty("eventId").GetGuid());
        Assert.Equal(2, body.RootElement.GetProperty("generation").GetInt32());
        Assert.Equal("pending", body.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Replay_ActiveAndPreviousShareBudgetWithAuthenticationPriorityAndReset()
    {
        var time = new ManualTimeProvider();
        using var application = new TestApplicationFactory(
            TestApplicationFactory.AiExplanationQueryCredential,
            rateLimitTimeProvider: time,
            activeReplayCredential:
                TestApplicationFactory.AiExplanationReplayCredential,
            previousReplayCredential:
                TestApplicationFactory.PreviousAiExplanationQueryCredential,
            replayRateLimitPermitLimit: 1,
            replayRateLimitWindowMilliseconds: 1_000);
        var storeCalls = 0;
        application.ExplanationReplayStore.Handler =
            (eventId, replayId, token) =>
            {
                token.ThrowIfCancellationRequested();
                Interlocked.Increment(ref storeCalls);
                return Task.FromResult(
                    new ReleaseRiskExplanationReplayReceipt(
                        replayId,
                        eventId,
                        1,
                        DateTimeOffset.UnixEpoch,
                        ReleaseRiskExplanationReplayDisposition.Accepted));
            };
        using var client = application.CreateClient();
        var eventId = Guid.NewGuid();
        var route =
            $"/v1/release-risk-events/{eventId:D}/ai-explanation/replays";

        using (var active = CreatePost(
                   route,
                   TestApplicationFactory.AiExplanationReplayCredential,
                   Guid.NewGuid()))
        using (var admitted = await client.SendAsync(active))
        {
            Assert.Equal(HttpStatusCode.Accepted, admitted.StatusCode);
        }

        using (var previous = CreatePost(
                   route,
                   TestApplicationFactory.PreviousAiExplanationQueryCredential,
                   Guid.NewGuid()))
        using (var rejected = await client.SendAsync(previous))
        using (var body = await ReadJsonAsync(rejected))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.Equal(
                TimeSpan.FromSeconds(1),
                rejected.Headers.RetryAfter?.Delta);
            Assert.Equal(
                ReleaseRiskExplanationReplayEndpoint.RateLimitExceededCode,
                body.RootElement.GetProperty("code").GetString());
            Assert.Equal(
                "AI explanation replay rate limit exceeded.",
                body.RootElement.GetProperty("title").GetString());
            Assert.Equal(
                "The replay request rate limit was exceeded. Retry after the indicated delay.",
                body.RootElement.GetProperty("detail").GetString());
            Assert.Equal(429, body.RootElement.GetProperty("status").GetInt32());
        }

        using (var unauthorized = CreatePost(
                   route,
                   TestApplicationFactory.AiExplanationQueryCredential,
                   Guid.NewGuid()))
        using (var response = await client.SendAsync(unauthorized))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(
                "Bearer",
                Assert.Single(response.Headers.WwwAuthenticate).Scheme);
        }

        Assert.Equal(1, Volatile.Read(ref storeCalls));
        time.Advance(TimeSpan.FromSeconds(1));

        using var afterReset = CreatePost(
            route,
            TestApplicationFactory.PreviousAiExplanationQueryCredential,
            Guid.NewGuid());
        using var recovered = await client.SendAsync(afterReset);
        Assert.Equal(HttpStatusCode.Accepted, recovered.StatusCode);
        Assert.Equal(2, Volatile.Read(ref storeCalls));
    }

    [Theory]
    [InlineData(
        ReleaseRiskExplanationReplayDisposition.EventNotFound,
        HttpStatusCode.NotFound,
        ReleaseRiskExplanationReplayEndpoint.NotFoundCode)]
    [InlineData(
        ReleaseRiskExplanationReplayDisposition.NotEligible,
        HttpStatusCode.Conflict,
        ReleaseRiskExplanationReplayEndpoint.NotEligibleCode)]
    [InlineData(
        ReleaseRiskExplanationReplayDisposition.ReplayIdConflict,
        HttpStatusCode.Conflict,
        ReleaseRiskExplanationReplayEndpoint.ReplayIdConflictCode)]
    public async Task Replay_StoreRejectionsUseStableProblemCodes(
        ReleaseRiskExplanationReplayDisposition disposition,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        using var application = new TestApplicationFactory();
        application.ExplanationReplayStore.Handler =
            (eventId, replayId, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(
                    new ReleaseRiskExplanationReplayReceipt(
                        replayId,
                        eventId,
                        0,
                        default,
                        disposition));
            };
        using var client = application.CreateClient();
        var route =
            $"/v1/release-risk-events/{Guid.NewGuid():D}/ai-explanation/replays";
        using var request = CreatePost(
            route,
            TestApplicationFactory.AiExplanationReplayCredential,
            Guid.NewGuid());

        using var response = await client.SendAsync(request);
        using var body = await ReadJsonAsync(response);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(
            expectedCode,
            body.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            (int)expectedStatus,
            body.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Replay_DatabaseDeadlineIs503ButCallerCancellationPropagates()
    {
        using (var timeoutApplication = new TestApplicationFactory(
                   TestApplicationFactory.AiExplanationQueryCredential,
                   replayRequestTimeoutMilliseconds: 100))
        {
            timeoutApplication.ExplanationReplayStore.Handler =
                async (_, _, token) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    throw new InvalidOperationException("Unreachable.");
                };
            using var client = timeoutApplication.CreateClient();
            using var request = CreatePost(
                $"/v1/release-risk-events/{Guid.NewGuid():D}/ai-explanation/replays",
                TestApplicationFactory.AiExplanationReplayCredential,
                Guid.NewGuid());

            using var response = await client.SendAsync(request);
            using var body = await ReadJsonAsync(response);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(
                ReleaseRiskExplanationReplayEndpoint.TimeoutCode,
                body.RootElement.GetProperty("code").GetString());
        }

        using var cancellationApplication = new TestApplicationFactory(
            TestApplicationFactory.AiExplanationQueryCredential,
            replayRequestTimeoutMilliseconds: 30_000);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationApplication.ExplanationReplayStore.Handler =
            async (_, _, token) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("Unreachable.");
            };
        using var cancellationClient = cancellationApplication.CreateClient();
        using var cancellationRequest = CreatePost(
            $"/v1/release-risk-events/{Guid.NewGuid():D}/ai-explanation/replays",
            TestApplicationFactory.AiExplanationReplayCredential,
            Guid.NewGuid());
        using var cancellation = new CancellationTokenSource();
        var responseTask = cancellationClient.SendAsync(
            cancellationRequest,
            cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await responseTask);
    }

    private static HttpRequestMessage CreatePost(
        string route,
        string credential,
        Guid? replayId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            credential);
        if (replayId is not null)
        {
            request.Headers.TryAddWithoutValidation(
                ReleaseRiskExplanationReplayEndpoint.IdempotencyHeaderName,
                replayId.Value.ToString("D"));
        }

        return request;
    }

    private static async Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpClient client,
        string route,
        string? credential = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            credential ??
            TestApplicationFactory.AiExplanationQueryCredential);
        return await client.SendAsync(request);
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());
}
