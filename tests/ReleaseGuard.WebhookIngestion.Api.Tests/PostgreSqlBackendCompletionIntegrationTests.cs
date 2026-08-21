using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Npgsql;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

[Collection(PostgreSqlIntegrationCollection.CollectionName)]
public sealed class PostgreSqlBackendCompletionIntegrationTests
{
    private readonly PostgreSqlIntegrationFixture _postgresql;

    public PostgreSqlBackendCompletionIntegrationTests(
        PostgreSqlIntegrationFixture postgresql)
    {
        _postgresql = postgresql;
    }

    [Fact]
    public async Task Collection_UsesStableKeysetPagesAndExplicitLatestAcceptance()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var inbox = new PostgreSqlReleaseRiskInboxStore(dataSource);
        var first = CreateEnvelope("acme/releaseguard", 42);
        var second = CreateEnvelope("acme/releaseguard", 42);
        var third = CreateEnvelope("acme/other", 7);
        await AcceptAsync(inbox, first, 0);
        await AcceptAsync(inbox, second, 1);
        await AcceptAsync(inbox, third, 2);
        await SetAcceptedAtAsync(
            connectionString,
            first.EventId,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        await SetAcceptedAtAsync(
            connectionString,
            second.EventId,
            DateTimeOffset.Parse("2026-01-02T00:00:00Z"));
        await SetAcceptedAtAsync(
            connectionString,
            third.EventId,
            DateTimeOffset.Parse("2026-01-03T00:00:00Z"));

        using var application = new PostgreSqlTestApplicationFactory(
            connectionString,
            applyMigrationsOnStartup: false);
        using var client = application.CreateClient();

        string cursor;
        using (var firstPage = await SendQueryGetAsync(
                   client,
                   "/v1/release-risk-events/ai-explanations?limit=2"))
        using (var body = await ReadJsonAsync(firstPage))
        {
            Assert.Equal(HttpStatusCode.OK, firstPage.StatusCode);
            var items = body.RootElement.GetProperty("items");
            Assert.Equal(2, items.GetArrayLength());
            Assert.Equal(
                third.EventId,
                items[0].GetProperty("eventId").GetGuid());
            Assert.Equal(
                second.EventId,
                items[1].GetProperty("eventId").GetGuid());
            cursor = body.RootElement.GetProperty("nextCursor").GetString()!;
        }

        using (var secondPage = await SendQueryGetAsync(
                   client,
                   $"/v1/release-risk-events/ai-explanations?limit=2&cursor={cursor}"))
        using (var body = await ReadJsonAsync(secondPage))
        {
            Assert.Equal(HttpStatusCode.OK, secondPage.StatusCode);
            var item = Assert.Single(
                body.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(first.EventId, item.GetProperty("eventId").GetGuid());
            Assert.False(body.RootElement.TryGetProperty("nextCursor", out _));
        }

        using var latest = await SendQueryGetAsync(
            client,
            "/v1/repositories/acme/releaseguard/changes/42/ai-explanation/latest-accepted");
        using var latestBody = await ReadJsonAsync(latest);
        Assert.Equal(HttpStatusCode.OK, latest.StatusCode);
        Assert.Equal(
            second.EventId,
            latestBody.RootElement.GetProperty("eventId").GetGuid());
        Assert.Equal(
            "latestAccepted",
            latestBody.RootElement.GetProperty("selection").GetString());
        Assert.Equal(
            "pending",
            latestBody.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Replay_PreservesOriginalTerminalRowAndProcessesNewGeneration()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var inbox = new PostgreSqlReleaseRiskInboxStore(dataSource);
        var store = new PostgreSqlReleaseRiskExplanationStore(dataSource);
        var envelope = CreateEnvelope("acme/releaseguard", 42);
        await AcceptAsync(inbox, envelope, 0);
        var baseClaim = Assert.Single(await store.ClaimPendingAsync(
            "terminal-before-replay",
            1,
            TimeSpan.FromSeconds(30),
            5,
            CancellationToken.None));
        var baseFailure = new ReleaseRiskExplanationTerminalFailure(
            AiExplanationFailureClassifier.ResponseContractInvalidCode,
            "AI explanation response violated the required response contract.");
        Assert.True(await store.MarkTerminalAsync(
            baseClaim,
            baseFailure,
            CancellationToken.None));
        var originalBefore = await ReadOriginalTerminalAsync(
            connectionString,
            envelope.EventId);

        using var application = new PostgreSqlTestApplicationFactory(
            connectionString,
            applyMigrationsOnStartup: false);
        using var client = application.CreateClient();
        var replayId = Guid.NewGuid();
        var route =
            $"/v1/release-risk-events/{envelope.EventId:D}/ai-explanation/replays";
        string receipt;
        using (var request = CreateReplayRequest(route, replayId))
        using (var response = await client.SendAsync(request))
        {
            receipt = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        using (var duplicate = CreateReplayRequest(route, replayId))
        using (var response = await client.SendAsync(duplicate))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal(receipt, await response.Content.ReadAsStringAsync());
        }

        using (var pending = await SendQueryGetAsync(
                   client,
                   $"/v1/release-risk-events/{envelope.EventId:D}/ai-explanation"))
        using (var body = await ReadJsonAsync(pending))
        {
            Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
            Assert.Equal(
                "pending",
                body.RootElement.GetProperty("status").GetString());
        }

        var replayClaim = Assert.Single(await store.ClaimPendingAsync(
            "replay-worker",
            1,
            TimeSpan.FromSeconds(30),
            5,
            CancellationToken.None));
        Assert.Equal(replayId, replayClaim.ReplayId);
        Assert.Equal(1, replayClaim.Generation);
        var explanation = new ReleaseRiskExplanation
        {
            EventId = envelope.EventId,
            Summary =
                "Replay completed without mutating the original terminal row.",
            Recommendations = ["Review the recovered explanation."]
        };
        Assert.True(await store.MarkCompletedAsync(
            replayClaim,
            explanation,
            CancellationToken.None));

        using (var completed = await SendQueryGetAsync(
                   client,
                   $"/v1/release-risk-events/{envelope.EventId:D}/ai-explanation"))
        using (var body = await ReadJsonAsync(completed))
        {
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
            Assert.Equal(
                "completed",
                body.RootElement.GetProperty("status").GetString());
            Assert.Equal(
                explanation.Summary,
                body.RootElement.GetProperty("explanation")
                    .GetProperty("summary").GetString());
        }

        Assert.Equal(
            originalBefore,
            await ReadOriginalTerminalAsync(
                connectionString,
                envelope.EventId));
        var replayState = await ReadReplayStateAsync(
            connectionString,
            replayId);
        Assert.Equal(1, replayState.Generation);
        Assert.NotNull(replayState.CompletedAt);
        Assert.Null(replayState.FailedAt);

        using var ineligibleRequest = CreateReplayRequest(route, Guid.NewGuid());
        using var ineligible = await client.SendAsync(ineligibleRequest);
        Assert.Equal(HttpStatusCode.Conflict, ineligible.StatusCode);
    }

    [Fact]
    public async Task Replay_ConcurrentSameIdempotencyKeyCreatesOneGeneration()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var inbox = new PostgreSqlReleaseRiskInboxStore(dataSource);
        var explanationStore = new PostgreSqlReleaseRiskExplanationStore(
            dataSource);
        var replayStore = new PostgreSqlReleaseRiskExplanationReplayStore(
            dataSource);
        var envelope = CreateEnvelope("acme/releaseguard", 43);
        await AcceptAsync(inbox, envelope, 0);
        var claim = Assert.Single(await explanationStore.ClaimPendingAsync(
            "concurrent-replay-terminal",
            1,
            TimeSpan.FromSeconds(30),
            5,
            CancellationToken.None));
        Assert.True(await explanationStore.MarkTerminalAsync(
            claim,
            new ReleaseRiskExplanationTerminalFailure(
                AiExplanationFailureClassifier.ResponseContractInvalidCode,
                "AI explanation response violated the required response contract."),
            CancellationToken.None));
        var replayId = Guid.NewGuid();

        var receipts = await Task.WhenAll(
            replayStore.RequestReplayAsync(
                envelope.EventId,
                replayId,
                CancellationToken.None),
            replayStore.RequestReplayAsync(
                envelope.EventId,
                replayId,
                CancellationToken.None));

        Assert.Equal(
            [
                ReleaseRiskExplanationReplayDisposition.Accepted,
                ReleaseRiskExplanationReplayDisposition.Duplicate
            ],
            receipts.Select(receipt => receipt.Disposition)
                .OrderBy(disposition => disposition)
                .ToArray());
        Assert.All(receipts, receipt =>
        {
            Assert.Equal(replayId, receipt.ReplayId);
            Assert.Equal(envelope.EventId, receipt.EventId);
            Assert.Equal(1, receipt.Generation);
        });
        Assert.Equal(receipts[0].RequestedAt, receipts[1].RequestedAt);
        Assert.Equal(
            1,
            await CountRowsAsync(
                connectionString,
                "release_risk_ai_explanation_replays"));
    }

    [Fact]
    public async Task Retention_DeletesOnlyDurablyAcceptedPublishedTransportHistory()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var deliveryStore = new PostgreSqlGitHubWebhookDeliveryStore(dataSource);
        var inboxStore = new PostgreSqlReleaseRiskInboxStore(dataSource);
        var safe = CreateEnvelope("acme/releaseguard", 1);
        var pending = CreateEnvelope("acme/releaseguard", 2);
        var ignoredId = Guid.NewGuid();
        await AcceptDeliveryAsync(deliveryStore, safe);
        await AcceptDeliveryAsync(deliveryStore, pending);
        await AcceptAsync(inboxStore, safe, 0);
        await AcceptIgnoredAsync(deliveryStore, ignoredId);
        await AgeRetentionRowsAsync(
            connectionString,
            safe.EventId,
            pending.EventId,
            ignoredId);
        var retention = new PostgreSqlReleaseGuardRetentionStore(dataSource);

        var result = await retention.DeleteBatchAsync(
            100,
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(1),
            CancellationToken.None);

        Assert.Equal(1, result.PublishedOutboxMessagesDeleted);
        Assert.Equal(1, result.AcceptedDeliveriesDeleted);
        Assert.Equal(1, result.IgnoredDeliveriesDeleted);
        Assert.True(await ExistsAsync(
            connectionString,
            "release_risk_event_inbox",
            safe.EventId));
        Assert.False(await ExistsAsync(
            connectionString,
            "release_risk_outbox_messages",
            safe.EventId));
        Assert.False(await ExistsAsync(
            connectionString,
            "github_webhook_deliveries",
            safe.EventId));
        Assert.True(await ExistsAsync(
            connectionString,
            "release_risk_outbox_messages",
            pending.EventId));
        Assert.True(await ExistsAsync(
            connectionString,
            "github_webhook_deliveries",
            pending.EventId));
    }

