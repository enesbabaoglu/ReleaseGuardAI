using Microsoft.Extensions.Options;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed class AiExplanationMetricsExportOptions
{
    public const string SectionName = "AiExplanationMetricsExport";
    public const string GrpcProtocol = "grpc";
    public const string HttpProtobufProtocol = "http/protobuf";
    public const int MinimumExportIntervalMilliseconds = 1_000;
    public const int MaximumExportIntervalMilliseconds = 300_000;
    public const int MinimumExportTimeoutMilliseconds = 100;
    public const int MaximumExportTimeoutMilliseconds = 30_000;

    public bool Enabled { get; init; }

    public string? Endpoint { get; init; }

    public string? Protocol { get; init; }

    public int ExportIntervalMilliseconds { get; init; } = 60_000;

    public int ExportTimeoutMilliseconds { get; init; } = 10_000;

    public static void ThrowIfInvalid(AiExplanationMetricsExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = GetValidationFailures(options);
        if (failures.Count > 0)
        {
            throw new OptionsValidationException(
                SectionName,
                typeof(AiExplanationMetricsExportOptions),
                failures);
        }
    }

    public static Uri GetEndpoint(AiExplanationMetricsExportOptions options)
    {
        ThrowIfInvalid(options);
        return new Uri(options.Endpoint!, UriKind.Absolute);
    }

    internal static IReadOnlyList<string> GetValidationFailures(
        AiExplanationMetricsExportOptions options)
    {
        var failures = new List<string>();

        if (options.ExportIntervalMilliseconds is
            < MinimumExportIntervalMilliseconds or
            > MaximumExportIntervalMilliseconds)
        {
            failures.Add(
                $"{SectionName}:ExportIntervalMilliseconds must be between {MinimumExportIntervalMilliseconds} and {MaximumExportIntervalMilliseconds}.");
        }

        if (options.ExportTimeoutMilliseconds is
            < MinimumExportTimeoutMilliseconds or
            > MaximumExportTimeoutMilliseconds)
        {
            failures.Add(
                $"{SectionName}:ExportTimeoutMilliseconds must be between {MinimumExportTimeoutMilliseconds} and {MaximumExportTimeoutMilliseconds}.");
        }

        if (options.ExportTimeoutMilliseconds > options.ExportIntervalMilliseconds)
        {
            failures.Add(
                $"{SectionName}:ExportTimeoutMilliseconds must not exceed ExportIntervalMilliseconds.");
        }

        if (!options.Enabled)
        {
            return failures;
        }

        if (!IsSupportedProtocol(options.Protocol))
        {
            failures.Add(
                $"{SectionName}:Protocol must be '{GrpcProtocol}' or '{HttpProtobufProtocol}' when export is enabled.");
        }

        if (!HasValidEndpoint(options.Endpoint))
        {
            failures.Add(
                $"{SectionName}:Endpoint must be an absolute HTTP or HTTPS URL without credentials, query, or fragment when export is enabled.");
        }

        if (string.Equals(
                options.Protocol,
                HttpProtobufProtocol,
                StringComparison.Ordinal) &&
            HasValidEndpoint(options.Endpoint) &&
            !new Uri(options.Endpoint!, UriKind.Absolute).AbsolutePath.EndsWith(
                "/v1/metrics",
                StringComparison.Ordinal))
        {
            failures.Add(
                $"{SectionName}:Endpoint must end with '/v1/metrics' for the '{HttpProtobufProtocol}' protocol.");
        }

        return failures;
    }

    private static bool IsSupportedProtocol(string? protocol) =>
        string.Equals(protocol, GrpcProtocol, StringComparison.Ordinal) ||
        string.Equals(protocol, HttpProtobufProtocol, StringComparison.Ordinal);

    private static bool HasValidEndpoint(string? endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return (uri.Scheme == Uri.UriSchemeHttp ||
                uri.Scheme == Uri.UriSchemeHttps) &&
               !string.IsNullOrWhiteSpace(uri.Host) &&
               string.IsNullOrEmpty(uri.UserInfo) &&
               string.IsNullOrEmpty(uri.Query) &&
               string.IsNullOrEmpty(uri.Fragment);
    }
}

public sealed class AiExplanationMetricsExportOptionsValidator :
    IValidateOptions<AiExplanationMetricsExportOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        AiExplanationMetricsExportOptions options)
    {
        var failures = AiExplanationMetricsExportOptions
            .GetValidationFailures(options);
        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
