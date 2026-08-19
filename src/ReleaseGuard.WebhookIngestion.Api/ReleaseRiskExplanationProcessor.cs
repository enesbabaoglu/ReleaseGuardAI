using Microsoft.Extensions.Options;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed class ReleaseRiskExplanationProcessor : BackgroundService
{
    private readonly IReleaseRiskExplanationStore _store;
    private readonly IReleaseRiskExplanationClient _client;
    private readonly AiExplanationProcessorOptions _options;
    private readonly ILogger<ReleaseRiskExplanationProcessor> _logger;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");

    public ReleaseRiskExplanationProcessor(
        IReleaseRiskExplanationStore store,
        IReleaseRiskExplanationClient client,
        IOptions<AiExplanationProcessorOptions> options,
        ILogger<ReleaseRiskExplanationProcessor> logger)
    {
        _store = store;
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<int> ProcessPendingBatchAsync(
        CancellationToken cancellationToken)
    {
        var claimOwner = $"{_instanceId}:{Guid.NewGuid():N}";
        var claims = await _store.ClaimPendingAsync(
            claimOwner,
            _options.BatchSize,
            TimeSpan.FromMilliseconds(_options.LeaseDurationMilliseconds),
            cancellationToken);

        await Task.WhenAll(claims.Select(
            claim => ProcessClaimAsync(claim, cancellationToken)));

        return claims.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Release risk AI explanation processor is disabled.");
            return;
        }

        _logger.LogInformation(
            "Release risk AI explanation processor {ProcessorInstanceId} started.",
            _instanceId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var claimedCount = await ProcessPendingBatchAsync(stoppingToken);
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
                    "Release risk AI explanation processing loop failed; polling will resume after the configured interval.");

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
            "Release risk AI explanation processor {ProcessorInstanceId} stopped.",
            _instanceId);
    }

    private async Task ProcessClaimAsync(
        ReleaseRiskExplanationClaim claim,
        CancellationToken cancellationToken)
    {
        ReleaseRiskExplanation explanation;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            explanation = await _client.ExplainAsync(
                claim.Envelope,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (!explanation.IsValidFor(claim.EventId))
            {
                throw new ReleaseRiskExplanationContractException(
                    "The explanation response is not valid for the claimed event ID.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryStateUpdateAsync(
                token => _store.ReleaseClaimAsync(claim, token),
                claim,
                "release during shutdown");
            throw;
        }
        catch (Exception exception)
        {
            var retryDelay = AiExplanationProcessorOptions.CalculateRetryDelay(
                _options,
                claim.AttemptCount);
            var markedFailed = await TryStateUpdateAsync(
                token => _store.MarkFailedAsync(
                    claim,
                    retryDelay,
                    token),
                claim,
                "schedule retry");

            _logger.LogWarning(
                exception,
                "Generating release risk explanation {EventId} failed on attempt {AttemptCount}; retry scheduled in {RetryDelayMilliseconds} ms: {RetryStateRecorded}.",
                claim.EventId,
                claim.AttemptCount,
                retryDelay.TotalMilliseconds,
                markedFailed);
            return;
        }

        var markedCompleted = await TryStateUpdateAsync(
            token => _store.MarkCompletedAsync(
                claim,
                explanation,
                token),
            claim,
            "mark completed");

        if (!markedCompleted)
        {
            _logger.LogWarning(
                "Release risk explanation {EventId} completed after its ownership was lost or its state update was uncertain; the result was not accepted and lease recovery may call the service again.",
                claim.EventId);
        }
    }

    private async Task<bool> TryStateUpdateAsync(
        Func<CancellationToken, Task<bool>> update,
        ReleaseRiskExplanationClaim claim,
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
                "Failed to {ExplanationStateOperation} for release risk event {EventId}; the lease remains the recovery boundary.",
                operation,
                claim.EventId);
            return false;
        }
    }
}
