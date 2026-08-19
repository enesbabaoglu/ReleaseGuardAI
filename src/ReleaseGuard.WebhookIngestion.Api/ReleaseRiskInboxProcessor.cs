using Microsoft.Extensions.Options;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed record ReleaseRiskInboxProcessingResult(
    ConsumedReleaseRiskEvent ConsumedEvent,
    ReleaseRiskInboxAcceptance Acceptance);

public sealed class ReleaseRiskInboxProcessor : BackgroundService
{
    private readonly Func<IReleaseRiskEventConsumer> _consumerFactory;
    private readonly IReleaseRiskInboxStore _store;
    private readonly ReleaseRiskInboxProcessorOptions _options;
    private readonly ILogger<ReleaseRiskInboxProcessor> _logger;
    private IReleaseRiskEventConsumer? _consumer;

    public ReleaseRiskInboxProcessor(
        Func<IReleaseRiskEventConsumer> consumerFactory,
        IReleaseRiskInboxStore store,
        IOptions<ReleaseRiskInboxProcessorOptions> options,
        ILogger<ReleaseRiskInboxProcessor> logger)
    {
        _consumerFactory = consumerFactory;
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ReleaseRiskInboxProcessingResult?> ProcessNextAsync(
        CancellationToken cancellationToken)
    {
        var consumer = _consumer ??= _consumerFactory();
        var consumedEvent = consumer.Consume(cancellationToken);
        if (consumedEvent is null)
        {
            return null;
        }

        using var persistenceTimeout = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(
                _options.PersistenceTimeoutMilliseconds));
        using var persistenceCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(
                cancellationToken,
                persistenceTimeout.Token);

        ReleaseRiskInboxAcceptance acceptance;
        try
        {
            acceptance = await _store.AcceptAsync(
                consumedEvent,
                persistenceCancellation.Token);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested &&
                  persistenceTimeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Persisting release risk event '{consumedEvent.MessageKey}' exceeded the configured timeout; its Kafka offset was not committed.",
                exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        consumer.Commit(consumedEvent);

        return new ReleaseRiskInboxProcessingResult(
            consumedEvent,
            acceptance);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Release risk inbox processor is disabled.");
            return;
        }

        await Task.Yield();
        _logger.LogInformation("Release risk inbox processor started.");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var result = await ProcessNextAsync(stoppingToken);
                if (result is not null)
                {
                    _logger.LogInformation(
                        "Release risk event {EventId} was durably {InboxAcceptance} and Kafka offset {Topic}/{Partition}/{Offset} was committed.",
                        result.ConsumedEvent.MessageKey,
                        result.Acceptance,
                        result.ConsumedEvent.Topic,
                        result.ConsumedEvent.Partition,
                        result.ConsumedEvent.Offset);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogCritical(
                exception,
                "Release risk inbox processing failed before a safe offset boundary; the worker will stop so a later record cannot commit past the failed record.");
            throw;
        }

        _logger.LogInformation("Release risk inbox processor stopped.");
    }
}
