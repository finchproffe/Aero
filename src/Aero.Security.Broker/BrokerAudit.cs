// Aero Vault. Vault is Powered by Aero.
// Copyright (c) 2026 finchproffe. Licensed under the Mozilla Public License 2.0.
// Under MPL-2.0 section 3.4 this notice must stay in every copy and fork of this file.
using System.Security.Cryptography;
using System.Text;

namespace Aero.Security.BrokerHost;

internal sealed class BrokerAudit
{
    private const long MaximumAuditLength = 1024 * 1024;
    private readonly string _path;
    private readonly object _sync = new();

    public BrokerAudit(string securityDirectory)
    {
        Directory.CreateDirectory(securityDirectory);
        _path = Path.Combine(securityDirectory, "broker-audit.log");
    }

    public void Write(
        string action,
        string outcome,
        int clientProcessId,
        Guid requestId,
        ReadOnlySpan<byte> originUtf8 = default)
    {
        byte[]? originHash = null;
        try
        {
            string originFingerprint = "-";
            if (!originUtf8.IsEmpty)
            {
                originHash = SHA256.HashData(originUtf8);
                originFingerprint = Convert.ToHexString(originHash.AsSpan(0, 8));
            }

            string line = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{DateTimeOffset.UtcNow:O}\taction={Sanitize(action)}\toutcome={Sanitize(outcome)}\tclient={clientProcessId}\trequest={requestId:N}\torigin={originFingerprint}{Environment.NewLine}");
            lock (_sync)
            {
                try
                {
                    if (File.Exists(_path) && new FileInfo(_path).Length > MaximumAuditLength)
                    {
                        File.Move(_path, _path + ".old", overwrite: true);
                    }
                    File.AppendAllText(_path, line, new UTF8Encoding(false));
                }
                catch
                {
                    // Security audit logging must never make a protected operation fail open.
                }
            }
        }
        finally
        {
            if (originHash is not null)
            {
                CryptographicOperations.ZeroMemory(originHash);
            }
        }
    }

    private static string Sanitize(string value)
    {
        Span<char> output = stackalloc char[Math.Min(value.Length, 48)];
        int length = 0;
        foreach (char character in value)
        {
            if (length == output.Length)
            {
                break;
            }

            output[length++] = char.IsLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '_';
        }
        return new string(output[..length]);
    }
}
