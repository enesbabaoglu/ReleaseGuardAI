using System.Text;
using Microsoft.Extensions.Options;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed class KafkaProducerOptions
{
    public const string SectionName = "Kafka";
    public const int MinimumTimeoutMilliseconds = 1_000;
    public const int MaximumTimeoutMilliseconds = 300_000;
    public const int MinimumRetries = 1;
    public const int MaximumAllowedRetries = 100;

    public string BootstrapServers { get; init; } = string.Empty;

    public string Topic { get; init; } = string.Empty;

    public string ClientId { get; init; } = "releaseguard-webhook-ingestion";

    public int DeliveryTimeoutMilliseconds { get; init; } = 10_000;

    public int RequestTimeoutMilliseconds { get; init; } = 5_000;

    public int MaximumRetries { get; init; } = 3;

    public static bool HasValidBootstrapServers(KafkaProducerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return HasValidBootstrapServers(options.BootstrapServers);
    }

    public static bool HasValidBootstrapServers(string bootstrapServers)
    {
        if (string.IsNullOrWhiteSpace(bootstrapServers))
        {
            return false;
        }

        var endpoints = bootstrapServers.Split(',');
        return endpoints.Length > 0 && endpoints.All(IsValidBootstrapEndpoint);
    }

    public static bool HasValidTopic(KafkaProducerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return HasValidTopic(options.Topic);
    }

    public static bool HasValidTopic(string topic)
    {
        return topic is not ("" or "." or "..") &&
               !string.IsNullOrWhiteSpace(topic) &&
               Encoding.UTF8.GetByteCount(topic) <= 249 &&
               topic.All(character =>
                   char.IsAsciiLetterOrDigit(character) ||
                   character is '.' or '_' or '-');
    }

    public static bool HasValidClientId(KafkaProducerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return !string.IsNullOrWhiteSpace(options.ClientId) &&
               options.ClientId.Length <= 128;
    }

    public static bool HasValidTimeouts(KafkaProducerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.DeliveryTimeoutMilliseconds is
                   >= MinimumTimeoutMilliseconds and <= MaximumTimeoutMilliseconds &&
               options.RequestTimeoutMilliseconds is
                   >= MinimumTimeoutMilliseconds and <= MaximumTimeoutMilliseconds &&
               options.RequestTimeoutMilliseconds <= options.DeliveryTimeoutMilliseconds;
    }

    public static bool HasValidRetryLimit(KafkaProducerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.MaximumRetries is >= MinimumRetries and <= MaximumAllowedRetries;
    }

    public static void ThrowIfInvalid(KafkaProducerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (!HasValidBootstrapServers(options))
        {
            failures.Add(
                $"{SectionName}:BootstrapServers must contain one or more host:port endpoints.");
        }

        if (!HasValidTopic(options))
        {
            failures.Add(
                $"{SectionName}:Topic must be a valid explicit Kafka topic name of at most 249 UTF-8 bytes.");
        }

        if (!HasValidClientId(options))
        {
            failures.Add(
                $"{SectionName}:ClientId must contain between 1 and 128 characters.");
        }

        if (!HasValidTimeouts(options))
        {
            failures.Add(
                $"{SectionName} timeouts must be between {MinimumTimeoutMilliseconds} and {MaximumTimeoutMilliseconds} milliseconds, and RequestTimeoutMilliseconds must not exceed DeliveryTimeoutMilliseconds.");
        }

        if (!HasValidRetryLimit(options))
        {
            failures.Add(
                $"{SectionName}:MaximumRetries must be between {MinimumRetries} and {MaximumAllowedRetries}.");
        }

        if (failures.Count > 0)
        {
            throw new OptionsValidationException(
                SectionName,
                typeof(KafkaProducerOptions),
                failures);
        }
    }

    private static bool IsValidBootstrapEndpoint(string endpoint)
    {
        var trimmedEndpoint = endpoint.Trim();
        if (trimmedEndpoint.Length == 0 ||
            !Uri.TryCreate($"tcp://{trimmedEndpoint}", UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.IsNullOrEmpty(uri.UserInfo) &&
               string.IsNullOrEmpty(uri.Query) &&
               string.IsNullOrEmpty(uri.Fragment) &&
               uri.AbsolutePath == "/" &&
               !string.IsNullOrWhiteSpace(uri.Host) &&
               uri.Port is >= 1 and <= 65_535;
    }
}
