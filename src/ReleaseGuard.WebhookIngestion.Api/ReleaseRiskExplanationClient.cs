using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace ReleaseGuard.WebhookIngestion.Api;

public interface IReleaseRiskExplanationClient
{
    Task<ReleaseRiskExplanation> ExplainAsync(
        ReleaseRiskOutboxEnvelope envelope,
        CancellationToken cancellationToken);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReleaseRiskExplanation
{
    [JsonRequired]
    public required Guid EventId { get; init; }

    [JsonRequired]
    public required string Summary { get; init; }

    [JsonRequired]
    public required IReadOnlyList<string> Recommendations { get; init; }
}

public class ReleaseRiskExplanationContractException : Exception
{
    public ReleaseRiskExplanationContractException(string message)
        : base(message)
    {
    }

    public ReleaseRiskExplanationContractException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class ReleaseRiskExplanationEventIdConflictException :
    ReleaseRiskExplanationContractException
{
    public ReleaseRiskExplanationEventIdConflictException(
        Guid requestEventId,
        Guid responseEventId)
        : base(
            $"The explanation response event ID '{responseEventId:D}' does not match request event ID '{requestEventId:D}'.")
    {
        RequestEventId = requestEventId;
        ResponseEventId = responseEventId;
    }

    public Guid RequestEventId { get; }

    public Guid ResponseEventId { get; }
}

public sealed class HttpReleaseRiskExplanationClient :
    IReleaseRiskExplanationClient
{
    public const string EndpointPath = "v1/release-risk-explanations";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly Uri _endpointUri;
    private readonly TimeSpan _requestTimeout;

    public HttpReleaseRiskExplanationClient(
        HttpClient httpClient,
        IOptions<AiExplanationClientOptions> options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        AiExplanationClientOptions.ThrowIfInvalid(options.Value);

        _httpClient = httpClient;
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _endpointUri = new Uri(
            options.Value.GetNormalizedBaseUri(),
            EndpointPath);
        _requestTimeout = TimeSpan.FromMilliseconds(
            options.Value.RequestTimeoutMilliseconds);
    }

    public async Task<ReleaseRiskExplanation> ExplainAsync(
        ReleaseRiskOutboxEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();

        if (!envelope.IsValidVersionOneContract())
        {
            throw new ArgumentException(
                "The envelope must be a valid V1 release-risk contract.",
                nameof(envelope));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpointUri)
        {
            Content = new ByteArrayContent(envelope.SerializeToUtf8Bytes())
        };
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue(
                "application/json")
            {
                CharSet = "utf-8"
            };

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(_requestTimeout);

        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token);
            response.EnsureSuccessStatusCode();

            ReleaseRiskExplanation explanation;
            try
            {
                explanation = await response.Content.ReadFromJsonAsync<
                        ReleaseRiskExplanation>(
                        JsonOptions,
                        timeoutSource.Token)
                    ?? throw new JsonException(
                        "The explanation response deserialized to null.");
            }
            catch (JsonException exception)
            {
                throw new ReleaseRiskExplanationContractException(
                    "The explanation response is not a valid V1 contract.",
                    exception);
            }

            ValidateResponse(explanation, envelope.EventId);
            return explanation;
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested &&
                  timeoutSource.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The AI explanation request exceeded the configured {_requestTimeout.TotalMilliseconds:0} ms timeout.",
                exception);
        }
    }

    private static void ValidateResponse(
        ReleaseRiskExplanation explanation,
        Guid requestEventId)
    {
        if (explanation.EventId != requestEventId)
        {
            throw new ReleaseRiskExplanationEventIdConflictException(
                requestEventId,
                explanation.EventId);
        }

        if (string.IsNullOrWhiteSpace(explanation.Summary) ||
            explanation.Recommendations is not { Count: > 0 } ||
            explanation.Recommendations.Any(string.IsNullOrWhiteSpace))
        {
            throw new ReleaseRiskExplanationContractException(
                "The explanation response must contain a non-empty summary and at least one non-empty recommendation.");
        }
    }
}
