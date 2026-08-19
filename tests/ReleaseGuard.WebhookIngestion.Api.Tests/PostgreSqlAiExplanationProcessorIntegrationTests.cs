using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

[Collection(PostgreSqlIntegrationCollection.CollectionName)]
public sealed class PostgreSqlAiExplanationProcessorIntegrationTests
{
    private readonly PostgreSqlIntegrationFixture _postgresql;

    public PostgreSqlAiExplanationProcessorIntegrationTests(
        PostgreSqlIntegrationFixture postgresql)
    {
        _postgresql = postgresql;
    }

    [Fact]
    public async Task Processor_PersistsEventBoundExplanationWithoutChangingRiskSnapshot()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        var envelope = await AcceptEnvelopeAsync(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new PostgreSqlReleaseRiskExplanationStore(dataSource);
        var client = new RecordingClient(envelope.EventId);
        using var processor = CreateProcessor(store, client);

        var processed = await processor.ProcessPendingBatchAsync(
            CancellationToken.None);

        Assert.Equal(1, processed);
        Assert.Equal(envelope.Serialize(), client.ReceivedEnvelope?.Serialize());
        var state = await ReadStateAsync(connectionString, envelope.EventId);
        Assert.Equal(1, state.AttemptCount);
        Assert.NotNull(state.CompletedAt);
        Assert.Null(state.ClaimedBy);
        Assert.Null(state.ClaimExpiresAt);
        Assert.NotNull(state.Explanation);
        Assert.Equal(envelope.EventId, state.Explanation.EventId);
        Assert.Equal("Risk snapshot explained.", state.Explanation.Summary);
        Assert.Equal(envelope.Serialize(), state.Envelope.Serialize());
        Assert.Equal(envelope.RiskAssessment.Score, state.Envelope.RiskAssessment.Score);
        Assert.Equal(envelope.RiskAssessment.Level, state.Envelope.RiskAssessment.Level);
        Assert.Equal(envelope.RiskAssessment.Factors, state.Envelope.RiskAssessment.Factors);
    }

    [Fact]
    public async Task ConcurrentStores_ClaimAcceptedEventOnlyOnce()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        await AcceptEnvelopeAsync(connectionString);
        await using var firstDataSource = NpgsqlDataSource.Create(connectionString);
        await using var secondDataSource = NpgsqlDataSource.Create(connectionString);
        var firstStore = new PostgreSqlReleaseRiskExplanationStore(
            firstDataSource);
        var secondStore = new PostgreSqlReleaseRiskExplanationStore(
            secondDataSource);

        var claims = await Task.WhenAll(
            firstStore.ClaimPendingAsync(
                "first-processor",
                1,
                TimeSpan.FromSeconds(30),
                5,
                CancellationToken.None),
            secondStore.ClaimPendingAsync(
                "second-processor",
                1,
                TimeSpan.FromSeconds(30),
                5,
                CancellationToken.None));

