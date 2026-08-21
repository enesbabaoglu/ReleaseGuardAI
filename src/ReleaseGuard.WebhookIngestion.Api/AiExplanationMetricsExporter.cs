using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed class AiExplanationMetricsExporter : IHostedService, IDisposable
{
    private readonly AiExplanationMetricsExportOptions _options;
    private MeterProvider? _meterProvider;

    public AiExplanationMetricsExporter(
        IOptions<AiExplanationMetricsExportOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        AiExplanationMetricsExportOptions.ThrowIfInvalid(_options);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled)
        {
            return Task.CompletedTask;
        }

        _meterProvider = Sdk
            .CreateMeterProviderBuilder()
            .AddMeter(AiExplanationQueryMetrics.MeterName)
            .AddOtlpExporter((exporter, reader) =>
            {
                exporter.Endpoint = AiExplanationMetricsExportOptions
                    .GetEndpoint(_options);
                exporter.Protocol = string.Equals(
                    _options.Protocol,
                    AiExplanationMetricsExportOptions.GrpcProtocol,
                    StringComparison.Ordinal)
                    ? OtlpExportProtocol.Grpc
                    : OtlpExportProtocol.HttpProtobuf;
                exporter.TimeoutMilliseconds =
                    _options.ExportTimeoutMilliseconds;
                reader.PeriodicExportingMetricReaderOptions
                        .ExportIntervalMilliseconds =
                    _options.ExportIntervalMilliseconds;
                reader.PeriodicExportingMetricReaderOptions
                        .ExportTimeoutMilliseconds =
                    _options.ExportTimeoutMilliseconds;
            })
            .Build();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _meterProvider?.Dispose();
        _meterProvider = null;
    }
}
