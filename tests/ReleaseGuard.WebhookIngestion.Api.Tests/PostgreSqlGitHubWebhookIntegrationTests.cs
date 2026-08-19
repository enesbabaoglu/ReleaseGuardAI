using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

[Collection(PostgreSqlIntegrationCollection.CollectionName)]
public sealed class PostgreSqlGitHubWebhookIntegrationTests
{
    private static readonly byte[] OpenedPayload = CreatePullRequestPayload();

    private readonly PostgreSqlIntegrationFixture _postgresql;

    public PostgreSqlGitHubWebhookIntegrationTests(
        PostgreSqlIntegrationFixture postgresql)
    {
        _postgresql = postgresql;
    }

    [Fact]
    public async Task AcceptedDelivery_PersistsAllSnapshots_AndSurvivesRestart()
    {
        var connectionString = await _postgresql.CreateIsolatedDatabaseAsync();
        var deliveryId = Guid.NewGuid();

        using (var firstApplication = new PostgreSqlTestApplicationFactory(
                   connectionString,
                   applyMigrationsOnStartup: true))
        using (var firstClient = firstApplication.CreateClient())
        using (var firstRequest = CreateRequest(OpenedPayload, deliveryId))
        using (var firstResponse = await firstClient.SendAsync(firstRequest))
        {
            Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
        }

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                """
                SELECT
                    event_name,
                    payload ->> 'action',
                    disposition,
                    risk_input ->> 'kind',
                    (risk_assessment ->> 'score')::integer,
                    risk_assessment -> 'factors' -> 0 ->> 'code'
                FROM github_webhook_deliveries
                WHERE delivery_id = @delivery_id;
                """,
                connection);
            command.Parameters.AddWithValue("delivery_id", deliveryId);

            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("pull_request", reader.GetString(0));
            Assert.Equal(GitHubRiskInputMapper.OpenedAction, reader.GetString(1));
            Assert.Equal("accepted", reader.GetString(2));
            Assert.Equal(GitHubRiskInputMapper.ChangeOpenedKind, reader.GetString(3));
            Assert.Equal(20, reader.GetInt32(4));
            Assert.Equal("primary_target_branch", reader.GetString(5));
            Assert.False(await reader.ReadAsync());
        }

        using var restartedApplication = new PostgreSqlTestApplicationFactory(
            connectionString,
            applyMigrationsOnStartup: false);
        using var restartedClient = restartedApplication.CreateClient();
        using var repeatedRequest = CreateRequest(OpenedPayload, deliveryId);
        using var repeatedResponse = await restartedClient.SendAsync(repeatedRequest);
        var receipt = await repeatedResponse.Content
            .ReadFromJsonAsync<GitHubWebhookReceipt>();

        Assert.Equal(HttpStatusCode.OK, repeatedResponse.StatusCode);
        Assert.NotNull(receipt);
        Assert.Equal("duplicate", receipt.Status);
        Assert.Equal(1, await CountDeliveriesAsync(connectionString));
    }

    [Fact]
    public async Task ConcurrentInstances_RelyOnUniqueConstraint_ForSingleAcceptance()
    {
        var connectionString = await _postgresql.CreateIsolatedDatabaseAsync();
        var deliveryId = Guid.NewGuid();
        using var firstApplication = new PostgreSqlTestApplicationFactory(
            connectionString,
            applyMigrationsOnStartup: true);
        using var secondApplication = new PostgreSqlTestApplicationFactory(
            connectionString,
            applyMigrationsOnStartup: false);
        using var firstClient = firstApplication.CreateClient();
        using var secondClient = secondApplication.CreateClient();

        var responseTasks = Enumerable.Range(0, 20)
            .Select(index => SendAsync(
                index % 2 == 0 ? firstClient : secondClient,
                OpenedPayload,
                deliveryId));
        var statuses = await Task.WhenAll(responseTasks);

        Assert.Equal(1, statuses.Count(status => status == HttpStatusCode.Accepted));
        Assert.Equal(19, statuses.Count(status => status == HttpStatusCode.OK));
        Assert.Equal(1, await CountDeliveriesAsync(connectionString));
    }

    [Fact]
    public async Task IgnoredDelivery_IsPersisted_AndBecomesDuplicateAfterRestart()
    {
        var connectionString = await _postgresql.CreateIsolatedDatabaseAsync();
        var deliveryId = Guid.NewGuid();
        var ignoredPayload = Encoding.UTF8.GetBytes("""{"action":"closed"}""");

        using (var firstApplication = new PostgreSqlTestApplicationFactory(
                   connectionString,
                   applyMigrationsOnStartup: true))
        using (var firstClient = firstApplication.CreateClient())
        using (var firstRequest = CreateRequest(ignoredPayload, deliveryId))
        using (var firstResponse = await firstClient.SendAsync(firstRequest))
        {
            var receipt = await firstResponse.Content
                .ReadFromJsonAsync<GitHubWebhookReceipt>();
            Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
            Assert.Equal("ignored", receipt?.Status);
        }

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                """
                SELECT disposition, risk_input IS NULL, risk_assessment IS NULL
                FROM github_webhook_deliveries
                WHERE delivery_id = @delivery_id;
                """,
                connection);
            command.Parameters.AddWithValue("delivery_id", deliveryId);

            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("ignored", reader.GetString(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
        }

        using var restartedApplication = new PostgreSqlTestApplicationFactory(
            connectionString,
            applyMigrationsOnStartup: false);
        using var restartedClient = restartedApplication.CreateClient();
        using var repeatedRequest = CreateRequest(ignoredPayload, deliveryId);
        using var repeatedResponse = await restartedClient.SendAsync(repeatedRequest);
        var repeatedReceipt = await repeatedResponse.Content
            .ReadFromJsonAsync<GitHubWebhookReceipt>();

        Assert.Equal(HttpStatusCode.OK, repeatedResponse.StatusCode);
        Assert.Equal("duplicate", repeatedReceipt?.Status);
        Assert.Equal(1, await CountDeliveriesAsync(connectionString));
    }

    [Fact]
    public async Task InvalidRequests_CreateNoDatabaseRecords()
    {
        var connectionString = await _postgresql.CreateIsolatedDatabaseAsync();
        using var application = new PostgreSqlTestApplicationFactory(
            connectionString,
            applyMigrationsOnStartup: true);
        using var client = application.CreateClient();

        var invalidSignatureId = Guid.NewGuid();
        using (var request = CreateRequest(
                   OpenedPayload,
                   invalidSignatureId,
                   signature: $"sha256={new string('0', 64)}"))
        using (var response = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (var request = CreateRequest(
                   OpenedPayload,
                   Guid.NewGuid(),
                   includeDeliveryHeader: false))
        using (var response = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using (var request = CreateRequest(
                   OpenedPayload,
                   Guid.NewGuid(),
                   includeEventHeader: false))
        using (var response = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var malformedJson = Encoding.UTF8.GetBytes("{not-json}");
        using (var request = CreateRequest(malformedJson, Guid.NewGuid()))
        using (var response = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var invalidSupportedDeliveryId = Guid.NewGuid();
        var invalidSupportedPayload = Encoding.UTF8.GetBytes(
            """{"action":"opened","number":42}""");
        using (var request = CreateRequest(
                   invalidSupportedPayload,
                   invalidSupportedDeliveryId))
        using (var response = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Equal(0, await CountDeliveriesAsync(connectionString));

        using var validRetry = CreateRequest(OpenedPayload, invalidSupportedDeliveryId);
        using var validResponse = await client.SendAsync(validRetry);

        Assert.Equal(HttpStatusCode.Accepted, validResponse.StatusCode);
        Assert.Equal(1, await CountDeliveriesAsync(connectionString));
    }

    [Fact]
    public async Task StartupWithoutAppliedMigration_FailsFast()
    {
        var connectionString = await _postgresql.CreateIsolatedDatabaseAsync();
        using var application = new PostgreSqlTestApplicationFactory(
            connectionString,
            applyMigrationsOnStartup: false);

        var exception = Assert.Throws<InvalidOperationException>(
            () => application.CreateClient());

        Assert.Contains(
            "PostgreSQL schema initialization failed",
            exception.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(0, await CountUserTablesAsync(connectionString));
    }

    private static async Task<HttpStatusCode> SendAsync(
        HttpClient client,
        byte[] payload,
        Guid deliveryId)
    {
        using var request = CreateRequest(payload, deliveryId);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static async Task<long> CountDeliveriesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM github_webhook_deliveries;",
            connection);

        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<long> CountUserTablesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT COUNT(*)
            FROM information_schema.tables
            WHERE table_schema = 'public';
            """,
            connection);

        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static byte[] CreatePullRequestPayload() =>
        Encoding.UTF8.GetBytes(
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

    private static HttpRequestMessage CreateRequest(
        byte[] payload,
        Guid deliveryId,
        string? signature = null,
        bool includeDeliveryHeader = true,
        bool includeEventHeader = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, GitHubWebhookEndpoint.Route)
        {
            Content = new ByteArrayContent(payload)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add(
            GitHubWebhookSignatureValidator.SignatureHeaderName,
            signature ?? CreateSignature(payload));

        if (includeDeliveryHeader)
        {
            request.Headers.Add(
                GitHubWebhookEndpoint.DeliveryHeaderName,
                deliveryId.ToString());
        }

        if (includeEventHeader)
        {
            request.Headers.Add(GitHubWebhookEndpoint.EventHeaderName, "pull_request");
        }

        return request;
    }

    private static string CreateSignature(byte[] payload)
    {
        var secret = Encoding.UTF8.GetBytes(
            TestApplicationFactory.GitHubWebhookSecret);
        var digest = HMACSHA256.HashData(secret, payload);

        return $"sha256={Convert.ToHexString(digest).ToLowerInvariant()}";
    }
}