    private async Task<string> CreateInitializedDatabaseAsync()
    {
        var connectionString = await _postgresql.CreateIsolatedDatabaseAsync();
        using var application = new PostgreSqlTestApplicationFactory(
            connectionString,
            applyMigrationsOnStartup: true);
        using var client = application.CreateClient();
        using var health = await client.GetAsync("/health");
        health.EnsureSuccessStatusCode();
        return connectionString;
    }

    private static ReleaseRiskOutboxEnvelope CreateEnvelope(
        string repository,
        long changeNumber)
    {
        var eventId = Guid.NewGuid();
        var input = new ReleaseRiskInput(
            eventId,
            "github",
            GitHubRiskInputMapper.ChangeOpenedKind,
            repository,
            changeNumber,
            "Complete backend checkpoints",
            "octocat",
            "main",
            "feature/backend-completion",
            false,
            4,
            120,
            15);
        return ReleaseRiskOutboxEnvelope.Create(
            eventId,
            input,
            new ReleaseRiskEvaluator().Evaluate(input));
    }

    private static async Task AcceptAsync(
        IReleaseRiskInboxStore store,
        ReleaseRiskOutboxEnvelope envelope,
        long offset)
    {
        Assert.Equal(
            ReleaseRiskInboxAcceptance.Accepted,
            await store.AcceptAsync(
                new ConsumedReleaseRiskEvent(
                    "releaseguard.release-risk-assessed",
                    0,
                    offset,
                    envelope.EventId,
                    envelope.SerializeToUtf8Bytes(),
                    envelope),
                CancellationToken.None));
    }

