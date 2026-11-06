namespace Aero.Downloads;

/// <summary>
/// Owns duplicate suppression for WebView download notifications without taking
/// a dependency on WebView2, WinUI or the download UI.
/// </summary>
public sealed class DownloadRequestCoordinator
{
    private static readonly TimeSpan RequestRetention = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(4);

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _recentRequests =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<object> _claimedOperations =
        new(ReferenceIdentityComparer.Instance);
    private readonly HashSet<object> _registeredOperations =
        new(ReferenceIdentityComparer.Instance);

    public bool TryClaimOperation(object operationIdentity)
    {
        ArgumentNullException.ThrowIfNull(operationIdentity);
        lock (_gate)
        {
            return _claimedOperations.Add(operationIdentity);
        }
    }

    public void ReleaseOperationClaim(object operationIdentity)
    {
        ArgumentNullException.ThrowIfNull(operationIdentity);
        lock (_gate)
        {
            _claimedOperations.Remove(operationIdentity);
        }
    }

    public bool TryRegisterOperation(object operationIdentity)
    {
        ArgumentNullException.ThrowIfNull(operationIdentity);
        lock (_gate)
        {
            return _registeredOperations.Add(operationIdentity);
        }
    }

    public void ReleaseRegisteredOperation(object operationIdentity)
    {
        ArgumentNullException.ThrowIfNull(operationIdentity);
        lock (_gate)
        {
            _registeredOperations.Remove(operationIdentity);
            _claimedOperations.Remove(operationIdentity);
        }
    }

    public bool TryClaimRequest(
        string? downloadUri,
        string? suggestedPath,
        out string fingerprint) =>
        TryClaimRequest(downloadUri, suggestedPath, DateTimeOffset.UtcNow, out fingerprint);

    public bool TryClaimRequest(
        string? downloadUri,
        string? suggestedPath,
        DateTimeOffset now,
        out string fingerprint)
    {
        fingerprint = string.Concat(
            DownloadFingerprint.NormalizeUri(downloadUri),
            "\n",
            DownloadFingerprint.NormalizeFileName(suggestedPath));

        lock (_gate)
        {
            RemoveExpiredRequests(now);
            if (_recentRequests.TryGetValue(fingerprint, out DateTimeOffset previous) &&
                now - previous < DuplicateWindow)
            {
                fingerprint = string.Empty;
                return false;
            }

            _recentRequests[fingerprint] = now;
            return true;
        }
    }

    public void ReleaseRequest(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return;
        }

        lock (_gate)
        {
            _recentRequests.Remove(fingerprint);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _recentRequests.Clear();
            _claimedOperations.Clear();
            _registeredOperations.Clear();
        }
    }

    private void RemoveExpiredRequests(DateTimeOffset now)
    {
        List<string>? stale = null;
        foreach (KeyValuePair<string, DateTimeOffset> pair in _recentRequests)
        {
            if (now - pair.Value <= RequestRetention)
            {
                continue;
            }

            stale ??= [];
            stale.Add(pair.Key);
        }

        if (stale is null)
        {
            return;
        }

        foreach (string key in stale)
        {
            _recentRequests.Remove(key);
        }
    }

    private sealed class ReferenceIdentityComparer : IEqualityComparer<object>
    {
        public static ReferenceIdentityComparer Instance { get; } = new();

        bool IEqualityComparer<object>.Equals(object? left, object? right) =>
            ReferenceEquals(left, right);

        int IEqualityComparer<object>.GetHashCode(object value) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
    }
}
