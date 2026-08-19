using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class OutboxDispatcherIntegrationCollection :
    ICollectionFixture<PostgreSqlIntegrationFixture>,
    ICollectionFixture<KafkaIntegrationFixture>
{
    public const string CollectionName = "Outbox dispatcher integration";
}

[Collection(OutboxDispatcherIntegrationCollection.CollectionName)]
public sealed class PostgreSqlOutboxDispatcherIntegrationTests
{
    private readonly PostgreSqlIntegrationFixture _postgresql;
    private readonly KafkaIntegrationFixture _kafka;

    public PostgreSqlOutboxDispatcherIntegrationTests(
        PostgreSqlIntegrationFixture postgresql,
        KafkaIntegrationFixture kafka)
    {
        _postgresql = postgresql;
        _kafka = kafka;
    }

    [Fact]
    public async Task DispatchPendingBatch_PublishesToKafkaThenMarksOutboxPublished()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        var envelope = await InsertAcceptedOutboxAsync(connectionString);
        var topic = await _kafka.CreateTopicAsync();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new PostgreSqlReleaseRiskOutboxStore(dataSource);
        using var producer = CreateKafkaProducer(topic);
        using var dispatcher = CreateDispatcher(store, producer);

        var dispatched = await dispatcher.DispatchPendingBatchAsync(
            CancellationToken.None);

        Assert.Equal(1, dispatched);
        var state = await ReadOutboxStateAsync(connectionString, envelope.EventId);
        Assert.NotNull(state.PublishedAt);
        Assert.Equal(1, state.AttemptCount);
        Assert.Null(state.ClaimedBy);
        Assert.Null(state.ClaimExpiresAt);

