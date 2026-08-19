using Microsoft.Extensions.Options;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed class ReleaseRiskOutboxDispatcher : BackgroundService
{
    private readonly IReleaseRiskOutboxStore _store;
    private readonly IReleaseRiskEventProducer _producer;
    private readonly OutboxDispatcherOptions _options;
    private readonly ILogger<ReleaseRiskOutboxDispatcher> _logger;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");

    public ReleaseRiskOutboxDispatcher(
        IReleaseRiskOutboxStore store,
        IReleaseRiskEventProducer producer,
        IOptions<OutboxDispatcherOptions> options,
        ILogger<ReleaseRiskOutboxDispatcher> logger)
    {
        _store = store;
        _producer = producer;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Release risk outbox dispatcher is disabled.");
            return;
        }

        _logger.LogInformation(
            "Release risk outbox dispatcher {DispatcherInstanceId} started.",
            _instanceId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var claimedCount = await DispatchPendingBatchAsync(stoppingToken);
                if (claimedCount == 0)
                {
                    await Task.Delay(
                        _options.PollIntervalMilliseconds,
                        stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Release risk outbox dispatch loop failed; polling will resume after the configured interval.");

                try
                {
                    await Task.Delay(
                        _options.PollIntervalMilliseconds,
                        stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        _logger.LogInformation(
            "Release risk outbox dispatcher {DispatcherInstanceId} stopped.",
            _instanceId);
    }

    public async Task<int> DispatchPendingBatchAsync(
        CancellationToken cancellationToken)
    {
        var claimOwner = $"{_instanceId}:{Guid.NewGuid():N}";
        var claims = await _store.ClaimPendingAsync(
            claimOwner,
            _options.BatchSize,
            TimeSpan.FromMilliseconds(_options.LeaseDurationMilliseconds),
            cancellationToken);

        await Task.WhenAll(claims.Select(
            claim => DispatchClaimAsync(claim, cancellationToken)));

        return claims.Count;
    }

    private async Task DispatchClaimAsync(
        ReleaseRiskOutboxClaim claim,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DispatchOneAsync(claim, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseClaimsAsync([claim]);
            throw;
        }
    }

    private async Task DispatchOneAsync(
        ReleaseRiskOutboxClaim claim,
        CancellationToken cancellationToken)
    {
        try
        {
            await _producer.PublishAsync(claim.Envelope, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var retryDelay = OutboxDispatcherOptions.CalculateRetryDelay(
                _options,
                claim.AttemptCount);
            var markedFailed = await TryStateUpdateAsync(
                token => _store.MarkFailedAsync(claim, retryDelay, token),
                claim,
                "schedule retry");

            _logger.LogWarning(
                exception,
                "Publishing release risk event {EventId} failed on attempt {AttemptCount}; retry scheduled in {RetryDelayMilliseconds} ms: {RetryStateRecorded}.",
                claim.EventId,
                claim.AttemptCount,
                retryDelay.TotalMilliseconds,
                markedFailed);
            return;
        }

        var markedPublished = await TryStateUpdateAsync(
            token => _store.MarkPublishedAsync(claim, token),
            claim,
            "mark published");

        if (!markedPublished)
        {
            _logger.LogWarning(
                "Release risk event {EventId} was acknowledged by Kafka but its claim could not be marked published; an expired-lease retry may produce a duplicate.",
                claim.EventId);
        }
    }

    private async Task ReleaseClaimsAsync(
        IEnumerable<ReleaseRiskOutboxClaim> claims)
    {
        foreach (var claim in claims)
        {
            await TryStateUpdateAsync(
                token => _store.ReleaseClaimAsync(claim, token),
                claim,
                "release during shutdown");
        }
    }

    private async Task<bool> TryStateUpdateAsync(
        Func<CancellationToken, Task<bool>> update,
        ReleaseRiskOutboxClaim claim,
        string operation)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(_options.StateUpdateTimeoutMilliseconds));

        try
        {
            return await update(timeout.Token);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to {OutboxStateOperation} for release risk event {EventId}; the lease remains the recovery boundary.",
                operation,
                claim.EventId);
            return false;
        }
    }
}
