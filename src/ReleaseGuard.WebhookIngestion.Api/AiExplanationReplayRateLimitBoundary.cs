using Microsoft.Extensions.Options;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed class AiExplanationReplayRateLimitBoundary
{
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _window;
    private readonly int _permitLimit;
    private long _windowStartedTimestamp;
    private int _remainingPermits;

    public AiExplanationReplayRateLimitBoundary(
        IOptions<AiExplanationReplayOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        var value = options.Value;
        AiExplanationReplayOptions.ThrowIfInvalid(value);
        _timeProvider = timeProvider;
        _window = TimeSpan.FromMilliseconds(value.WindowMilliseconds);
        _permitLimit = value.PermitLimit;
        _remainingPermits = value.PermitLimit;
        _windowStartedTimestamp = timeProvider.GetTimestamp();
    }

    public AiExplanationQueryRateLimitDecision AttemptAcquire()
    {
        lock (_sync)
        {
            var now = _timeProvider.GetTimestamp();
            var elapsed = _timeProvider.GetElapsedTime(
                _windowStartedTimestamp,
                now);
            if (elapsed >= _window)
            {
                _windowStartedTimestamp = now;
                _remainingPermits = _permitLimit;
                elapsed = TimeSpan.Zero;
            }

            if (_remainingPermits > 0)
            {
                _remainingPermits--;
                return AiExplanationQueryRateLimitDecision.Acquired;
            }

            return new AiExplanationQueryRateLimitDecision(
                false,
                Math.Max(
                    1,
                    checked((int)Math.Ceiling((_window - elapsed).TotalSeconds))));
        }
    }
}