        using var consumer = CreateReleaseRiskConsumer(topic);
        var consumed = consumer.Consume(CancellationToken.None);
        Assert.NotNull(consumed);
        Assert.Equal(topic, consumed.Topic);
        Assert.Equal(envelope.EventId, consumed.MessageKey);
        Assert.Equal(envelope.SerializeToUtf8Bytes(), consumed.Payload);
        Assert.Equal(envelope.Serialize(), consumed.Envelope.Serialize());
    }

    [Fact]
    public async Task ConcurrentStores_ClaimPendingEventOnlyOnce()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        await InsertAcceptedOutboxAsync(connectionString);
        await using var firstDataSource = NpgsqlDataSource.Create(connectionString);
        await using var secondDataSource = NpgsqlDataSource.Create(connectionString);
        var firstStore = new PostgreSqlReleaseRiskOutboxStore(firstDataSource);
        var secondStore = new PostgreSqlReleaseRiskOutboxStore(secondDataSource);

        var claims = await Task.WhenAll(
            firstStore.ClaimPendingAsync(
                "first-instance-claim",
                1,
                TimeSpan.FromSeconds(30),
                CancellationToken.None),
            secondStore.ClaimPendingAsync(
                "second-instance-claim",
                1,
                TimeSpan.FromSeconds(30),
                CancellationToken.None));

        Assert.Equal(1, claims.Sum(result => result.Count));
        Assert.Equal(1, claims.SelectMany(result => result).Single().AttemptCount);
    }

    [Fact]
    public async Task ExpiredLease_CanBeRecoveredByAnotherInstance()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        var envelope = await InsertAcceptedOutboxAsync(connectionString);
        await using var firstDataSource = NpgsqlDataSource.Create(connectionString);
        await using var secondDataSource = NpgsqlDataSource.Create(connectionString);
        var firstStore = new PostgreSqlReleaseRiskOutboxStore(firstDataSource);
        var secondStore = new PostgreSqlReleaseRiskOutboxStore(secondDataSource);
        var firstClaim = Assert.Single(await firstStore.ClaimPendingAsync(
            "stopped-process-claim",
            1,
            TimeSpan.FromSeconds(30),
            CancellationToken.None));

        Assert.Empty(await secondStore.ClaimPendingAsync(
            "replacement-process-before-expiry",
            1,
            TimeSpan.FromSeconds(30),
            CancellationToken.None));

        await ExpireClaimAsync(connectionString, envelope.EventId);
        var recoveredClaim = Assert.Single(await secondStore.ClaimPendingAsync(
            "replacement-process-after-expiry",
            1,
            TimeSpan.FromSeconds(30),
            CancellationToken.None));

        Assert.Equal(firstClaim.EventId, recoveredClaim.EventId);
        Assert.Equal(2, recoveredClaim.AttemptCount);
        Assert.False(await firstStore.MarkPublishedAsync(
            firstClaim,
            CancellationToken.None));
        Assert.True(await secondStore.MarkPublishedAsync(
            recoveredClaim,
            CancellationToken.None));
    }

    [Fact]
    public async Task BrokerFailure_ReleasesClaimAndSchedulesBoundedRetry()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        var envelope = await InsertAcceptedOutboxAsync(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new PostgreSqlReleaseRiskOutboxStore(dataSource);
        using var producer = CreateKafkaProducer(
            $"missing-topic-{Guid.NewGuid():N}",
            timeoutMilliseconds: 1_000);
        using var dispatcher = CreateDispatcher(
            store,
            producer,
            initialRetryDelayMilliseconds: 5_000,
            maximumRetryDelayMilliseconds: 5_000);

        var dispatched = await dispatcher.DispatchPendingBatchAsync(
            CancellationToken.None);

        Assert.Equal(1, dispatched);
        var state = await ReadOutboxStateAsync(connectionString, envelope.EventId);
        Assert.Null(state.PublishedAt);
        Assert.Equal(1, state.AttemptCount);
        Assert.Null(state.ClaimedBy);
        Assert.Null(state.ClaimExpiresAt);
        Assert.True(state.NextAttemptAt > DateTimeOffset.UtcNow.AddSeconds(2));
        Assert.Empty(await store.ClaimPendingAsync(
            "retry-too-early",
            1,
            TimeSpan.FromSeconds(30),
            CancellationToken.None));
    }

    [Fact]
    public async Task KafkaAckFollowedByStateUpdateFailure_IsRetriedAfterLeaseExpiry()
    {
        var connectionString = await CreateInitializedDatabaseAsync();
        var envelope = await InsertAcceptedOutboxAsync(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var backingStore = new PostgreSqlReleaseRiskOutboxStore(dataSource);
        var failingStore = new ThrowingMarkPublishedStore(backingStore);
        var producer = new RecordingProducer();
        using (var firstDispatcher = CreateDispatcher(failingStore, producer))
        {
            Assert.Equal(
                1,
                await firstDispatcher.DispatchPendingBatchAsync(
                    CancellationToken.None));
        }

        var uncertainState = await ReadOutboxStateAsync(
            connectionString,
            envelope.EventId);
        Assert.Null(uncertainState.PublishedAt);
        Assert.NotNull(uncertainState.ClaimedBy);
        Assert.Equal(1, producer.PublishCount);

        await ExpireClaimAsync(connectionString, envelope.EventId);
        using (var replacementDispatcher = CreateDispatcher(backingStore, producer))
        {
            Assert.Equal(
                1,
                await replacementDispatcher.DispatchPendingBatchAsync(
                    CancellationToken.None));
        }

        var recoveredState = await ReadOutboxStateAsync(
            connectionString,
            envelope.EventId);
        Assert.NotNull(recoveredState.PublishedAt);
        Assert.Equal(2, recoveredState.AttemptCount);
        Assert.Equal(2, producer.PublishCount);
    }

    [Fact]
    public async Task VersionTwoOutbox_UpgradesInPlaceWithPendingLifecycleDefaults()
    {
        var connectionString = await _postgresql.CreateIsolatedDatabaseAsync();
        var envelope = CreateEnvelope();
        await ApplyVersionTwoSchemaAsync(connectionString, envelope);

        using (var application = new PostgreSqlTestApplicationFactory(
                   connectionString,
                   applyMigrationsOnStartup: true))
        using (var client = application.CreateClient())
        using (var response = await client.GetAsync("/health"))
        {
            response.EnsureSuccessStatusCode();
        }

        var state = await ReadOutboxStateAsync(connectionString, envelope.EventId);
        Assert.Null(state.PublishedAt);
        Assert.Equal(0, state.AttemptCount);
        Assert.Null(state.ClaimedBy);
        Assert.Null(state.ClaimExpiresAt);
        Assert.True(state.NextAttemptAt <= DateTimeOffset.UtcNow);
        Assert.Equal(
            new[] { 1, 2, 3, 4, 5 },
            await ReadMigrationVersionsAsync(connectionString));
        Assert.Equal(0, await CountInboxEventsAsync(connectionString));
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

    private static async Task<ReleaseRiskOutboxEnvelope> InsertAcceptedOutboxAsync(
        string connectionString)
    {
        var envelope = CreateEnvelope();
        using var payload = JsonDocument.Parse("""{"action":"opened"}""");
        var webhook = new VerifiedGitHubWebhook(
            envelope.EventId,
            "pull_request",
            payload.RootElement.Clone());
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var deliveryStore = new PostgreSqlGitHubWebhookDeliveryStore(dataSource);

        Assert.True(await deliveryStore.TryAcceptAsync(
            webhook,
            envelope.RiskInput,
            envelope.RiskAssessment,
            CancellationToken.None));
        return envelope;
    }

    private KafkaReleaseRiskEventProducer CreateKafkaProducer(
        string topic,
        int timeoutMilliseconds = 5_000) =>
        new(Options.Create(new KafkaProducerOptions
        {
            BootstrapServers = _kafka.BootstrapServers,
            Topic = topic,
            ClientId = $"releaseguard-dispatcher-test-{Guid.NewGuid():N}",
            DeliveryTimeoutMilliseconds = timeoutMilliseconds,
            RequestTimeoutMilliseconds = Math.Min(timeoutMilliseconds, 1_000),
            MaximumRetries = 2
        }));

    private static ReleaseRiskOutboxDispatcher CreateDispatcher(
        IReleaseRiskOutboxStore store,
        IReleaseRiskEventProducer producer,
        int initialRetryDelayMilliseconds = 1_000,
        int maximumRetryDelayMilliseconds = 5_000) =>
        new(
            store,
            producer,
            Options.Create(new OutboxDispatcherOptions
            {
                Enabled = true,
                BatchSize = 10,
                PollIntervalMilliseconds = 100,
                LeaseDurationMilliseconds = 10_000,
                InitialRetryDelayMilliseconds = initialRetryDelayMilliseconds,
                MaximumRetryDelayMilliseconds = maximumRetryDelayMilliseconds,
                StateUpdateTimeoutMilliseconds = 1_000
            }),
            NullLogger<ReleaseRiskOutboxDispatcher>.Instance);

    private KafkaReleaseRiskEventConsumer CreateReleaseRiskConsumer(
        string topic) =>
        new(
            Options.Create(new KafkaConsumerOptions
            {
                BootstrapServers = _kafka.BootstrapServers,
                Topic = topic,
                GroupId = $"releaseguard-dispatcher-test-{Guid.NewGuid():N}",
                ClientId = $"releaseguard-dispatcher-consumer-{Guid.NewGuid():N}",
                ConsumeTimeoutMilliseconds = 10_000
            }),
            Options.Create(new KafkaProducerOptions
            {
                BootstrapServers = _kafka.BootstrapServers,
                Topic = topic
            }));

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

    private static async Task ExpireClaimAsync(
        string connectionString,
        Guid eventId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE release_risk_outbox_messages
            SET claim_expires_at = clock_timestamp() - interval '1 second'
            WHERE event_id = @event_id;
            """,
            connection);
        command.Parameters.AddWithValue("event_id", eventId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<OutboxState> ReadOutboxStateAsync(
        string connectionString,
        Guid eventId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                published_at,
                attempt_count,
                next_attempt_at,
                claimed_by,
                claim_expires_at
            FROM release_risk_outbox_messages
            WHERE event_id = @event_id;
            """,
            connection);
        command.Parameters.AddWithValue("event_id", eventId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new OutboxState(
            reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0),
            reader.GetInt32(1),
            reader.GetFieldValue<DateTimeOffset>(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4));
    }

    private static async Task ApplyVersionTwoSchemaAsync(
        string connectionString,
        ReleaseRiskOutboxEnvelope envelope)
    {
        var versionOneSql = await ReadMigrationResourceAsync(
            "ReleaseGuard.Database.Migrations.V001.sql");
        var versionTwoSql = await ReadMigrationResourceAsync(
            "ReleaseGuard.Database.Migrations.V002.sql");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var command = new NpgsqlCommand(
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
            await command.ExecuteNonQueryAsync();
        }

        foreach (var sql in new[] { versionOneSql, versionTwoSql })
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync();
        }

        await using (var command = new NpgsqlCommand(
                         """
                         INSERT INTO release_guard_schema_migrations (version, description)
                         VALUES
                             (1, 'create GitHub webhook deliveries'),
                             (2, 'create release risk outbox');

                         INSERT INTO github_webhook_deliveries (
                             delivery_id,
                             event_name,
                             payload,
                             disposition,
                             risk_input,
                             risk_assessment)
                         VALUES (
                             @event_id,
                             'pull_request',
                             '{"action":"opened"}'::jsonb,
                             'accepted',
                             @risk_input,
                             @risk_assessment);

                         INSERT INTO release_risk_outbox_messages (
                             event_id,
                             event_type,
                             schema_version,
                             source_provider,
                             event_kind,
                             envelope)
                         VALUES (
                             @event_id,
                             @event_type,
                             @schema_version,
                             @source_provider,
                             @event_kind,
                             @envelope);
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("event_id", envelope.EventId);
            command.Parameters.AddWithValue("event_type", envelope.EventType);
            command.Parameters.AddWithValue("schema_version", envelope.SchemaVersion);
            command.Parameters.AddWithValue("source_provider", envelope.SourceProvider);
            command.Parameters.AddWithValue("event_kind", envelope.Kind);
            command.Parameters.AddWithValue(
                "risk_input",
                NpgsqlDbType.Jsonb,
                JsonSerializer.Serialize(
                    envelope.RiskInput,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            command.Parameters.AddWithValue(
                "risk_assessment",
                NpgsqlDbType.Jsonb,
                JsonSerializer.Serialize(
                    envelope.RiskAssessment,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            command.Parameters.AddWithValue(
                "envelope",
                NpgsqlDbType.Jsonb,
                envelope.Serialize());
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task<string> ReadMigrationResourceAsync(string resourceName)
    {
        await using var stream = typeof(Program).Assembly
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded migration '{resourceName}' was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static async Task<int[]> ReadMigrationVersionsAsync(
        string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT version FROM release_guard_schema_migrations ORDER BY version;",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var versions = new List<int>();
        while (await reader.ReadAsync())
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions.ToArray();
    }

    private static async Task<long> CountInboxEventsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM release_risk_event_inbox;",
            connection);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private sealed record OutboxState(
        DateTimeOffset? PublishedAt,
        int AttemptCount,
        DateTimeOffset NextAttemptAt,
        string? ClaimedBy,
        DateTimeOffset? ClaimExpiresAt);

    private sealed class RecordingProducer : IReleaseRiskEventProducer
    {
        public int PublishCount { get; private set; }

        public Task PublishAsync(
            ReleaseRiskOutboxEnvelope envelope,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PublishCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingMarkPublishedStore : IReleaseRiskOutboxStore
    {
        private readonly IReleaseRiskOutboxStore _inner;

        public ThrowingMarkPublishedStore(IReleaseRiskOutboxStore inner)
        {
            _inner = inner;
        }

        public Task<IReadOnlyList<ReleaseRiskOutboxClaim>> ClaimPendingAsync(
            string claimOwner,
            int batchSize,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) =>
            _inner.ClaimPendingAsync(
                claimOwner,
                batchSize,
                leaseDuration,
                cancellationToken);

        public Task<bool> MarkPublishedAsync(
            ReleaseRiskOutboxClaim claim,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Forced post-ack state update failure.");

        public Task<bool> MarkFailedAsync(
            ReleaseRiskOutboxClaim claim,
            TimeSpan retryDelay,
            CancellationToken cancellationToken) =>
            _inner.MarkFailedAsync(claim, retryDelay, cancellationToken);

        public Task<bool> ReleaseClaimAsync(
            ReleaseRiskOutboxClaim claim,
            CancellationToken cancellationToken) =>
            _inner.ReleaseClaimAsync(claim, cancellationToken);
    }
}
