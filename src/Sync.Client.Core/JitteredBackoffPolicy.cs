using Microsoft.AspNetCore.SignalR.Client;

namespace Sync.Client.Core;

public sealed class JitteredBackoffPolicy : IRetryPolicy
{
    private static readonly TimeSpan MinimumDelay = TimeSpan.FromSeconds(1);

    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;

    public JitteredBackoffPolicy(TimeSpan? baseDelay = null, TimeSpan? maxDelay = null)
    {
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
        _maxDelay = maxDelay ?? TimeSpan.FromSeconds(60);
        if (_baseDelay < MinimumDelay) _baseDelay = MinimumDelay;
        if (_maxDelay < _baseDelay) _maxDelay = _baseDelay;
    }

    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        if (SyncConnection.FindRejection(retryContext.RetryReason) is { IsPermanent: true })
            return null;

        var exponent = Math.Min(retryContext.PreviousRetryCount, 16);
        var ceiling = Math.Min(_maxDelay.TotalMilliseconds, _baseDelay.TotalMilliseconds * Math.Pow(2, exponent));
        var jittered = ceiling / 2 + Random.Shared.NextDouble() * ceiling / 2;
        return TimeSpan.FromMilliseconds(Math.Max(MinimumDelay.TotalMilliseconds, jittered));
    }
}
