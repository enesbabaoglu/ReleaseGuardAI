using Microsoft.Extensions.Options;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed class AiExplanationClientOptions
{
    public const string SectionName = "AiExplanationClient";
    public const int MinimumRequestTimeoutMilliseconds = 100;
    public const int MaximumRequestTimeoutMilliseconds = 60_000;

    public string BaseUrl { get; init; } = string.Empty;

    public int RequestTimeoutMilliseconds { get; init; } = 5_000;

    public static bool HasValidBaseUrl(AiExplanationClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return !string.IsNullOrWhiteSpace(options.BaseUrl) &&
               string.Equals(
                   options.BaseUrl,
                   options.BaseUrl.Trim(),
                   StringComparison.Ordinal) &&
               Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) &&
               (string.Equals(
                    uri.Scheme,
                    Uri.UriSchemeHttp,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    uri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase)) &&
               !string.IsNullOrWhiteSpace(uri.Host) &&
               string.IsNullOrEmpty(uri.UserInfo) &&
               string.IsNullOrEmpty(uri.Query) &&
               string.IsNullOrEmpty(uri.Fragment);
    }

    public static bool HasValidRequestTimeout(AiExplanationClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.RequestTimeoutMilliseconds is
            >= MinimumRequestTimeoutMilliseconds and
            <= MaximumRequestTimeoutMilliseconds;
    }

    public static void ThrowIfInvalid(AiExplanationClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (!HasValidBaseUrl(options))
        {
            failures.Add(
                $"{SectionName}:BaseUrl must be an absolute HTTP or HTTPS URL without credentials, query, or fragment.");
        }

        if (!HasValidRequestTimeout(options))
        {
            failures.Add(
                $"{SectionName}:RequestTimeoutMilliseconds must be between {MinimumRequestTimeoutMilliseconds} and {MaximumRequestTimeoutMilliseconds}.");
        }

        if (failures.Count > 0)
        {
            throw new OptionsValidationException(
                SectionName,
                typeof(AiExplanationClientOptions),
                failures);
        }
    }

    internal Uri GetNormalizedBaseUri() =>
        new($"{BaseUrl.TrimEnd('/')}/", UriKind.Absolute);
}