    private static async Task AcceptDeliveryAsync(
        IGitHubWebhookDeliveryStore store,
        ReleaseRiskOutboxEnvelope envelope)
    {
        using var payload = JsonDocument.Parse("{\"test\":true}");
        Assert.True(await store.TryAcceptAsync(
            new VerifiedGitHubWebhook(
                envelope.EventId,
                "pull_request",
                payload.RootElement.Clone()),
            envelope.RiskInput,
            envelope.RiskAssessment,
            CancellationToken.None));
    }

    private static async Task AcceptIgnoredAsync(
        IGitHubWebhookDeliveryStore store,
        Guid eventId)
    {
        using var payload = JsonDocument.Parse("{\"ignored\":true}");
        Assert.True(await store.TryAcceptAsync(
            new VerifiedGitHubWebhook(
                eventId,
                "push",
                payload.RootElement.Clone()),
            null,
            null,
            CancellationToken.None));
    }

    private static async Task SetAcceptedAtAsync(
        string connectionString,
        Guid eventId,
        DateTimeOffset acceptedAt)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE release_risk_event_inbox SET accepted_at = @accepted_at WHERE event_id = @event_id;",
            connection);
        command.Parameters.AddWithValue("accepted_at", acceptedAt);
        command.Parameters.AddWithValue("event_id", eventId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task AgeRetentionRowsAsync(
        string connectionString,
        Guid safeEventId,
        Guid pendingEventId,
        Guid ignoredId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE github_webhook_deliveries
            SET accepted_at = clock_timestamp() - interval '10 days'
            WHERE delivery_id IN (@safe_event_id, @pending_event_id, @ignored_id);

            UPDATE release_risk_outbox_messages
            SET
                created_at = clock_timestamp() - interval '10 days',
                next_attempt_at = clock_timestamp() - interval '10 days',
                published_at = CASE
                    WHEN event_id = @safe_event_id
                    THEN clock_timestamp() - interval '9 days'
                    ELSE NULL
                END
            WHERE event_id IN (@safe_event_id, @pending_event_id);
            """,
            connection);
        command.Parameters.AddWithValue("safe_event_id", safeEventId);
        command.Parameters.AddWithValue("pending_event_id", pendingEventId);
        command.Parameters.AddWithValue("ignored_id", ignoredId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> ExistsAsync(
        string connectionString,
        string table,
        Guid eventId)
    {
        var idColumn = string.Equals(
            table,
            "github_webhook_deliveries",
            StringComparison.Ordinal)
            ? "delivery_id"
            : "event_id";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT EXISTS(SELECT 1 FROM {table} WHERE {idColumn} = @event_id);",
            connection);
        command.Parameters.AddWithValue("event_id", eventId);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    private static async Task<int> CountRowsAsync(
        string connectionString,
        string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM {table};",
            connection);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<OriginalTerminal> ReadOriginalTerminalAsync(
        string connectionString,
        Guid eventId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                explanation_attempt_count,
                explanation_failed_at,
                explanation_failure_code,
                explanation_failure_reason,
                envelope::text
            FROM release_risk_event_inbox
            WHERE event_id = @event_id;
            """,
            connection);
        command.Parameters.AddWithValue("event_id", eventId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new OriginalTerminal(
            reader.GetInt32(0),
            reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4));
    }

    private static async Task<ReplayState> ReadReplayStateAsync(
        string connectionString,
        Guid replayId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT generation, completed_at, failed_at
            FROM release_risk_ai_explanation_replays
            WHERE replay_id = @replay_id;
            """,
            connection);
        command.Parameters.AddWithValue("replay_id", replayId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new ReplayState(
            reader.GetInt32(0),
            reader.IsDBNull(1)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(1),
            reader.IsDBNull(2)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(2));
    }

    private static HttpRequestMessage CreateReplayRequest(
        string route,
        Guid replayId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TestApplicationFactory.AiExplanationReplayCredential);
        request.Headers.TryAddWithoutValidation(
            ReleaseRiskExplanationReplayEndpoint.IdempotencyHeaderName,
            replayId.ToString("D"));
        return request;
    }

    private static async Task<HttpResponseMessage> SendQueryGetAsync(
        HttpClient client,
        string route)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TestApplicationFactory.AiExplanationQueryCredential);
        return await client.SendAsync(request);
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private sealed record OriginalTerminal(
        int AttemptCount,
        DateTimeOffset FailedAt,
        string FailureCode,
        string FailureReason,
        string EnvelopeJson);

    private sealed record ReplayState(
        int Generation,
        DateTimeOffset? CompletedAt,
        DateTimeOffset? FailedAt);
}
