using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class AiExplanationQueryMetricsTests
{
    [Fact]
    public void Instruments_EmitOnlyBoundedOutcomeTags()
    {
        var measurements = new ConcurrentQueue<RecordedMeasurement>();
        var instruments = new ConcurrentQueue<Instrument>();
        using var meterFactory = new TestMeterFactory();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, activeListener) =>
        {
            if (ReferenceEquals(instrument.Meter, meterFactory.CreatedMeter))
            {
                instruments.Enqueue(instrument);
                activeListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, measurement, tags, _) =>
                measurements.Enqueue(
                    RecordedMeasurement.Create(
                        instrument,
                        measurement,
                        tags)));
        listener.SetMeasurementEventCallback<double>(
            (instrument, measurement, tags, _) =>
                measurements.Enqueue(
                    RecordedMeasurement.Create(
                        instrument,
                        measurement,
                        tags)));
        listener.Start();
        var metrics = new AiExplanationQueryMetrics(meterFactory);

        metrics.RecordAuthenticationFailure();
        metrics.RecordRateLimitPermit();
        metrics.RecordRateLimitRejection();
        metrics.RecordOutcome(AiExplanationQueryOutcome.Pending);
        metrics.RecordOutcome(AiExplanationQueryOutcome.Completed);
        metrics.RecordOutcome(AiExplanationQueryOutcome.Failed);
        metrics.RecordOutcome(AiExplanationQueryOutcome.NotFound);
        metrics.RecordOutcome(AiExplanationQueryOutcome.Timeout);
        metrics.RecordDatabaseReadDuration(TimeSpan.FromMilliseconds(12.5));

        var published = instruments.ToDictionary(
            instrument => instrument.Name,
            StringComparer.Ordinal);
        Assert.Equal(5, published.Count);
        Assert.All(
            published.Values,
            instrument => Assert.Equal(
                AiExplanationQueryMetrics.MeterName,
                instrument.Meter.Name));
        Assert.IsType<Counter<long>>(
            published[AiExplanationQueryMetrics
                .AuthenticationFailuresInstrumentName]);
        Assert.IsType<Counter<long>>(
            published[AiExplanationQueryMetrics.RateLimitPermitsInstrumentName]);
        Assert.IsType<Counter<long>>(
            published[AiExplanationQueryMetrics
                .RateLimitRejectionsInstrumentName]);
        Assert.IsType<Counter<long>>(
            published[AiExplanationQueryMetrics.OutcomesInstrumentName]);
        Assert.IsType<Histogram<double>>(
            published[AiExplanationQueryMetrics
                .DatabaseReadDurationInstrumentName]);
        Assert.All(
            published.Values.Where(
                instrument => instrument.Name !=
                    AiExplanationQueryMetrics.DatabaseReadDurationInstrumentName),
            instrument => Assert.Equal("{request}", instrument.Unit));
        Assert.Equal(
            "ms",
            published[AiExplanationQueryMetrics
                .DatabaseReadDurationInstrumentName].Unit);

        var recorded = measurements.ToArray();
        Assert.Equal(
            [
                AiExplanationQueryMetrics.AuthenticationFailuresInstrumentName,
                AiExplanationQueryMetrics.DatabaseReadDurationInstrumentName,
                AiExplanationQueryMetrics.OutcomesInstrumentName,
                AiExplanationQueryMetrics.RateLimitPermitsInstrumentName,
                AiExplanationQueryMetrics.RateLimitRejectionsInstrumentName
            ],
            recorded.Select(measurement => measurement.InstrumentName)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());

        var untagged = recorded.Where(
            measurement => measurement.InstrumentName !=
                AiExplanationQueryMetrics.OutcomesInstrumentName);
        Assert.All(untagged, measurement => Assert.Empty(measurement.Tags));

        var outcomes = recorded
            .Where(measurement => measurement.InstrumentName ==
                AiExplanationQueryMetrics.OutcomesInstrumentName)
            .Select(
                measurement =>
                {
                    var tag = Assert.Single(measurement.Tags);
                    Assert.Equal(AiExplanationQueryMetrics.OutcomeTagName, tag.Key);
                    return Assert.IsType<string>(tag.Value);
                })
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            ["completed", "failed", "not_found", "pending", "timeout"],
            outcomes);
        Assert.All(
            recorded.Where(
                measurement => measurement.InstrumentName !=
                    AiExplanationQueryMetrics.DatabaseReadDurationInstrumentName),
            measurement => Assert.Equal(1, measurement.Value));
        Assert.Contains(
            recorded,
            measurement =>
                measurement.InstrumentName ==
                    AiExplanationQueryMetrics.DatabaseReadDurationInstrumentName &&
                measurement.Value == 12.5);
    }

    [Fact]
    public void Record_RejectsUnknownOutcomeAndNegativeDuration()
    {
        using var meterFactory = new TestMeterFactory();
        var metrics = new AiExplanationQueryMetrics(meterFactory);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => metrics.RecordOutcome((AiExplanationQueryOutcome)int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => metrics.RecordDatabaseReadDuration(
                TimeSpan.FromMilliseconds(-1)));
    }

    private sealed class TestMeterFactory : IMeterFactory, IDisposable
    {
        public Meter? CreatedMeter { get; private set; }

        public Meter Create(MeterOptions options)
        {
            Assert.Equal(AiExplanationQueryMetrics.MeterName, options.Name);
            return CreatedMeter ??= new Meter(options);
        }

        public void Dispose() => CreatedMeter?.Dispose();
    }

    private sealed record RecordedMeasurement(
        string InstrumentName,
        double Value,
        IReadOnlyList<KeyValuePair<string, object?>> Tags)
    {
        public static RecordedMeasurement Create<T>(
            Instrument instrument,
            T value,
            ReadOnlySpan<KeyValuePair<string, object?>> tags)
            where T : struct,
                IConvertible =>
            new(
                instrument.Name,
                value.ToDouble(provider: null),
                tags.ToArray());
    }
}