        Assert.Equal(1, claims.Sum(result => result.Count));
        Assert.Equal(1, claims.SelectMany(result => result).Single().AttemptCount);
    }

    [Fact]
    public async Task CrashRestart_RecoversExpiredLeaseAndRejectsStaleCompletion()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        var envelope = await AcceptEnvelopeAsync(connectionString);
        await using var firstDataSource = NpgsqlDataSource.Create(connectionString);
        await using var secondDataSource = NpgsqlDataSource.Create(connectionString);
        var firstStore = new PostgreSqlReleaseRiskExplanationStore(
            firstDataSource);
        var secondStore = new PostgreSqlReleaseRiskExplanationStore(
            secondDataSource);
        var firstClaim = Assert.Single(await firstStore.ClaimPendingAsync(
            "crashed-processor",
            1,
            TimeSpan.FromSeconds(30),
            5,
            CancellationToken.None));

        Assert.Empty(await secondStore.ClaimPendingAsync(
            "replacement-before-expiry",
            1,
            TimeSpan.FromSeconds(30),
            5,
            CancellationToken.None));

        await ExpireClaimAsync(connectionString, envelope.EventId);
        var recoveredClaim = Assert.Single(await secondStore.ClaimPendingAsync(
            "replacement-after-expiry",
            1,
            TimeSpan.FromSeconds(30),
            5,
            CancellationToken.None));
        var staleExplanation = CreateExplanation(envelope.EventId, "stale");
        var recoveredExplanation = CreateExplanation(
            envelope.EventId,
            "recovered");

        Assert.Equal(2, recoveredClaim.AttemptCount);
        Assert.False(await firstStore.MarkCompletedAsync(
            firstClaim,
            staleExplanation,
            CancellationToken.None));
        Assert.True(await secondStore.MarkCompletedAsync(
            recoveredClaim,
            recoveredExplanation,
            CancellationToken.None));

        var state = await ReadStateAsync(connectionString, envelope.EventId);
        Assert.Equal("recovered", state.Explanation?.Summary);
        Assert.Equal(2, state.AttemptCount);
    }

    [Fact]
    public async Task RetryableClientFailure_PersistsBoundedRetryWithoutResult()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        var envelope = await AcceptEnvelopeAsync(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new PostgreSqlReleaseRiskExplanationStore(dataSource);
        IReleaseRiskExplanationClient client = new ThrowingClient(
            new TimeoutException("Simulated request timeout."));
        using var processor = CreateProcessor(
            store,
            client,
            initialRetryDelayMilliseconds: 5_000,
            maximumRetryDelayMilliseconds: 5_000);

        Assert.Equal(
            1,
            await processor.ProcessPendingBatchAsync(CancellationToken.None));

        var state = await ReadStateAsync(connectionString, envelope.EventId);
        Assert.Equal(1, state.AttemptCount);
        Assert.Null(state.CompletedAt);
        Assert.Null(state.Explanation);
        Assert.Null(state.ClaimedBy);
        Assert.Null(state.ClaimExpiresAt);
        Assert.Null(state.FailedAt);
        Assert.True(state.NextAttemptAt > DateTimeOffset.UtcNow.AddSeconds(2));
        Assert.Empty(await store.ClaimPendingAsync(
            "too-early-retry",
            1,
            TimeSpan.FromSeconds(30),
            5,
            CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_ReleasesClaimForImmediateRestart()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        var envelope = await AcceptEnvelopeAsync(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new PostgreSqlReleaseRiskExplanationStore(dataSource);
        var client = new BlockingClient();
        using var processor = CreateProcessor(store, client);
        using var cancellation = new CancellationTokenSource();

        var processing = processor.ProcessPendingBatchAsync(cancellation.Token);
        await client.Started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        var replay = Assert.Single(await store.ClaimPendingAsync(
            "replacement-after-cancellation",
            1,
            TimeSpan.FromSeconds(30),
            5,
            CancellationToken.None));
        Assert.Equal(envelope.EventId, replay.EventId);
        Assert.Equal(2, replay.AttemptCount);
    }

    [Fact]
    public async Task DuplicateInboxAcceptance_DoesNotResetCompletedExplanation()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        var envelope = await AcceptEnvelopeAsync(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var inboxStore = new PostgreSqlReleaseRiskInboxStore(dataSource);
        var explanationStore = new PostgreSqlReleaseRiskExplanationStore(
            dataSource);
        var claim = Assert.Single(await explanationStore.ClaimPendingAsync(
            "complete-before-duplicate",
            1,
            TimeSpan.FromSeconds(30),
            5,
            CancellationToken.None));
        Assert.True(await explanationStore.MarkCompletedAsync(
            claim,
            CreateExplanation(envelope.EventId, "durable result"),
            CancellationToken.None));

        var duplicate = await inboxStore.AcceptAsync(
            CreateConsumedEvent(envelope, offset: 1),
            CancellationToken.None);

        Assert.Equal(ReleaseRiskInboxAcceptance.Duplicate, duplicate);
        var state = await ReadStateAsync(connectionString, envelope.EventId);
        Assert.Equal("durable result", state.Explanation?.Summary);
        Assert.Equal(1, state.AttemptCount);
        Assert.Empty(await explanationStore.ClaimPendingAsync(
            "must-not-reprocess-completed",
            1,
            TimeSpan.FromSeconds(30),
            5,
            CancellationToken.None));
    }

    [Fact]
    public async Task TerminalFailure_IsDurableQueryableAndDuplicateAcceptanceDoesNotResetIt()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        var envelope = await AcceptEnvelopeAsync(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var inboxStore = new PostgreSqlReleaseRiskInboxStore(dataSource);
        var store = new PostgreSqlReleaseRiskExplanationStore(dataSource);
        using var processor = CreateProcessor(
            store,
            new ThrowingClient(
                new ReleaseRiskExplanationEventIdConflictException(
                    envelope.EventId,
                    Guid.NewGuid())));

        Assert.Equal(
            1,
            await processor.ProcessPendingBatchAsync(CancellationToken.None));
        Assert.Equal(
            ReleaseRiskInboxAcceptance.Duplicate,
            await inboxStore.AcceptAsync(
                CreateConsumedEvent(envelope, offset: 1),
                CancellationToken.None));

        var state = await ReadStateAsync(connectionString, envelope.EventId);
        Assert.Null(state.CompletedAt);
        Assert.Null(state.Explanation);
        Assert.NotNull(state.FailedAt);
        Assert.Equal(
            AiExplanationFailureClassifier.EventIdConflictCode,
            state.FailureCode);
        Assert.Equal(
            "AI explanation response event ID did not match the claimed event.",
            state.FailureReason);
        Assert.Null(state.ClaimedBy);
        Assert.Null(state.ClaimExpiresAt);
        Assert.Empty(await store.ClaimPendingAsync(
            "must-not-reprocess-terminal",
            1,
            TimeSpan.FromSeconds(30),
            5,
            CancellationToken.None));

        var failed = Assert.Single(await store.ReadFailedWorkAsync(
            10,
            CancellationToken.None));
        Assert.Equal(envelope.EventId, failed.EventId);
        Assert.Equal(1, failed.AttemptCount);
        Assert.Equal(state.FailedAt, failed.FailedAt);
        Assert.Equal(state.FailureCode, failed.FailureCode);
        Assert.Equal(state.FailureReason, failed.FailureReason);
        Assert.Equal(envelope.Serialize(), failed.Envelope.Serialize());
    }

    [Fact]
    public async Task TerminalUpdate_IsIdempotentAndCannotBeOverwrittenBySuccess()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        var envelope = await AcceptEnvelopeAsync(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new PostgreSqlReleaseRiskExplanationStore(dataSource);
        var claim = Assert.Single(await store.ClaimPendingAsync(
            "terminal-owner",
            1,
            TimeSpan.FromSeconds(30),
            5,
            CancellationToken.None));
        var failure = new ReleaseRiskExplanationTerminalFailure(
            AiExplanationFailureClassifier.ResponseContractInvalidCode,
            "AI explanation response violated the required response contract.");

        Assert.True(await store.MarkTerminalAsync(
            claim,
            failure,
            CancellationToken.None));
        Assert.True(await store.MarkTerminalAsync(
            claim,
            failure,
            CancellationToken.None));
        Assert.False(await store.MarkTerminalAsync(
            claim,
            failure with { Reason = "A different terminal outcome." },
            CancellationToken.None));
        Assert.False(await store.MarkCompletedAsync(
            claim,
            CreateExplanation(envelope.EventId, "must not win"),
            CancellationToken.None));

        var state = await ReadStateAsync(connectionString, envelope.EventId);
        Assert.Equal(failure.Code, state.FailureCode);
        Assert.Equal(failure.Reason, state.FailureReason);
        Assert.Null(state.CompletedAt);
        Assert.Null(state.Explanation);
    }

    [Fact]
    public async Task SuccessfulOutcome_CannotBeOverwrittenByStaleTerminalFailure()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        var envelope = await AcceptEnvelopeAsync(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new PostgreSqlReleaseRiskExplanationStore(dataSource);
        var claim = Assert.Single(await store.ClaimPendingAsync(
            "success-owner",
            1,
            TimeSpan.FromSeconds(30),
            5,
            CancellationToken.None));

        Assert.True(await store.MarkCompletedAsync(
            claim,
            CreateExplanation(envelope.EventId, "durable success"),
            CancellationToken.None));
        Assert.False(await store.MarkTerminalAsync(
            claim,
            new ReleaseRiskExplanationTerminalFailure(
                AiExplanationFailureClassifier.UnexpectedErrorCode,
                "A stale terminal update."),
            CancellationToken.None));

        var state = await ReadStateAsync(connectionString, envelope.EventId);
        Assert.Equal("durable success", state.Explanation?.Summary);
        Assert.NotNull(state.CompletedAt);
        Assert.Null(state.FailedAt);
        Assert.Empty(await store.ReadFailedWorkAsync(
            10,
            CancellationToken.None));
    }

    [Fact]
    public async Task CrashAtAttemptLimit_TerminalizesExpiredWorkOnRestart()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        var envelope = await AcceptEnvelopeAsync(connectionString);
        await using var firstDataSource = NpgsqlDataSource.Create(connectionString);
        await using var restartedDataSource = NpgsqlDataSource.Create(
            connectionString);
        var firstStore = new PostgreSqlReleaseRiskExplanationStore(
            firstDataSource);
        var restartedStore = new PostgreSqlReleaseRiskExplanationStore(
            restartedDataSource);

        var abandonedClaim = Assert.Single(await firstStore.ClaimPendingAsync(
            "crashed-at-limit",
            1,
            TimeSpan.FromSeconds(30),
            1,
            CancellationToken.None));
        await ExpireClaimAsync(connectionString, envelope.EventId);

        Assert.Empty(await restartedStore.ClaimPendingAsync(
            "restart-after-limit",
            1,
            TimeSpan.FromSeconds(30),
            1,
            CancellationToken.None));
        Assert.Empty(await restartedStore.ClaimPendingAsync(
            "second-restart-after-limit",
            1,
            TimeSpan.FromSeconds(30),
            1,
            CancellationToken.None));
        Assert.False(await firstStore.MarkCompletedAsync(
            abandonedClaim,
            CreateExplanation(envelope.EventId, "stale crash result"),
            CancellationToken.None));

        var state = await ReadStateAsync(connectionString, envelope.EventId);
        Assert.Equal(1, state.AttemptCount);
        Assert.NotNull(state.FailedAt);
        Assert.Equal(
            AiExplanationFailureClassifier.AttemptLimitExhaustedCode,
            state.FailureCode);
        Assert.Null(state.CompletedAt);
        Assert.Null(state.Explanation);
    }

    [Fact]
    public async Task RetryableFailure_OnLastConfiguredAttempt_PersistsTerminalCause()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        var envelope = await AcceptEnvelopeAsync(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new PostgreSqlReleaseRiskExplanationStore(dataSource);
        using var processor = CreateProcessor(
            store,
            new ThrowingClient(new TimeoutException("Simulated timeout.")),
            maximumAttempts: 1);

        Assert.Equal(
            1,
            await processor.ProcessPendingBatchAsync(CancellationToken.None));

        var state = await ReadStateAsync(connectionString, envelope.EventId);
        Assert.Equal(1, state.AttemptCount);
        Assert.NotNull(state.FailedAt);
        Assert.Equal(
            AiExplanationFailureClassifier.RequestTimeoutCode,
            state.FailureCode);
        Assert.Contains("maximum of 1 attempts", state.FailureReason);
        Assert.Empty(await store.ClaimPendingAsync(
            "restart-after-terminal-timeout",
            1,
            TimeSpan.FromSeconds(30),
            1,
            CancellationToken.None));
    }

    [Fact]
    public async Task ExpiredOwner_CannotWriteTerminalFailureAfterReplacementSuccess()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        var envelope = await AcceptEnvelopeAsync(connectionString);
        await using var firstDataSource = NpgsqlDataSource.Create(connectionString);
        await using var replacementDataSource = NpgsqlDataSource.Create(
            connectionString);
        var firstStore = new PostgreSqlReleaseRiskExplanationStore(
            firstDataSource);
        var replacementStore = new PostgreSqlReleaseRiskExplanationStore(
            replacementDataSource);
        var staleClaim = Assert.Single(await firstStore.ClaimPendingAsync(
            "stale-owner",
            1,
            TimeSpan.FromSeconds(30),
            5,
            CancellationToken.None));
        await ExpireClaimAsync(connectionString, envelope.EventId);
        var replacementClaim = Assert.Single(
            await replacementStore.ClaimPendingAsync(
                "replacement-owner",
                1,
                TimeSpan.FromSeconds(30),
                5,
                CancellationToken.None));

        Assert.True(await replacementStore.MarkCompletedAsync(
            replacementClaim,
            CreateExplanation(envelope.EventId, "replacement success"),
            CancellationToken.None));
        Assert.False(await firstStore.MarkTerminalAsync(
            staleClaim,
            new ReleaseRiskExplanationTerminalFailure(
                AiExplanationFailureClassifier.UnexpectedErrorCode,
                "Stale owner failure."),
            CancellationToken.None));

        var state = await ReadStateAsync(connectionString, envelope.EventId);
        Assert.Equal("replacement success", state.Explanation?.Summary);
        Assert.Null(state.FailedAt);
        Assert.Equal(2, state.AttemptCount);
    }

    [Fact]
    public async Task FailedWorkView_RejectsMutation()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        var envelope = await AcceptEnvelopeAsync(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new PostgreSqlReleaseRiskExplanationStore(dataSource);
        var claim = Assert.Single(await store.ClaimPendingAsync(
            "failed-view-owner",
            1,
            TimeSpan.FromSeconds(30),
            5,
            CancellationToken.None));
        Assert.True(await store.MarkTerminalAsync(
            claim,
            new ReleaseRiskExplanationTerminalFailure(
                AiExplanationFailureClassifier.ResponseContractInvalidCode,
                "Terminal contract failure."),
            CancellationToken.None));

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE release_risk_ai_explanation_failed_work SET failure_reason = 'changed';",
            connection);

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, exception.SqlState);
    }

    [Fact]
    public async Task DatabaseConstraint_RejectsSuccessAndTerminalFailureTogether()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        var envelope = await AcceptEnvelopeAsync(connectionString);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE release_risk_event_inbox
            SET
                explanation_completed_at = clock_timestamp(),
                explanation = @explanation,
                explanation_failed_at = clock_timestamp(),
                explanation_failure_code = 'unexpected_error',
                explanation_failure_reason = 'Mutually exclusive outcome.'
            WHERE event_id = @event_id;
            """,
            connection);
        command.Parameters.AddWithValue("event_id", envelope.EventId);
        command.Parameters.AddWithValue(
            "explanation",
            NpgsqlDbType.Jsonb,
            System.Text.Json.JsonSerializer.Serialize(
                CreateExplanation(envelope.EventId, "invalid dual outcome"),
                new System.Text.Json.JsonSerializerOptions(
                    System.Text.Json.JsonSerializerDefaults.Web)));

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task VersionFourInbox_UpgradesThroughTerminalLifecycleAsPendingWork()
    {
        var connectionString = await _postgresql.CreateIsolatedDatabaseAsync();
        var envelope = CreateEnvelope();
        await ApplyVersionFourSchemaAsync(connectionString, envelope);

        using (var application = new PostgreSqlTestApplicationFactory(
                   connectionString,
                   applyMigrationsOnStartup: true))
        using (var client = application.CreateClient())
        using (var response = await client.GetAsync("/health"))
        {
            response.EnsureSuccessStatusCode();
        }

        var state = await ReadStateAsync(connectionString, envelope.EventId);
        Assert.Equal(0, state.AttemptCount);
        Assert.True(state.NextAttemptAt <= DateTimeOffset.UtcNow);
        Assert.Null(state.ClaimedBy);
        Assert.Null(state.CompletedAt);
        Assert.Null(state.Explanation);
        Assert.Equal(
            new[] { 1, 2, 3, 4, 5, 6 },
            await ReadMigrationVersionsAsync(connectionString));

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new PostgreSqlReleaseRiskExplanationStore(dataSource);
        var claim = Assert.Single(await store.ClaimPendingAsync(
            "processor-after-v6-upgrade",
            1,
            TimeSpan.FromSeconds(30),
            5,
            CancellationToken.None));
        Assert.Equal(envelope.EventId, claim.EventId);
        Assert.Equal(1, claim.AttemptCount);
    }

    [Fact]
    public async Task VersionFiveCompletedExplanation_UpgradesWithoutBecomingFailed()
    {
        var connectionString = await _postgresql.CreateIsolatedDatabaseAsync();
        var envelope = CreateEnvelope();
        await ApplyVersionFourSchemaAsync(connectionString, envelope);
        await ApplyVersionFiveCompletionAsync(connectionString, envelope);

        using (var application = new PostgreSqlTestApplicationFactory(
                   connectionString,
                   applyMigrationsOnStartup: true))
        using (var client = application.CreateClient())
        using (var response = await client.GetAsync("/health"))
        {
            response.EnsureSuccessStatusCode();
        }

        var state = await ReadStateAsync(connectionString, envelope.EventId);
        Assert.Equal("completed before v6", state.Explanation?.Summary);
        Assert.NotNull(state.CompletedAt);
        Assert.Null(state.FailedAt);
        Assert.Null(state.FailureCode);
        Assert.Null(state.FailureReason);
        Assert.Equal(
            new[] { 1, 2, 3, 4, 5, 6 },
            await ReadMigrationVersionsAsync(connectionString));
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

    private static async Task<ReleaseRiskOutboxEnvelope> AcceptEnvelopeAsync(
        string connectionString)
    {
        var envelope = CreateEnvelope();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new PostgreSqlReleaseRiskInboxStore(dataSource);
        Assert.Equal(
            ReleaseRiskInboxAcceptance.Accepted,
            await store.AcceptAsync(
                CreateConsumedEvent(envelope, offset: 0),
                CancellationToken.None));
        return envelope;
    }

    private static ConsumedReleaseRiskEvent CreateConsumedEvent(
        ReleaseRiskOutboxEnvelope envelope,
        long offset) =>
        new(
            "releaseguard.release-risk-assessed",
            0,
            offset,
            envelope.EventId,
            envelope.SerializeToUtf8Bytes(),
            envelope);

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

    private static ReleaseRiskExplanationProcessor CreateProcessor(
        IReleaseRiskExplanationStore store,
        IReleaseRiskExplanationClient client,
        int initialRetryDelayMilliseconds = 1_000,
        int maximumRetryDelayMilliseconds = 60_000,
        int maximumAttempts = 5) =>
        new(
            store,
            client,
            Options.Create(new AiExplanationProcessorOptions
            {
                Enabled = true,
                BatchSize = 10,
                PollIntervalMilliseconds = 100,
                LeaseDurationMilliseconds = 30_000,
                InitialRetryDelayMilliseconds = initialRetryDelayMilliseconds,
                MaximumRetryDelayMilliseconds = maximumRetryDelayMilliseconds,
                MaximumAttempts = maximumAttempts,
                StateUpdateTimeoutMilliseconds = 1_000
            }),
            NullLogger<ReleaseRiskExplanationProcessor>.Instance);

    private static async Task ExpireClaimAsync(
        string connectionString,
        Guid eventId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE release_risk_event_inbox
            SET explanation_claim_expires_at = clock_timestamp() - interval '1 second'
            WHERE event_id = @event_id;
            """,
            connection);
        command.Parameters.AddWithValue("event_id", eventId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task ApplyVersionFourSchemaAsync(
        string connectionString,
        ReleaseRiskOutboxEnvelope envelope)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var createMigrations = new NpgsqlCommand(
                         """
                         CREATE TABLE release_guard_schema_migrations (
                             version integer PRIMARY KEY,
                             description text NOT NULL,
                             applied_at timestamptz NOT NULL DEFAULT transaction_timestamp()
                         );
                         """,
                         connection,
                         transaction))
        {
            await createMigrations.ExecuteNonQueryAsync();
        }

        for (var version = 1; version <= 4; version++)
        {
            var migrationSql = await ReadMigrationResourceAsync(version);
            await using (var migration = new NpgsqlCommand(
                             migrationSql,
                             connection,
                             transaction))
            {
                await migration.ExecuteNonQueryAsync();
            }

            await using var record = new NpgsqlCommand(
                """
                INSERT INTO release_guard_schema_migrations (version, description)
                VALUES (@version, @description);
                """,
                connection,
                transaction);
            record.Parameters.AddWithValue("version", version);
            record.Parameters.AddWithValue(
                "description",
                $"test schema version {version}");
            await record.ExecuteNonQueryAsync();
        }

        await using (var insertInbox = new NpgsqlCommand(
                         """
                         INSERT INTO release_risk_event_inbox (
                             event_id,
                             message_key,
                             topic,
                             kafka_partition,
                             kafka_offset,
                             event_type,
                             schema_version,
                             source_provider,
                             event_kind,
                             payload,
                             envelope)
                         VALUES (
                             @event_id,
                             @event_id,
                             'releaseguard.release-risk-assessed',
                             0,
                             0,
                             @event_type,
                             @schema_version,
                             @source_provider,
                             @event_kind,
                             @payload,
                             @envelope);
                         """,
                         connection,
                         transaction))
        {
            insertInbox.Parameters.AddWithValue("event_id", envelope.EventId);
            insertInbox.Parameters.AddWithValue("event_type", envelope.EventType);
            insertInbox.Parameters.AddWithValue(
                "schema_version",
                envelope.SchemaVersion);
            insertInbox.Parameters.AddWithValue(
                "source_provider",
                envelope.SourceProvider);
            insertInbox.Parameters.AddWithValue("event_kind", envelope.Kind);
            insertInbox.Parameters.AddWithValue(
                "payload",
                NpgsqlDbType.Bytea,
                envelope.SerializeToUtf8Bytes());
            insertInbox.Parameters.AddWithValue(
                "envelope",
                NpgsqlDbType.Jsonb,
                envelope.Serialize());
            await insertInbox.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task<string> ReadMigrationResourceAsync(int version)
    {
        var assembly = typeof(Program).Assembly;
        await using var stream = assembly.GetManifestResourceStream(
            $"ReleaseGuard.Database.Migrations.V{version:000}.sql")
            ?? throw new InvalidOperationException(
                $"Migration resource V{version:000} was not found.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static async Task ApplyVersionFiveCompletionAsync(
        string connectionString,
        ReleaseRiskOutboxEnvelope envelope)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var migrationSql = await ReadMigrationResourceAsync(5);
        await using (var migration = new NpgsqlCommand(
                         migrationSql,
                         connection,
                         transaction))
        {
            await migration.ExecuteNonQueryAsync();
        }

        await using (var record = new NpgsqlCommand(
                         """
                         INSERT INTO release_guard_schema_migrations (
                             version,
                             description)
                         VALUES (5, 'test schema version 5');
                         """,
                         connection,
                         transaction))
        {
            await record.ExecuteNonQueryAsync();
        }

        await using (var complete = new NpgsqlCommand(
                         """
                         UPDATE release_risk_event_inbox
                         SET
                             explanation_completed_at = clock_timestamp(),
                             explanation = @explanation
                         WHERE event_id = @event_id;
                         """,
                         connection,
                         transaction))
        {
            complete.Parameters.AddWithValue("event_id", envelope.EventId);
            complete.Parameters.AddWithValue(
                "explanation",
                NpgsqlDbType.Jsonb,
                System.Text.Json.JsonSerializer.Serialize(
                    CreateExplanation(
                        envelope.EventId,
                        "completed before v6"),
                    new System.Text.Json.JsonSerializerOptions(
                        System.Text.Json.JsonSerializerDefaults.Web)));
            Assert.Equal(1, await complete.ExecuteNonQueryAsync());
        }

        await transaction.CommitAsync();
    }

    private static async Task<int[]> ReadMigrationVersionsAsync(
        string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT version
            FROM release_guard_schema_migrations
            ORDER BY version;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var versions = new List<int>();
        while (await reader.ReadAsync())
        {
            versions.Add(reader.GetInt32(0));
        }

        return [.. versions];
    }

    private static async Task<ExplanationState> ReadStateAsync(
        string connectionString,
        Guid eventId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                explanation_attempt_count,
                explanation_next_attempt_at,
                explanation_claimed_by,
                explanation_claim_expires_at,
                explanation_completed_at,
                explanation::text,
                envelope::text,
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
        var envelopeJson = reader.GetString(6);
        return new ExplanationState(
            reader.GetInt32(0),
            reader.GetFieldValue<DateTimeOffset>(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(4),
            reader.IsDBNull(5)
                ? null
                : System.Text.Json.JsonSerializer.Deserialize<
                    ReleaseRiskExplanation>(
                    reader.GetString(5),
                    new System.Text.Json.JsonSerializerOptions(
                        System.Text.Json.JsonSerializerDefaults.Web)),
            envelopeJson,
            ReleaseRiskOutboxEnvelope.Deserialize(envelopeJson),
            reader.IsDBNull(7)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));
    }

    private sealed record ExplanationState(
        int AttemptCount,
        DateTimeOffset NextAttemptAt,
        string? ClaimedBy,
        DateTimeOffset? ClaimExpiresAt,
        DateTimeOffset? CompletedAt,
        ReleaseRiskExplanation? Explanation,
        string EnvelopeJson,
        ReleaseRiskOutboxEnvelope Envelope,
        DateTimeOffset? FailedAt,
        string? FailureCode,
        string? FailureReason);

    private sealed class RecordingClient : IReleaseRiskExplanationClient
    {
        private readonly Guid _eventId;

        public RecordingClient(Guid eventId)
        {
            _eventId = eventId;
        }

        public ReleaseRiskOutboxEnvelope? ReceivedEnvelope { get; private set; }

        public Task<ReleaseRiskExplanation> ExplainAsync(
            ReleaseRiskOutboxEnvelope envelope,
            CancellationToken cancellationToken)
        {
            ReceivedEnvelope = envelope;
            return Task.FromResult(CreateExplanation(
                _eventId,
                "Risk snapshot explained."));
        }
    }

    private sealed class ThrowingClient : IReleaseRiskExplanationClient
    {
        private readonly Exception _exception;

        public ThrowingClient(Exception exception)
        {
            _exception = exception;
        }

        public Task<ReleaseRiskExplanation> ExplainAsync(
            ReleaseRiskOutboxEnvelope envelope,
            CancellationToken cancellationToken) =>
            Task.FromException<ReleaseRiskExplanation>(_exception);
    }

    private sealed class InvalidResultClient : IReleaseRiskExplanationClient
    {
        private readonly Guid _responseEventId;

        public InvalidResultClient(Guid responseEventId)
        {
            _responseEventId = responseEventId;
        }

        public Task<ReleaseRiskExplanation> ExplainAsync(
            ReleaseRiskOutboxEnvelope envelope,
            CancellationToken cancellationToken) =>
            Task.FromResult(CreateExplanation(
                _responseEventId,
                "Conflicting event result."));
    }

    private sealed class BlockingClient : IReleaseRiskExplanationClient
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ReleaseRiskExplanation> ExplainAsync(
            ReleaseRiskOutboxEnvelope envelope,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }
}
