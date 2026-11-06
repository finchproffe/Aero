namespace Aero.Security.TestLab.Scanning;

internal sealed record PatternHit(string Encoding, long Offset);

internal sealed record BinaryScanResult(
    long BytesRead,
    bool Truncated,
    IReadOnlyList<PatternHit> Hits);

internal static class BinaryPatternScanner
{
    private const int ChunkSize = 1024 * 1024;
    private const int MaximumHitsPerPattern = 8;

    public static BinaryScanResult Scan(
        Stream stream,
        MarkerSet markerSet,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(markerSet);

        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream is not readable.", nameof(stream));
        }

        maximumBytes = Math.Max(1, maximumBytes);
        int overlapCapacity = Math.Max(0, markerSet.MaximumPatternLength - 1);
        byte[] readBuffer = new byte[ChunkSize];
        byte[] window = new byte[ChunkSize + overlapCapacity];
        byte[] tail = new byte[overlapCapacity];
        int tailLength = 0;
        long bytesReadTotal = 0;
        List<PatternHit> hits = [];
        Dictionary<string, int> hitCounts = markerSet.Patterns
            .ToDictionary(pattern => pattern.Name, _ => 0, StringComparer.Ordinal);

        while (bytesReadTotal < maximumBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int requested = (int)Math.Min(readBuffer.Length, maximumBytes - bytesReadTotal);
            int read = stream.Read(readBuffer, 0, requested);
            if (read <= 0)
            {
                break;
            }

            if (tailLength > 0)
            {
                Buffer.BlockCopy(tail, 0, window, 0, tailLength);
            }

            Buffer.BlockCopy(readBuffer, 0, window, tailLength, read);
            int windowLength = tailLength + read;
            long windowBaseOffset = bytesReadTotal - tailLength;
            ReadOnlySpan<byte> span = window.AsSpan(0, windowLength);

            foreach (MarkerPattern pattern in markerSet.Patterns)
            {
                if (hitCounts[pattern.Name] >= MaximumHitsPerPattern)
                {
                    continue;
                }

                int cursor = 0;
                while (cursor <= span.Length - pattern.Bytes.Length &&
                       hitCounts[pattern.Name] < MaximumHitsPerPattern)
                {
                    int relative = span[cursor..].IndexOf(pattern.Bytes);
                    if (relative < 0)
                    {
                        break;
                    }

                    int index = cursor + relative;
                    hits.Add(new PatternHit(pattern.Name, windowBaseOffset + index));
                    hitCounts[pattern.Name]++;
                    cursor = index + 1;
                }
            }

            bytesReadTotal += read;
            tailLength = Math.Min(overlapCapacity, windowLength);
            if (tailLength > 0)
            {
                Buffer.BlockCopy(window, windowLength - tailLength, tail, 0, tailLength);
            }
        }

        bool truncated = bytesReadTotal >= maximumBytes && stream.ReadByte() >= 0;
        Array.Clear(readBuffer);
        Array.Clear(window);
        Array.Clear(tail);
        return new BinaryScanResult(bytesReadTotal, truncated, hits);
    }
}
