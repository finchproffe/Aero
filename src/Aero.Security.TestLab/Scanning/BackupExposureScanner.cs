using System.IO.Compression;
using System.Security.Cryptography;
using Aero.Security.TestLab.Models;

namespace Aero.Security.TestLab.Scanning;

internal sealed class BackupExposureScanner
{
    private static readonly byte[] EnvelopeMagic = "AEROBK18"u8.ToArray();

    private static readonly string[] SensitiveEntryPrefixes =
    [
        "Security/",
        "Network/Cookies",
        "Secure Preferences",
        "Local Storage/",
        "IndexedDB/"
    ];

    public BackupExposureScanner(MarkerSet markerSet, long maximumEntryBytes)
    {
        _markerSet = markerSet;
        _maximumEntryBytes = maximumEntryBytes;
    }

    private readonly MarkerSet _markerSet;
    private readonly long _maximumEntryBytes;

    public int FilesVisited { get; private set; }

    public long BytesRead { get; private set; }

    public int ErrorCount { get; private set; }

    public IReadOnlyList<ScanFinding> Scan(
        string path,
        CancellationToken cancellationToken = default)
    {
        List<ScanFinding> findings = [];
        if (!File.Exists(path))
        {
            findings.Add(new ScanFinding(
                FindingSeverity.Warning,
                "backup",
                "backup-missing",
                path,
                "The requested backup file does not exist."));
            return findings;
        }

        FilesVisited++;
        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                FileOptions.SequentialScan);

            byte[] header = new byte[13];
            int headerRead = stream.Read(header, 0, header.Length);
            stream.Position = 0;

            if (headerRead == header.Length &&
                header.AsSpan(0, EnvelopeMagic.Length).SequenceEqual(EnvelopeMagic))
            {
                byte encryptionMode = header[12];
                if (encryptionMode == 0)
                {
                    findings.Add(new ScanFinding(
                        FindingSeverity.Warning,
                        "backup",
                        "unencrypted-aero-envelope",
                        path,
                        "The Aero backup uses the unencrypted envelope mode."));
                    InspectUnencryptedEnvelope(stream, path, findings, cancellationToken);
                }
                else if (encryptionMode is 1 or 2)
                {
                    findings.Add(new ScanFinding(
                        FindingSeverity.Info,
                        "backup",
                        encryptionMode == 1 ? "password-encrypted-envelope" : "dpapi-encrypted-envelope",
                        path,
                        encryptionMode == 1
                            ? "The Aero backup is protected with password-based AES-GCM encryption."
                            : "The Aero backup is protected for the current Windows user with DPAPI."));
                    InspectEncryptedEnvelope(stream, path, findings, cancellationToken);
                }
                else
                {
                    findings.Add(new ScanFinding(
                        FindingSeverity.Warning,
                        "backup",
                        "unknown-envelope-mode",
                        path,
                        $"The Aero backup uses unknown encryption mode {encryptionMode}."));
                }

                return findings;
            }

            if (headerRead >= 4 &&
                header[0] == (byte)'P' &&
                header[1] == (byte)'K' &&
                header[2] == 3 &&
                header[3] == 4)
            {
                findings.Add(new ScanFinding(
                    FindingSeverity.Warning,
                    "backup",
                    "raw-unencrypted-zip",
                    path,
                    "The backup is a raw unencrypted ZIP payload."));
                InspectZip(stream, path, findings, cancellationToken);
                return findings;
            }

            findings.Add(new ScanFinding(
                FindingSeverity.Warning,
                "backup",
                "unknown-backup-format",
                path,
                "The file is neither an Aero envelope nor a ZIP payload."));
            InspectRawStream(stream, path, "plaintext-marker-in-unknown-backup", findings, cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException)
        {
            ErrorCount++;
            findings.Add(new ScanFinding(
                FindingSeverity.Warning,
                "backup",
                "backup-unreadable",
                path,
                $"The backup could not be inspected: {exception.GetType().Name}."));
        }

