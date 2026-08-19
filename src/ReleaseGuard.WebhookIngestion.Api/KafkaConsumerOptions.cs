using System.Text;
using Microsoft.Extensions.Options;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed class KafkaConsumerOptions
{
    public const string SectionName = "KafkaConsumer";
    public const int MinimumConsumeTimeoutMilliseconds = 100;
    public const int MaximumConsumeTimeoutMilliseconds = 60_000;
    public const int MinimumBrokerRequestTimeoutMilliseconds = 1_000;
    public const int MaximumBrokerRequestTimeoutMilliseconds = 300_000;
    public const int MaximumGroupIdUtf8Bytes = 255;

    public string BootstrapServers { get; init; } = string.Empty;

    public string Topic { get; init; } = string.Empty;

    public string GroupId { get; init; } = string.Empty;

    public string ClientId { get; init; } = "releaseguard-release-risk-consumer";

    public int ConsumeTimeoutMilliseconds { get; init; } = 5_000;

    public int BrokerRequestTimeoutMilliseconds { get; init; } = 5_000;

    public static bool HasValidBootstrapServers(KafkaConsumerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return KafkaProducerOptions.HasValidBootstrapServers(
            options.BootstrapServers);
    }

    public static bool HasValidTopic(KafkaConsumerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return KafkaProducerOptions.HasValidTopic(options.Topic);
    }

    public static bool HasValidGroupId(KafkaConsumerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return IsBoundedPrintableValue(
            options.GroupId,
            MaximumGroupIdUtf8Bytes);
    }

    public static bool HasValidClientId(KafkaConsumerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return IsBoundedPrintableValue(options.ClientId, 128);
    }

    public static bool HasValidConsumeTimeout(KafkaConsumerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.ConsumeTimeoutMilliseconds is
            >= MinimumConsumeTimeoutMilliseconds and
            <= MaximumConsumeTimeoutMilliseconds;
    }

    public static bool HasValidBrokerRequestTimeout(KafkaConsumerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.BrokerRequestTimeoutMilliseconds is
            >= MinimumBrokerRequestTimeoutMilliseconds and
            <= MaximumBrokerRequestTimeoutMilliseconds;
    }

    public static void ThrowIfInvalid(
        KafkaConsumerOptions options,
        KafkaProducerOptions producerOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(producerOptions);
        var failures = GetValidationFailures(options, producerOptions);

        if (failures.Count > 0)
        {
            throw new OptionsValidationException(
                SectionName,
                typeof(KafkaConsumerOptions),
                failures);
        }
    }

    internal static IReadOnlyList<string> GetValidationFailures(
        KafkaConsumerOptions options,
        KafkaProducerOptions producerOptions)
    {
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

        if (!HasValidGroupId(options))
        {
            failures.Add(
                $"{SectionName}:GroupId must be a printable value of at most {MaximumGroupIdUtf8Bytes} UTF-8 bytes.");
        }

        if (!HasValidClientId(options))
        {
            failures.Add(
                $"{SectionName}:ClientId must be a printable value of at most 128 UTF-8 bytes.");
        }

        if (!HasValidConsumeTimeout(options))
        {
            failures.Add(
                $"{SectionName}:ConsumeTimeoutMilliseconds must be between {MinimumConsumeTimeoutMilliseconds} and {MaximumConsumeTimeoutMilliseconds}.");
        }

        if (!HasValidBrokerRequestTimeout(options))
        {
            failures.Add(
                $"{SectionName}:BrokerRequestTimeoutMilliseconds must be between {MinimumBrokerRequestTimeoutMilliseconds} and {MaximumBrokerRequestTimeoutMilliseconds}.");
        }

        if (HasValidTopic(options) &&
            KafkaProducerOptions.HasValidTopic(producerOptions) &&
            !string.Equals(
                options.Topic,
                producerOptions.Topic,
                StringComparison.Ordinal))
        {
            failures.Add(
                $"{SectionName}:Topic must exactly match {KafkaProducerOptions.SectionName}:Topic.");
        }

        return failures;
    }

    private static bool IsBoundedPrintableValue(string value, int maximumBytes) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        Encoding.UTF8.GetByteCount(value) <= maximumBytes &&
        value.All(character => !char.IsControl(character));
}

public sealed class KafkaConsumerOptionsValidator :
    IValidateOptions<KafkaConsumerOptions>
{
    private readonly IOptions<KafkaProducerOptions> _producerOptions;

    public KafkaConsumerOptionsValidator(
        IOptions<KafkaProducerOptions> producerOptions)
    {
        _producerOptions = producerOptions;
    }

    public ValidateOptionsResult Validate(
        string? name,
        KafkaConsumerOptions options)
    {
        var failures = KafkaConsumerOptions.GetValidationFailures(
            options,
            _producerOptions.Value);
        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
