using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Npgsql;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

[Collection(PostgreSqlIntegrationCollection.CollectionName)]
public sealed class PostgreSqlAiExplanationQueryIntegrationTests
{
    private readonly PostgreSqlIntegrationFixture _postgresql;

    public PostgreSqlAiExplanationQueryIntegrationTests(
        PostgreSqlIntegrationFixture postgresql)
    {
        _postgresql = postgresql;
    }

    [Fact]
    public async Task Endpoint_ReadsPendingCompletedAndFailedWithoutMutatingSnapshots()
    {
        var connectionString = await _postgresql.CreateIsolatedDatabaseAsync();
        using var application = new PostgreSqlTestApplicationFactory(
            connectionString,
            applyMigrationsOnStartup: true);
        using var client = application.CreateClient();
        using var health = await client.GetAsync("/health");
        health.EnsureSuccessStatusCode();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var inboxStore = new PostgreSqlReleaseRiskInboxStore(dataSource);
        var explanationStore = new PostgreSqlReleaseRiskExplanationStore(
            dataSource);
        var completedEnvelope = CreateEnvelope();
        var failedEnvelope = CreateEnvelope();
        await AcceptAsync(inboxStore, completedEnvelope, offset: 0);
        await AcceptAsync(inboxStore, failedEnvelope, offset: 1);

        using (var pendingResponse = await SendAuthorizedAsync(
                   client,
                   Route(completedEnvelope.EventId)))
        using (var pendingBody = await ReadJsonAsync(pendingResponse))
        {
            Assert.Equal(HttpStatusCode.OK, pendingResponse.StatusCode);
            Assert.Equal(
                "pending",
                pendingBody.RootElement.GetProperty("status").GetString());
        }

        var completedClaim = Assert.Single(
            await explanationStore.ClaimPendingAsync(
                "query-integration-completed",
                1,
                TimeSpan.FromSeconds(30),
                5,
                CancellationToken.None));
        Assert.Equal(completedEnvelope.EventId, completedClaim.EventId);
        var explanation = CreateExplanation(
            completedEnvelope.EventId,
            "durable completed snapshot");
        Assert.True(await explanationStore.MarkCompletedAsync(
            completedClaim,
            explanation,
            CancellationToken.None));

        var failedClaim = Assert.Single(
            await explanationStore.ClaimPendingAsync(
                "query-integration-failed",
                1,
                TimeSpan.FromSeconds(30),
                5,
                CancellationToken.None));
        Assert.Equal(failedEnvelope.EventId, failedClaim.EventId);
        var failure = new ReleaseRiskExplanationTerminalFailure(
            "response_contract_invalid",
            "AI explanation response violated the required response contract.");
        Assert.True(await explanationStore.MarkTerminalAsync(
            failedClaim,
            failure,
            CancellationToken.None));

        var completedBefore = await ReadStoredSnapshotAsync(
            connectionString,
            completedEnvelope.EventId);
        var failedBefore = await ReadStoredSnapshotAsync(
            connectionString,
            failedEnvelope.EventId);

        string firstCompletedBody;
        using (var completedResponse = await SendAuthorizedAsync(
                   client,
                   Route(completedEnvelope.EventId)))
        {
            firstCompletedBody = await completedResponse.Content.ReadAsStringAsync();
            using var completedBody = JsonDocument.Parse(firstCompletedBody);
            Assert.Equal(HttpStatusCode.OK, completedResponse.StatusCode);
            Assert.Equal(
                "completed",
                completedBody.RootElement.GetProperty("status").GetString());
            var returnedExplanation = completedBody.RootElement.GetProperty(
                "explanation");
            Assert.Equal(
                completedEnvelope.EventId,
                returnedExplanation.GetProperty("eventId").GetGuid());
            Assert.Equal(
                explanation.Summary,
                returnedExplanation.GetProperty("summary").GetString());
        }

        using (var repeatedCompletedResponse = await SendAuthorizedAsync(
                   client,
                   Route(completedEnvelope.EventId)))
        {
            Assert.Equal(
                firstCompletedBody,
                await repeatedCompletedResponse.Content.ReadAsStringAsync());
        }

        using (var failedResponse = await SendAuthorizedAsync(
                   client,
                   Route(failedEnvelope.EventId)))
        using (var failedBody = await ReadJsonAsync(failedResponse))
        {
            Assert.Equal(HttpStatusCode.OK, failedResponse.StatusCode);
            Assert.Equal(
                "failed",
                failedBody.RootElement.GetProperty("status").GetString());
            Assert.Equal(
                failure.Code,
                failedBody.RootElement.GetProperty("failure")
                    .GetProperty("code")
                    .GetString());
            Assert.Equal(
                failure.Reason,
                failedBody.RootElement.GetProperty("failure")
                    .GetProperty("reason")
                    .GetString());
        }

        Assert.Equal(
            completedBefore,
            await ReadStoredSnapshotAsync(
                connectionString,
                completedEnvelope.EventId));
        Assert.Equal(
            failedBefore,
            await ReadStoredSnapshotAsync(
                connectionString,
                failedEnvelope.EventId));

        using var notFoundResponse = await SendAuthorizedAsync(
            client,
            Route(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, notFoundResponse.StatusCode);
    }

    [Fact]
    public async Task Endpoint_WhenDatabaseReadDeadlineExpires_ReturnsStableTimeoutAndRemainsReadable()
    {
        var connectionString = await _postgresql.CreateIsolatedDatabaseAsync();
        using var application = new PostgreSqlTestApplicationFactory(
            connectionString,
            applyMigrationsOnStartup: true,
            queryReadTimeoutMilliseconds: 100);
        using var client = application.CreateClient();
        using var health = await client.GetAsync("/health");
        health.EnsureSuccessStatusCode();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var inboxStore = new PostgreSqlReleaseRiskInboxStore(dataSource);
        var envelope = CreateEnvelope();
        await AcceptAsync(inboxStore, envelope, offset: 0);

        await using (var blocker = new NpgsqlConnection(connectionString))
        {
            await blocker.OpenAsync();
            await using var transaction = await blocker.BeginTransactionAsync();
            await using var lockCommand = new NpgsqlCommand(
                "LOCK TABLE release_risk_event_inbox IN ACCESS EXCLUSIVE MODE;",
                blocker,
                transaction);
            await lockCommand.ExecuteNonQueryAsync();

            var stopwatch = Stopwatch.StartNew();
            using var response = await SendAuthorizedAsync(
                client,
                Route(envelope.EventId));
            stopwatch.Stop();
            using var body = await ReadJsonAsync(response);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(
                ReleaseRiskExplanationQueryEndpoint.QueryTimeoutCode,
                body.RootElement.GetProperty("code").GetString());
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
            await transaction.RollbackAsync();
        }

        using var recoveredResponse = await SendAuthorizedAsync(
            client,
            Route(envelope.EventId));
        using var recoveredBody = await ReadJsonAsync(recoveredResponse);
        Assert.Equal(HttpStatusCode.OK, recoveredResponse.StatusCode);
        Assert.Equal(
            "pending",
            recoveredBody.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Query_WhenDatabaseReadIsCanceled_PropagatesCancellation()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var inboxStore = new PostgreSqlReleaseRiskInboxStore(dataSource);
        var envelope = CreateEnvelope();
        await AcceptAsync(inboxStore, envelope, offset: 0);
        var query = new PostgreSqlReleaseRiskExplanationQuery(dataSource);
        await using var blocker = new NpgsqlConnection(connectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using var lockCommand = new NpgsqlCommand(
            "LOCK TABLE release_risk_event_inbox IN ACCESS EXCLUSIVE MODE;",
            blocker,
            transaction);
        await lockCommand.ExecuteNonQueryAsync();
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => query.ReadAsync(envelope.EventId, cancellation.Token));

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Endpoint_RejectsMissingMalformedDuplicateAndWrongCredentialBeforeServingPostgreSqlState()
    {
        var connectionString = await _postgresql.CreateIsolatedDatabaseAsync();
        using var application = new PostgreSqlTestApplicationFactory(
            connectionString,
            applyMigrationsOnStartup: true);
        using var client = application.CreateClient();
        using var health = await client.GetAsync("/health");
        health.EnsureSuccessStatusCode();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var inboxStore = new PostgreSqlReleaseRiskInboxStore(dataSource);
        var envelope = CreateEnvelope();
        await AcceptAsync(inboxStore, envelope, offset: 0);
        var invalidAuthorizationValues = new string?[][]
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
        string? expectedUnauthorizedBody = null;

        foreach (var values in invalidAuthorizationValues)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                Route(envelope.EventId));
            if (values.Length > 0)
            {
                request.Headers.TryAddWithoutValidation(
                    AiExplanationQueryAuthenticator.HeaderName,
                    values);
            }

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(
                AiExplanationQueryAuthenticator.Challenge,
                Assert.Single(response.Headers.WwwAuthenticate).Scheme);
            expectedUnauthorizedBody ??= body;
            Assert.Equal(expectedUnauthorizedBody, body);
        }

        using var authorizedResponse = await SendAuthorizedAsync(
            client,
            Route(envelope.EventId));
        using var authorizedBody = await ReadJsonAsync(authorizedResponse);

        Assert.Equal(HttpStatusCode.OK, authorizedResponse.StatusCode);
        Assert.Equal(
            "pending",
            authorizedBody.RootElement.GetProperty("status").GetString());
    }

    private async Task<string> CreateInitializedDatabaseAsync()
    {
        var connectionString = await _postgresql.CreateIsolatedDatabaseAsync();
        using var application = new PostgreSqlTestApplicationFactory(
            connectionString,
            applyMigrationsOnStartup: true);
        using var client = application.CreateClient();
        using var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();
        return connectionString;
    }

    private static async Task AcceptAsync(
        IReleaseRiskInboxStore store,
        ReleaseRiskOutboxEnvelope envelope,
        long offset)
    {
        var consumedEvent = new ConsumedReleaseRiskEvent(
            "releaseguard.release-risk-assessed",
            0,
            offset,
            envelope.EventId,
            envelope.SerializeToUtf8Bytes(),
            envelope);
        Assert.Equal(
            ReleaseRiskInboxAcceptance.Accepted,
            await store.AcceptAsync(consumedEvent, CancellationToken.None));
    }

    private static ReleaseRiskOutboxEnvelope CreateEnvelope()
    {
        var eventId = Guid.NewGuid();
        var input = new ReleaseRiskInput(
            eventId,
            "github",
            GitHubRiskInputMapper.ChangeOpenedKind,
            "acme/ReleaseGuard",
            42,
            "Protect production releases",
            "octocat",
            "main",
            "feature/release-guard",
            false,
            4,
            120,
            15);
        return ReleaseRiskOutboxEnvelope.Create(
            eventId,
            input,
            new ReleaseRiskEvaluator().Evaluate(input));
    }

    private static ReleaseRiskExplanation CreateExplanation(
        Guid eventId,
        string summary) =>
        new()
        {
            EventId = eventId,
            Summary = summary,
            Recommendations = ["Require a focused review."]
        };

    private static string Route(Guid eventId) =>
        $"/v1/release-risk-events/{eventId:D}/ai-explanation";

    private static async Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpClient client,
        string route)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.TryAddWithoutValidation(
            AiExplanationQueryAuthenticator.HeaderName,
            $"Bearer {TestApplicationFactory.AiExplanationQueryCredential}");
        return await client.SendAsync(request);
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private static async Task<StoredSnapshot> ReadStoredSnapshotAsync(
        string connectionString,
        Guid eventId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                encode(payload, 'hex'),
                envelope::text,
                explanation_attempt_count,
                explanation_next_attempt_at,
                explanation_claimed_by,
                explanation_claim_expires_at,
                explanation_completed_at,
                explanation::text,
                explanation_failed_at,
                explanation_failure_code,
                explanation_failure_reason
            FROM release_risk_event_inbox
            WHERE event_id = @event_id;
            """,
            connection);
        command.Parameters.AddWithValue("event_id", eventId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        return new StoredSnapshot(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10));
    }

    private sealed record StoredSnapshot(
        string PayloadHex,
        string EnvelopeJson,
        int AttemptCount,
        DateTimeOffset NextAttemptAt,
        string? ClaimedBy,
        DateTimeOffset? ClaimExpiresAt,
        DateTimeOffset? CompletedAt,
        string? ExplanationJson,
        DateTimeOffset? FailedAt,
        string? FailureCode,
        string? FailureReason);
}
