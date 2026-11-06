namespace Aero.Downloads;

public sealed class DownloadTransferRateEstimator
{
    private long _lastBytesReceived;
    private DateTimeOffset _lastSample;

    public DownloadTransferRateEstimator(
        long initialBytesReceived,
        DateTimeOffset? initialTimestamp = null)
    {
        _lastBytesReceived = Math.Max(0, initialBytesReceived);
        _lastSample = initialTimestamp ?? DateTimeOffset.UtcNow;
    }

    public double BytesPerSecond { get; private set; }

    public bool Update(long currentBytesReceived, DateTimeOffset timestamp)
    {
        double seconds = (timestamp - _lastSample).TotalSeconds;
        if (seconds < 0.12)
        {
            return false;
        }

        long normalizedBytes = Math.Max(0, currentBytesReceived);
        long delta = Math.Max(0, normalizedBytes - _lastBytesReceived);
        double instantSpeed = delta / seconds;
        BytesPerSecond = BytesPerSecond <= 0
            ? instantSpeed
            : (BytesPerSecond * 0.65) + (instantSpeed * 0.35);
        _lastBytesReceived = normalizedBytes;
        _lastSample = timestamp;
        return true;
    }
}