        return findings;
    }

    private void InspectUnencryptedEnvelope(
        FileStream stream,
        string path,
        List<ScanFinding> findings,
        CancellationToken cancellationToken)
    {
        stream.Position = EnvelopeMagic.Length + sizeof(int) + sizeof(byte);
        using BinaryReader reader = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        int payloadLength = reader.ReadInt32();
        if (payloadLength < 0 || payloadLength > stream.Length - stream.Position)
        {
            throw new InvalidDataException("The unencrypted backup payload length is invalid.");
        }

        if (payloadLength > _maximumEntryBytes || payloadLength > int.MaxValue)
        {
            findings.Add(new ScanFinding(
                FindingSeverity.Warning,
                "backup",
                "plain-backup-too-large",
                path,
                $"The unencrypted payload exceeds the {_maximumEntryBytes / (1024 * 1024)} MB inspection limit."));
            return;
        }

        byte[] zipBytes = reader.ReadBytes(payloadLength);
        if (zipBytes.Length != payloadLength)
        {
            CryptographicOperations.ZeroMemory(zipBytes);
            throw new EndOfStreamException("The unencrypted backup payload is truncated.");
        }

        try
        {
            using MemoryStream zipStream = new(zipBytes, writable: false);
            InspectZip(zipStream, path, findings, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(zipBytes);
        }
    }

    private void InspectEncryptedEnvelope(
        FileStream stream,
        string path,
        List<ScanFinding> findings,
        CancellationToken cancellationToken)
    {
        stream.Position = 0;
        BinaryScanResult encryptedScan = BinaryPatternScanner.Scan(
            stream,
            _markerSet,
            Math.Min(stream.Length, _maximumEntryBytes),
            cancellationToken);
        BytesRead += encryptedScan.BytesRead;
        foreach (PatternHit hit in encryptedScan.Hits)
        {
            findings.Add(new ScanFinding(
                FindingSeverity.Critical,
                "backup",
                "marker-in-encrypted-envelope",
                path,
                "The test marker is visible inside an encrypted backup envelope.",
                hit.Encoding,
                $"0x{hit.Offset:X}"));
        }
    }

    private void InspectZip(
        Stream zipStream,
        string path,
        List<ScanFinding> findings,
        CancellationToken cancellationToken)
    {
        using ZipArchive archive = new(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalized = entry.FullName.Replace('\\', '/');
            if (SensitiveEntryPrefixes.Any(prefix =>
                normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                findings.Add(new ScanFinding(
                    FindingSeverity.Critical,
                    "backup",
                    "sensitive-entry-in-plain-backup",
                    path,
                    "An unencrypted backup contains sensitive profile data.",
                    Location: normalized));
            }

            if (entry.Length <= 0)
            {
                continue;
            }

            using Stream entryStream = entry.Open();
            BinaryScanResult entryResult = BinaryPatternScanner.Scan(
                entryStream,
                _markerSet,
                Math.Min(entry.Length, _maximumEntryBytes),
                cancellationToken);
            BytesRead += entryResult.BytesRead;
            foreach (PatternHit hit in entryResult.Hits)
            {
                findings.Add(new ScanFinding(
                    FindingSeverity.Critical,
                    "backup",
                    "plaintext-marker-in-backup",
                    path,
                    "The test secret marker was found in an unencrypted backup entry.",
                    hit.Encoding,
                    $"{normalized}@0x{hit.Offset:X}"));
            }
        }
    }

    private void InspectRawStream(
        Stream stream,
        string path,
        string rule,
        List<ScanFinding> findings,
        CancellationToken cancellationToken)
    {
        stream.Position = 0;
        BinaryScanResult rawResult = BinaryPatternScanner.Scan(
            stream,
            _markerSet,
            Math.Min(stream.Length, _maximumEntryBytes),
            cancellationToken);
        BytesRead += rawResult.BytesRead;
        foreach (PatternHit hit in rawResult.Hits)
        {
            findings.Add(new ScanFinding(
                FindingSeverity.Critical,
                "backup",
                rule,
                path,
                "The test secret marker is visible in the backup file.",
                hit.Encoding,
                $"0x{hit.Offset:X}"));
        }
    }
}
