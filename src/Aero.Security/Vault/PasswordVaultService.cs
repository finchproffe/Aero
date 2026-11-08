// Aero Vault. Vault is Powered by Aero.
// Copyright (c) 2026 finchproffe. Licensed under the Mozilla Public License 2.0.
// Under MPL-2.0 section 3.4 this notice must stay in every copy and fork of this file.
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Windows.Security.Credentials.UI;

namespace Aero.App.Security;

/// <summary>
/// Stores Aero credentials behind a non-exportable Windows Hello CNG key.
/// Plaintext password bytes are returned only through a short-lived PasswordLease.
/// </summary>
public sealed class PasswordVaultService : IDisposable
{
    private const int RootKeyLength = 32;
    private const int NodeWrappingKeyLength = 32;
    private const int NodeIdLength = 32;
    private const int NodeNonceLength = 12;
    private const int NodeTagLength = 16;
    private const int OriginTagLength = 32;
    private const int MaximumRecordLength = 1024 * 1024;
    private const int MaximumNodeLockEnvelopeLength = 64 * 1024;
    private const long MaximumVaultLength = 64L * 1024 * 1024;
    private const long MaximumAuditLength = 1024 * 1024;
    private static readonly TimeSpan FailedPromptCooldown = TimeSpan.FromSeconds(2);
    private static readonly byte[] VaultMagic = "AEROVLT2"u8.ToArray();
    private static readonly byte[] LegacyKeyEnvelopeMagic = "AEROKEY2"u8.ToArray();
    private static readonly byte[] KeyEnvelopeMagic = "AEROKEY3"u8.ToArray();
    private static readonly byte[] NodeLockEnvelopeMagic = "AERONDL1"u8.ToArray();
    private static readonly byte[] NodeLockAadLabel =
        Encoding.UTF8.GetBytes("Aero.Vault.NodeLock.DeviceSlot.v1");

    // Application-specific DPAPI entropy. This is an extra format binding, not a
    // replacement for the Windows user-bound DPAPI key.
    private static readonly byte[] ApplicationEntropy =
    [
        0x41, 0x65, 0x72, 0x6F, 0x2D, 0x56, 0x61, 0x75,
        0x6C, 0x74, 0x2D, 0x30, 0x2E, 0x31, 0x36, 0x2D,
        0x44, 0x50, 0x41, 0x50, 0x49, 0x2D, 0x4A, 0x49,
        0x54, 0x2D, 0x32, 0x30, 0x32, 0x36, 0xA7, 0x5C
    ];

    private static readonly byte[] RecordEntropyLabel =
        Encoding.UTF8.GetBytes("Aero.PasswordVault.RecordEntropy.v1");

    private readonly string _vaultPath;
    private readonly string _legacyRootKeyPath;
    private readonly string _wrappedRootKeyPath;
    private readonly string _nodeLockPath;
    private readonly string _auditPath;
    private readonly string _cngKeyName;
    private readonly string _nodeLockCngKeyName;
    private readonly string _transitionalCngKeyName;
    private readonly string _legacyCngKeyName;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _auditGate = new();
    private readonly PasswordBrokerClient? _passwordBrokerClient;
    private readonly string _processSessionId = Guid.NewGuid().ToString("N");
    private DateTimeOffset _lastFailedPromptUtc;
    private long _auditSequence;
    private bool _disposed;

    public PasswordVaultService(
        string? storageDirectory = null,
        PasswordBrokerClient? passwordBrokerClient = null)
    {
        _passwordBrokerClient = passwordBrokerClient;
        string directory = storageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Aero",
            "Security");

        Directory.CreateDirectory(directory);
        _vaultPath = Path.Combine(directory, "vault.bin");
        _legacyRootKeyPath = Path.Combine(directory, ".vault-key.bin");
        _wrappedRootKeyPath = Path.Combine(directory, ".vault-key-v2.bin");
        _nodeLockPath = Path.Combine(directory, ".vault-node-lock.bin");
        _auditPath = Path.Combine(directory, "vault-access.log");
        byte[] keyNameHash = SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(directory).ToUpperInvariant()));
        string keySuffix = Convert.ToHexString(keyNameHash.AsSpan(0, 12));
        _legacyCngKeyName = $"Aero.Vault2.{keySuffix}";
        _nodeLockCngKeyName = $"Aero.NodeLock.{keySuffix}";
        string userSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new PlatformNotSupportedException(
                "Aero Vault requires a Windows user security identifier.");
        // Passport KSP persistent containers use the Windows Hello
        // SID//domain/subdomain/name convention. Keep the previous attempted
        // spelling as a read-only migration alias.
        _cngKeyName = $"{userSid}//Aero//Vault-{keySuffix}";
        _transitionalCngKeyName = $"{userSid}//Aero/Vault/{keySuffix}";
        CryptographicOperations.ZeroMemory(keyNameHash);
    }

    // Phase 4 deliberately has no time-based global unlock cache. Every protected
    // operation receives its own short-lived root-key lease and destroys it on exit.
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "This instance property is retained for compatibility with the vault service contract.")]
    public bool IsUnlocked => false;

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "This instance property is retained for compatibility with the vault service contract.")]
    public TimeSpan RemainingUnlockTime => TimeSpan.Zero;

    public async Task SavePasswordAsync(
        string origin,
        ReadOnlyMemory<byte> usernameUtf8,
        ReadOnlyMemory<byte> passwordUtf8,
        IntPtr ownerWindow,
        CancellationToken cancellationToken = default)
    {
        await using PasswordVaultWriteSession session = await BeginWriteSessionAsync(
            ownerWindow,
            $"Save the protected sign-in for {new Uri(NormalizeHttpsOrigin(origin)).Host}",
            forceReauthentication: true,
            cancellationToken);
        await session.SavePasswordAsync(
            origin,
            usernameUtf8,
            passwordUtf8,
            cancellationToken);
        await session.CommitAsync(cancellationToken);
    }

    public async Task<PasswordVaultWriteSession> BeginWriteSessionAsync(
        IntPtr ownerWindow,
        string useContext,
        bool forceReauthentication = true,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        RootKeyOperationLease? rootKey = null;
        List<VaultRecord>? records = null;
        try
        {
            rootKey = await GetOrCreateRootKeyAsync(
                ownerWindow,
                useContext,
                cancellationToken,
                forceReauthentication);
            records = await ReadRecordsAsync(cancellationToken);
            PasswordVaultWriteSession session = new(this, rootKey, records);
            rootKey = null;
            records = null;
            WriteAuditEvent("write-session", "opened", origin: null);
            return session;
        }
        catch
        {
            rootKey?.Dispose();
            if (records is not null)
            {
                ZeroRecords(records);
            }
            WriteAuditEvent("write-session", "failed", origin: null);
            _gate.Release();
            throw;
        }
    }

    private string StagePasswordWithOperationKey(
        RootKeyOperationLease rootKey,
        List<VaultRecord> records,
        string origin,
        ReadOnlyMemory<byte> usernameUtf8,
        ReadOnlyMemory<byte> passwordUtf8)
    {
        ThrowIfDisposed();
        string normalizedOrigin = NormalizeHttpsOrigin(origin);
        if (passwordUtf8.IsEmpty)
        {
            throw new ArgumentException("Password cannot be empty.", nameof(passwordUtf8));
        }

        byte[]? originBytes = null;
        byte[]? originTag = null;
        byte[]? recordEntropy = null;
        byte[]? payload = null;
        byte[]? ciphertext = null;

        try
        {
            originBytes = Encoding.UTF8.GetBytes(normalizedOrigin);
            originTag = HMACSHA256.HashData(rootKey.Span, originBytes);
            recordEntropy = HMACSHA256.HashData(rootKey.Span, RecordEntropyLabel);
            payload = BuildPayload(originBytes, usernameUtf8.Span, passwordUtf8.Span);
            ciphertext = ProtectedData.Protect(
                payload,
                recordEntropy,
                DataProtectionScope.CurrentUser);

            int index = FindRecordIndex(records, originTag);
            VaultRecord replacement = new(originTag.ToArray(), ciphertext.ToArray());
            if (index >= 0)
            {
                ZeroRecord(records[index]);
                records[index] = replacement;
            }
            else
            {
                records.Add(replacement);
            }
            return normalizedOrigin;
        }
        catch
        {
            WriteAuditEvent("save-stage", "failed", normalizedOrigin);
            throw;
        }
        finally
        {
            Zero(originBytes);
            Zero(originTag);
            Zero(recordEntropy);
            Zero(payload);
            Zero(ciphertext);
        }
    }

    /// <summary>
    /// Requests an interactive password reveal through the short-lived,
    /// authenticated Aero Security Broker. This is the only reveal boundary
    /// exposed to Aero.App; plaintext vault decryption remains inside Broker.
    /// </summary>
    public Task<bool> GetPasswordForRevealAsync(
        string origin,
        IntPtr ownerWindow,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        PasswordBrokerClient brokerClient = _passwordBrokerClient
            ?? throw new InvalidOperationException(
                "Password reveal requires an attached Aero Security Broker client.");

        return brokerClient.RevealCredentialAsync(
            origin,
            ownerWindow,
            cancellationToken);
    }

    public Task<PasswordLease?> GetPasswordJitAsync(
        string origin,
        IntPtr ownerWindow,
        CancellationToken cancellationToken = default) =>
        GetPasswordJitCoreAsync(
            origin,
            ownerWindow,
            forceReauthentication: false,
            auditAction: "fill",
            beforePasswordMaterialization: null,
            cancellationToken: cancellationToken);

    public Task<PasswordLease?> GetPasswordForRevealJitAsync(
        string origin,
        IntPtr ownerWindow,
        CancellationToken cancellationToken = default) =>
        GetPasswordJitCoreAsync(
            origin,
            ownerWindow,
            forceReauthentication: true,
            auditAction: "reveal",
            beforePasswordMaterialization: null,
            cancellationToken: cancellationToken);

    public Task<PasswordLease?> GetPasswordForRevealJitAsync(
        string origin,
        IntPtr ownerWindow,
        Action beforePasswordMaterialization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(beforePasswordMaterialization);
        return GetPasswordJitCoreAsync(
            origin,
            ownerWindow,
            forceReauthentication: true,
            auditAction: "reveal",
            beforePasswordMaterialization: beforePasswordMaterialization,
            cancellationToken: cancellationToken);
    }

    private async Task<PasswordLease?> GetPasswordJitCoreAsync(
        string origin,
        IntPtr ownerWindow,
        bool forceReauthentication,
        string auditAction,
        Action? beforePasswordMaterialization,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string normalizedOrigin = NormalizeHttpsOrigin(origin);

        await _gate.WaitAsync(cancellationToken);

        RootKeyOperationLease? rootKey = null;
        byte[]? originBytes = null;
        byte[]? originTag = null;
        byte[]? recordEntropy = null;
        byte[]? plaintext = null;
        List<VaultRecord>? records = null;

        try
        {
            if (!File.Exists(_vaultPath) ||
                !HasAnyRootKeyEnvelope())
            {
                WriteAuditEvent(auditAction, "not-found", normalizedOrigin);
                return null;
            }

            rootKey = await GetOrCreateRootKeyAsync(
                ownerWindow,
                auditAction.Equals("reveal", StringComparison.Ordinal)
                    ? $"Reveal the protected password for {new Uri(normalizedOrigin).Host}"
                    : $"Fill the protected sign-in for {new Uri(normalizedOrigin).Host}",
                cancellationToken,
                forceReauthentication);

            // Windows Hello may need normal access to the Broker while its system UI
            // is active. The caller enables the kernel/DACL boundary immediately after
            // Hello unwraps the root key and before the vault record is located or the
            // password payload is decrypted into Broker memory.
            beforePasswordMaterialization?.Invoke();

            originBytes = Encoding.UTF8.GetBytes(normalizedOrigin);
            originTag = HMACSHA256.HashData(rootKey.Span, originBytes);
            recordEntropy = HMACSHA256.HashData(rootKey.Span, RecordEntropyLabel);
            records = await ReadRecordsAsync(cancellationToken);

            int index = FindRecordIndex(records, originTag);
            if (index < 0)
            {
                WriteAuditEvent(auditAction, "not-found", normalizedOrigin);
                return null;
            }

            plaintext = ProtectedData.Unprotect(
                records[index].Ciphertext,
                recordEntropy,
                DataProtectionScope.CurrentUser);

            PasswordLease lease = ParsePayload(plaintext, normalizedOrigin);
            if (!lease.IsPageLocked)
            {
                WriteAuditEvent("memory-lock", "unavailable", normalizedOrigin);
            }
            WriteAuditEvent(auditAction, "success", normalizedOrigin);
            return lease;
        }
        catch
        {
            WriteAuditEvent(auditAction, "failed", normalizedOrigin);
            throw;
        }
        finally
        {
            rootKey?.Dispose();
            Zero(originBytes);
            Zero(originTag);
            Zero(recordEntropy);
            Zero(plaintext);
            if (records is not null)
            {
                ZeroRecords(records);
            }
            _gate.Release();
        }
    }

    public Task<PasswordExportLease> GetAllPasswordsJitAsync(
        IntPtr ownerWindow,
        CancellationToken cancellationToken = default) =>
        GetPasswordEntriesJitAsync(
            ownerWindow,
            "Export every protected Aero Vault sign-in",
            "export",
            cancellationToken);


    public async Task<PasswordMetadataListLease> GetPasswordMetadataListJitAsync(
        IntPtr ownerWindow,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);

        RootKeyOperationLease? rootKey = null;
        byte[]? recordEntropy = null;
        List<VaultRecord>? records = null;
        List<PasswordMetadataEntryLease> entries = [];

        try
        {
            if (!File.Exists(_vaultPath) ||
                !HasAnyRootKeyEnvelope())
            {
                return new PasswordMetadataListLease(entries);
            }

            rootKey = await GetOrCreateRootKeyAsync(
                ownerWindow,
                "View saved Aero Vault sign-ins without revealing passwords",
                cancellationToken,
                forceReauthentication: true);
            recordEntropy = HMACSHA256.HashData(rootKey.Span, RecordEntropyLabel);
            records = await ReadRecordsAsync(cancellationToken);

            foreach (VaultRecord record in records)
            {
                byte[]? plaintext = null;
                byte[]? originForTag = null;
                byte[]? computedTag = null;
                PasswordMetadataEntryLease? entry = null;

                try
                {
                    plaintext = ProtectedData.Unprotect(
                        record.Ciphertext,
                        recordEntropy,
                        DataProtectionScope.CurrentUser);
                    entry = ParseMetadataPayload(plaintext);
                    originForTag = entry.OriginUtf8.ToArray();
                    computedTag = HMACSHA256.HashData(rootKey.Span, originForTag);
                    if (record.OriginTag.Length != computedTag.Length ||
                        !CryptographicOperations.FixedTimeEquals(record.OriginTag, computedTag))
                    {
                        entry.Dispose();
                        entry = null;
                        throw new CryptographicException(
                            "Aero password origin verification failed while reading protected metadata.");
                    }

                    entries.Add(entry);
                    entry = null;
                }
                finally
                {
                    entry?.Dispose();
                    Zero(plaintext);
                    Zero(originForTag);
                    Zero(computedTag);
                }
            }

            WriteAuditEvent("list-metadata", "success", origin: null);
            return new PasswordMetadataListLease(entries);
        }
        catch
        {
            WriteAuditEvent("list-metadata", "failed", origin: null);
            foreach (PasswordMetadataEntryLease entry in entries)
            {
                entry.Dispose();
            }

            throw;
        }
        finally
        {
            rootKey?.Dispose();
            Zero(recordEntropy);
            if (records is not null)
            {
                ZeroRecords(records);
            }
            _gate.Release();
        }
    }

    private async Task<PasswordExportLease> GetPasswordEntriesJitAsync(
        IntPtr ownerWindow,
        string useContext,
        string auditAction,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);

        RootKeyOperationLease? rootKey = null;
        byte[]? recordEntropy = null;
        List<VaultRecord>? records = null;
        List<PasswordExportEntryLease> entries = [];

        try
        {
            if (!File.Exists(_vaultPath) ||
                !HasAnyRootKeyEnvelope())
            {
                return new PasswordExportLease(entries);
            }

            rootKey = await GetOrCreateRootKeyAsync(
                ownerWindow,
                useContext,
                cancellationToken,
                forceReauthentication: true);
            recordEntropy = HMACSHA256.HashData(rootKey.Span, RecordEntropyLabel);
            records = await ReadRecordsAsync(cancellationToken);

            foreach (VaultRecord record in records)
            {
                byte[]? plaintext = null;
                byte[]? originForTag = null;
                byte[]? computedTag = null;
                PasswordExportEntryLease? entry = null;

                try
                {
                    plaintext = ProtectedData.Unprotect(
                        record.Ciphertext,
                        recordEntropy,
                        DataProtectionScope.CurrentUser);
                    entry = ParseExportPayload(plaintext);
                    originForTag = entry.OriginUtf8.ToArray();
                    computedTag = HMACSHA256.HashData(rootKey.Span, originForTag);
                    if (record.OriginTag.Length != computedTag.Length ||
                        !CryptographicOperations.FixedTimeEquals(record.OriginTag, computedTag))
                    {
                        entry.Dispose();
                        entry = null;
                        throw new CryptographicException(
                            "Aero password origin verification failed while reading protected entries.");
                    }

                    if (!entry.IsPasswordPageLocked)
                    {
                        WriteAuditEvent("memory-lock", "unavailable", origin: null);
                    }
                    entries.Add(entry);
                    entry = null;
                }
                finally
                {
                    entry?.Dispose();
                    Zero(plaintext);
                    Zero(originForTag);
                    Zero(computedTag);
                }
            }

            WriteAuditEvent(auditAction, "success", origin: null);
            return new PasswordExportLease(entries);
        }
        catch
        {
            WriteAuditEvent(auditAction, "failed", origin: null);
            foreach (PasswordExportEntryLease entry in entries)
            {
                entry.Dispose();
            }

            throw;
        }
        finally
        {
            rootKey?.Dispose();
            Zero(recordEntropy);
            if (records is not null)
            {
                ZeroRecords(records);
            }
            _gate.Release();
        }
    }

    public async Task<PasswordVaultLookupState> ContainsAsync(
        string origin,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        string normalizedOrigin = NormalizeHttpsOrigin(origin);
        await _gate.WaitAsync(cancellationToken);


        try
        {
            if (!File.Exists(_vaultPath))
            {
                return PasswordVaultLookupState.NotFound;
            }

            // A lookup would require unwrapping the root key. Do not create a hidden
            // unlock window merely to update toolbar state; the actual fill/delete
            // operation performs Windows Hello and an authenticated lookup.
            return PasswordVaultLookupState.Locked;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        List<VaultRecord>? records = null;

        try
        {
            records = await ReadRecordsAsync(cancellationToken);
            return records.Count;
        }
        finally
        {
            if (records is not null)
            {
                ZeroRecords(records);
            }
            _gate.Release();
        }
    }

    public async Task<bool> DeletePasswordAsync(
        string origin,
        IntPtr ownerWindow,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        string normalizedOrigin = NormalizeHttpsOrigin(origin);
        await _gate.WaitAsync(cancellationToken);

        RootKeyOperationLease? rootKey = null;
        byte[]? originBytes = null;
        byte[]? originTag = null;
        List<VaultRecord>? records = null;

        try
        {
            if (!File.Exists(_vaultPath) ||
                !HasAnyRootKeyEnvelope())
            {
                return false;
            }

            rootKey = await GetOrCreateRootKeyAsync(
                ownerWindow,
                $"Remove the protected sign-in for {new Uri(normalizedOrigin).Host}",
                cancellationToken);
            originBytes = Encoding.UTF8.GetBytes(normalizedOrigin);
            originTag = HMACSHA256.HashData(rootKey.Span, originBytes);
            records = await ReadRecordsAsync(cancellationToken);
            int index = FindRecordIndex(records, originTag);
            if (index < 0)
            {
                WriteAuditEvent("delete", "not-found", normalizedOrigin);
                return false;
            }

            ZeroRecord(records[index]);
            records.RemoveAt(index);
            await WriteRecordsAsync(records, cancellationToken);
            WriteAuditEvent("delete", "success", normalizedOrigin);
            return true;
        }
        catch
        {
            WriteAuditEvent("delete", "failed", normalizedOrigin);
            throw;
        }
        finally
        {
            rootKey?.Dispose();
            Zero(originBytes);
            Zero(originTag);
            if (records is not null)
            {
                ZeroRecords(records);
            }
            _gate.Release();
        }
    }

    public async Task ClearAsync(
        IntPtr ownerWindow,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (File.Exists(_vaultPath))
            {
                using RootKeyOperationLease rootKey = await GetOrCreateRootKeyAsync(
                    ownerWindow,
                    "Remove every protected Aero Vault sign-in",
                    cancellationToken,
                    forceReauthentication: true);
            }

            DeleteFileIfExists(_vaultPath);
            DeleteFileIfExists(_legacyRootKeyPath);
            DeleteFileIfExists(_wrappedRootKeyPath);
            DeleteFileIfExists(_nodeLockPath);
            DeleteCngKey();
            WriteAuditEvent("clear", "success", origin: null);
        }
        catch
        {
            WriteAuditEvent("clear", "failed", origin: null);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UnlockAsync(
        IntPtr ownerWindow,
        string useContext,
        bool forceReauthentication = false,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        RootKeyOperationLease? rootKey = null;
        try
        {
            rootKey = await GetOrCreateRootKeyAsync(
                ownerWindow,
                useContext,
                cancellationToken,
                forceReauthentication);
        }
        finally
        {
            rootKey?.Dispose();
            _gate.Release();
        }
    }

    public void Lock()
    {
        ThrowIfDisposed();
        // No persistent unlock state exists. Keep the method as an explicit audit
        // boundary for callers reacting to navigation/origin changes.
        WriteAuditEvent("lock", "success", origin: null);
    }

    public void RecordSecurityEvent(string action, string outcome, string? origin) =>
        WriteAuditEvent(action, outcome, origin);

    public static Task VerifyCookieImportConsentAsync(IntPtr ownerWindow) =>
        VerifySensitiveImportConsentAsync(
            ownerWindow,
            "Allow Aero to import cookies into the current browser profile?");

    public static Task VerifySiteUnlockAsync(IntPtr ownerWindow, string host) =>
        VerifySensitiveImportConsentAsync(
            ownerWindow,
            $"Unlock {host} in Aero?");

    private static async Task VerifySensitiveImportConsentAsync(
        IntPtr ownerWindow,
        string prompt)
    {
        UserConsentVerifierAvailability availability =
            await UserConsentVerifier.CheckAvailabilityAsync();

        if (availability != UserConsentVerifierAvailability.Available)
        {
            throw new PasswordVaultVerificationException(
                $"Windows Hello is unavailable: {availability}.");
        }

        UserConsentVerificationResult result =
            await UserConsentVerifierInterop.RequestVerificationForWindowAsync(
                ownerWindow,
                prompt);

        if (result != UserConsentVerificationResult.Verified)
        {
            throw new PasswordVaultVerificationException(
                $"Windows Hello verification was not completed: {result}.");
        }
    }

    private async Task<RootKeyOperationLease> GetOrCreateRootKeyAsync(
        IntPtr ownerWindow,
        string useContext,
        CancellationToken cancellationToken,
        bool forceReauthentication = false)
    {
        _ = forceReauthentication; // Every operation authenticates in Phase 4.

        if (ownerWindow == IntPtr.Zero)
        {
            throw new PasswordVaultVerificationException(
                "Aero Vault needs an active window for Windows Hello verification.");
        }

        UserConsentVerifierAvailability helloAvailability =
            await UserConsentVerifier.CheckAvailabilityAsync();
        if (helloAvailability != UserConsentVerifierAvailability.Available)
        {
            throw new PasswordVaultVerificationException(
                $"Windows Hello is not ready ({helloAvailability}). " +
                "Configure a Windows Hello PIN or biometrics in Windows Settings, then try again.");
        }

        if (DateTimeOffset.UtcNow - _lastFailedPromptUtc < FailedPromptCooldown)
        {
            WriteAuditEvent("unlock", "rate-limited", origin: null);
            throw new PasswordVaultVerificationException(
                "A Windows Hello request was just cancelled. Wait a moment before retrying.");
        }

        byte[]? rootKey = null;
        try
        {
            bool hasCngKey = DoesHelloKeyExist();

            if (File.Exists(_nodeLockPath))
            {
                if (!hasCngKey)
                {
                    throw new CryptographicException(
                        "The Windows Hello key for this Aero Vault is unavailable.");
                }

                WrappedRootKeyEnvelope envelope =
                    await ReadNodeLockedRootKeyAsync(cancellationToken);
                try
                {
                    rootKey = UnwrapRootKey(
                        envelope.WrappedKey,
                        ownerWindow,
                        useContext,
                        envelope.Padding);
                }
                finally
                {
                    Zero(envelope.WrappedKey);
                }

                // A legacy Hello-only envelope next to an active Node Lock would
                // be a bypass. Remove it only after both device and Hello gates
                // have successfully opened the same root key.
                DeleteFileIfExists(_wrappedRootKeyPath);
                await CompleteLegacyMigrationAsync(cancellationToken);
            }
            else if (File.Exists(_wrappedRootKeyPath) && hasCngKey)
            {
                WrappedRootKeyEnvelope envelope =
                    await ReadWrappedRootKeyAsync(cancellationToken);
                try
                {
                    rootKey = UnwrapRootKey(
                        envelope.WrappedKey,
                        ownerWindow,
                        useContext,
                        envelope.Padding);
                    await TryEnableNodeLockAsync(envelope, cancellationToken);
                }
                finally
                {
                    Zero(envelope.WrappedKey);
                }

                await CompleteLegacyMigrationAsync(cancellationToken);
            }
            else if (File.Exists(_legacyRootKeyPath))
            {
                rootKey = await MigrateLegacyRootKeyAsync(
                    ownerWindow,
                    useContext,
                    cancellationToken);
            }
            else if (File.Exists(_wrappedRootKeyPath))
            {
                throw new CryptographicException(
                    "The Windows Hello key for this Aero Vault is unavailable.");
            }
            else
            {
                rootKey = await CreateRootKeyAsync(
                    ownerWindow,
                    useContext,
                    cancellationToken);
            }

            if (rootKey.Length != RootKeyLength)
            {
                throw new CryptographicException("Aero Vault root key has an invalid length.");
            }

            RootKeyOperationLease lease = RootKeyOperationLease.From(rootKey);
            if (!lease.IsPageLocked)
            {
                WriteAuditEvent("memory-lock", "unavailable", origin: null);
            }
            WriteAuditEvent("unlock", "success", origin: null);
            return lease;
        }
        catch (PasswordVaultVerificationException)
        {
            _lastFailedPromptUtc = DateTimeOffset.UtcNow;
            WriteAuditEvent("unlock", "cancelled", origin: null);
            throw;
        }
        catch (InvalidDataException exception) when (File.Exists(_nodeLockPath))
        {
            _lastFailedPromptUtc = DateTimeOffset.UtcNow;
            WriteAuditEvent("node-lock", "invalid-envelope", origin: null);
            throw new PasswordVaultVerificationException(
                $"Aero Node Lock metadata is invalid: {exception.Message}");
        }
        catch (CryptographicException exception)
        {
            _lastFailedPromptUtc = DateTimeOffset.UtcNow;
            bool nodeLocked = File.Exists(_nodeLockPath);
            WriteAuditEvent(
                nodeLocked ? "node-lock" : "unlock",
                "failed",
                origin: null);
            throw new PasswordVaultVerificationException(
                nodeLocked
                    ? $"Aero Node Lock could not open this vault on this device: {exception.Message}"
                    : $"Windows Hello could not use the Aero Vault key: {exception.Message}");
        }
        finally
        {
            Zero(rootKey);
        }
    }

    private async Task<byte[]> CreateRootKeyAsync(
        IntPtr ownerWindow,
        string useContext,
        CancellationToken cancellationToken)
    {
        byte[] rootKey = RandomNumberGenerator.GetBytes(RootKeyLength);
        byte[]? wrapped = null;
        byte[]? verified = null;
        try
        {
            wrapped = WrapRootKey(rootKey, ownerWindow);
            verified = UnwrapRootKey(wrapped, ownerWindow, useContext);
            if (!CryptographicOperations.FixedTimeEquals(rootKey, verified))
            {
                throw new CryptographicException("Aero Vault key verification failed.");
            }

            await WritePreferredRootKeyEnvelopeAsync(
                wrapped,
                RootKeyWrapPadding.Pkcs1,
                cancellationToken);
            return rootKey;
        }
        catch
        {
            Zero(rootKey);
            throw;
        }
        finally
        {
            Zero(wrapped);
            Zero(verified);
        }
    }

    private async Task<byte[]> MigrateLegacyRootKeyAsync(
        IntPtr ownerWindow,
        string useContext,
        CancellationToken cancellationToken)
    {
        byte[] protectedKey = await File.ReadAllBytesAsync(
            _legacyRootKeyPath,
            cancellationToken);
        byte[]? rootKey = null;
        byte[]? wrapped = null;
        byte[]? verified = null;

        try
        {
            rootKey = ProtectedData.Unprotect(
                protectedKey,
                ApplicationEntropy,
                DataProtectionScope.CurrentUser);
            if (rootKey.Length != RootKeyLength)
            {
                throw new CryptographicException("Aero vault key has an invalid length.");
            }

            wrapped = WrapRootKey(rootKey, ownerWindow);
            verified = UnwrapRootKey(wrapped, ownerWindow, useContext);
            if (!CryptographicOperations.FixedTimeEquals(rootKey, verified))
            {
                throw new CryptographicException("Aero Vault migration verification failed.");
            }

            await WritePreferredRootKeyEnvelopeAsync(
                wrapped,
                RootKeyWrapPadding.Pkcs1,
                cancellationToken);
            await CompleteLegacyMigrationAsync(cancellationToken);
            WriteAuditEvent("migration", "success", origin: null);
            return rootKey;
        }
        catch
        {
            WriteAuditEvent("migration", "failed", origin: null);
            Zero(rootKey);
            throw;
        }
        finally
        {
            Zero(protectedKey);
            Zero(wrapped);
            Zero(verified);
        }
    }

    private async Task CompleteLegacyMigrationAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_vaultPath) && !await IsVaultV2Async(cancellationToken))
        {
            List<VaultRecord> records = await ReadRecordsAsync(cancellationToken);
            try
            {
                await WriteRecordsAsync(records, cancellationToken);
            }
            finally
            {
                ZeroRecords(records);
            }
        }

        // A surviving DPAPI key would defeat the new non-exportable-key boundary.
        // Treat deletion failure as an incomplete migration and retry next unlock.
        DeleteFileIfExists(_legacyRootKeyPath);
    }

    private byte[] WrapRootKey(byte[] rootKey, IntPtr ownerWindow)
    {
        // The Passport KSP implements RSA PKCS#1 encryption for its protected
        // decryption containers. OAEP-SHA256 is rejected on affected Windows
        // builds with NTE_INVALID_PARAMETER.
        string keyName = GetExistingHelloKeyName() ?? _cngKeyName;
        return WindowsHelloCngKey.EncryptPkcs1(keyName, rootKey, ownerWindow);
    }

    private byte[] UnwrapRootKey(
        byte[] wrappedRootKey,
        IntPtr ownerWindow,
        string useContext,
        RootKeyWrapPadding padding = RootKeyWrapPadding.Pkcs1)
    {
        string keyName = GetExistingHelloKeyName() ??
            throw new CryptographicException("The non-exportable Aero Vault key is missing.");
        return WindowsHelloCngKey.Decrypt(
            keyName,
            wrappedRootKey,
            ownerWindow,
            useContext,
            oaepSha256: padding == RootKeyWrapPadding.OaepSha256);
    }

    private string? GetExistingHelloKeyName()
    {
        if (WindowsHelloCngKey.Exists(_cngKeyName))
        {
            return _cngKeyName;
        }
        if (WindowsHelloCngKey.Exists(_transitionalCngKeyName))
        {
            return _transitionalCngKeyName;
        }
        if (WindowsHelloCngKey.Exists(_legacyCngKeyName))
        {
            return _legacyCngKeyName;
        }
        return null;
    }

    private bool DoesHelloKeyExist() => GetExistingHelloKeyName() is not null;

    private bool HasAnyRootKeyEnvelope() =>
        File.Exists(_nodeLockPath) ||
        File.Exists(_wrappedRootKeyPath) ||
        File.Exists(_legacyRootKeyPath);

    private async Task<WrappedRootKeyEnvelope> ReadWrappedRootKeyAsync(
        CancellationToken cancellationToken)
    {
        byte[] envelope = await File.ReadAllBytesAsync(_wrappedRootKeyPath, cancellationToken);
        try
        {
            if (envelope.Length < KeyEnvelopeMagic.Length + sizeof(int))
            {
                throw new InvalidDataException("Aero Vault key envelope is invalid.");
            }

            RootKeyWrapPadding padding;
            if (envelope.AsSpan(0, KeyEnvelopeMagic.Length).SequenceEqual(KeyEnvelopeMagic))
            {
                padding = RootKeyWrapPadding.Pkcs1;
            }
            else if (envelope.AsSpan(0, LegacyKeyEnvelopeMagic.Length)
                .SequenceEqual(LegacyKeyEnvelopeMagic))
            {
                padding = RootKeyWrapPadding.OaepSha256;
            }
            else
            {
                throw new InvalidDataException("Aero Vault key envelope has an unknown version.");
            }

            int length = BinaryPrimitives.ReadInt32LittleEndian(
                envelope.AsSpan(KeyEnvelopeMagic.Length, sizeof(int)));
            int offset = KeyEnvelopeMagic.Length + sizeof(int);
            if (length <= 0 || length > 16 * 1024 || length != envelope.Length - offset)
            {
                throw new InvalidDataException("Aero Vault key envelope is truncated.");
            }

            return new WrappedRootKeyEnvelope(
                envelope.AsSpan(offset, length).ToArray(),
                padding);
        }
        finally
        {
            Zero(envelope);
        }
    }

    private async Task WriteWrappedRootKeyAsync(
        byte[] wrappedRootKey,
        CancellationToken cancellationToken)
    {
        byte[] envelope = GC.AllocateUninitializedArray<byte>(
            KeyEnvelopeMagic.Length + sizeof(int) + wrappedRootKey.Length);
        try
        {
            KeyEnvelopeMagic.CopyTo(envelope, 0);
            BinaryPrimitives.WriteInt32LittleEndian(
                envelope.AsSpan(KeyEnvelopeMagic.Length, sizeof(int)),
                wrappedRootKey.Length);
            wrappedRootKey.CopyTo(envelope, KeyEnvelopeMagic.Length + sizeof(int));
            await WriteFileAtomicallyAsync(_wrappedRootKeyPath, envelope, cancellationToken);
        }
        finally
        {
            Zero(envelope);
        }
    }

    private async Task WritePreferredRootKeyEnvelopeAsync(
        byte[] wrappedRootKey,
        RootKeyWrapPadding padding,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteNodeLockedRootKeyAsync(
                wrappedRootKey,
                padding,
                cancellationToken);
            await VerifyNodeLockRoundTripAsync(
                wrappedRootKey,
                padding,
                cancellationToken);
            DeleteFileIfExists(_wrappedRootKeyPath);
            WriteAuditEvent("node-lock", "enabled-hardware", origin: null);
            return;
        }
        catch (OperationCanceledException)
        {
            DeleteFileIfExists(_nodeLockPath);
            // Keep a successfully created TPM key. Another Aero instance may
            // be completing the same first-time migration, and an orphaned
            // non-exportable key contains no Vault plaintext.
            throw;
        }
        catch
        {
            // TPM is an optional hardening layer during migration and first
            // creation. Never destroy access to an existing vault if the
            // provider is unavailable; keep the Hello-only envelope and report
            // the weaker state in the metadata-only audit.
            DeleteFileIfExists(_nodeLockPath);
            await WriteWrappedRootKeyAsync(wrappedRootKey, cancellationToken);
            WriteAuditEvent("node-lock", "hardware-unavailable", origin: null);
        }
    }

    private async Task TryEnableNodeLockAsync(
        WrappedRootKeyEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (File.Exists(_nodeLockPath))
        {
            return;
        }

        try
        {
            await WriteNodeLockedRootKeyAsync(
                envelope.WrappedKey,
                envelope.Padding,
                cancellationToken);
            await VerifyNodeLockRoundTripAsync(
                envelope.WrappedKey,
                envelope.Padding,
                cancellationToken);
            DeleteFileIfExists(_wrappedRootKeyPath);
            WriteAuditEvent("node-lock", "migrated-hardware", origin: null);
        }
        catch (OperationCanceledException)
        {
            DeleteFileIfExists(_nodeLockPath);
            // Do not delete a node key here: another Aero instance may have
            // already adopted it. The old Hello envelope is still authoritative.
            throw;
        }
        catch
        {
            // The old Hello envelope remains the source of truth. A partial
            // Node Lock file must never shadow it on the next operation.
            DeleteFileIfExists(_nodeLockPath);
            WriteAuditEvent("node-lock", "migration-deferred", origin: null);
        }
    }

    private async Task VerifyNodeLockRoundTripAsync(
        byte[] expectedWrappedRootKey,
        RootKeyWrapPadding expectedPadding,
        CancellationToken cancellationToken)
    {
        WrappedRootKeyEnvelope verified =
            await ReadNodeLockedRootKeyAsync(cancellationToken);
        try
        {
            if (verified.Padding != expectedPadding ||
                verified.WrappedKey.Length != expectedWrappedRootKey.Length ||
                !CryptographicOperations.FixedTimeEquals(
                    verified.WrappedKey,
                    expectedWrappedRootKey))
            {
                throw new CryptographicException(
                    "Aero Node Lock verification did not reproduce the protected root-key envelope.");
            }
        }
        finally
        {
            Zero(verified.WrappedKey);
        }
    }

    private async Task WriteNodeLockedRootKeyAsync(
        byte[] wrappedRootKey,
        RootKeyWrapPadding padding,
        CancellationToken cancellationToken)
    {
        byte[]? nodeId = null;
        byte[]? nodeWrappingKey = null;
        byte[]? wrappedNodeKey = null;
        byte[]? nonce = null;
        byte[]? tag = null;
        byte[]? ciphertext = null;
        byte[]? associatedData = null;
        byte[]? serialized = null;

        try
        {
            nodeId = DeviceNodeLockKey.GetOrCreateNodeId(_nodeLockCngKeyName);
            if (nodeId.Length != NodeIdLength)
            {
                throw new CryptographicException("Aero Node Lock produced an invalid node identifier.");
            }

            nodeWrappingKey = RandomNumberGenerator.GetBytes(NodeWrappingKeyLength);
            wrappedNodeKey = DeviceNodeLockKey.EncryptKeyMaterial(
                _nodeLockCngKeyName,
                nodeWrappingKey);
            nonce = RandomNumberGenerator.GetBytes(NodeNonceLength);
            tag = new byte[NodeTagLength];
            ciphertext = new byte[wrappedRootKey.Length];
            associatedData = BuildNodeLockAssociatedData(nodeId, padding);

            using (AesGcm aes = new(nodeWrappingKey, NodeTagLength))
            {
                aes.Encrypt(
                    nonce,
                    wrappedRootKey,
                    ciphertext,
                    tag,
                    associatedData);
            }

            const int lengthFieldCount = 6;
            int headerLength = checked(
                NodeLockEnvelopeMagic.Length + lengthFieldCount * sizeof(int));
            int totalLength = checked(
                headerLength + nodeId.Length + wrappedNodeKey.Length +
                nonce.Length + tag.Length + ciphertext.Length);
            if (totalLength > MaximumNodeLockEnvelopeLength)
            {
                throw new InvalidDataException("Aero Node Lock envelope is unexpectedly large.");
            }

            serialized = GC.AllocateUninitializedArray<byte>(totalLength);
            NodeLockEnvelopeMagic.CopyTo(serialized, 0);
            int offset = NodeLockEnvelopeMagic.Length;
            WriteInt32(serialized, ref offset, (int)padding);
            WriteInt32(serialized, ref offset, nodeId.Length);
            WriteInt32(serialized, ref offset, wrappedNodeKey.Length);
            WriteInt32(serialized, ref offset, nonce.Length);
            WriteInt32(serialized, ref offset, tag.Length);
            WriteInt32(serialized, ref offset, ciphertext.Length);
            CopyAndAdvance(nodeId, serialized, ref offset);
            CopyAndAdvance(wrappedNodeKey, serialized, ref offset);
            CopyAndAdvance(nonce, serialized, ref offset);
            CopyAndAdvance(tag, serialized, ref offset);
            CopyAndAdvance(ciphertext, serialized, ref offset);

            if (offset != serialized.Length)
            {
                throw new InvalidDataException("Aero Node Lock envelope length is inconsistent.");
            }

            await WriteFileAtomicallyAsync(_nodeLockPath, serialized, cancellationToken);
        }
        finally
        {
            Zero(nodeId);
            Zero(nodeWrappingKey);
            Zero(wrappedNodeKey);
            Zero(nonce);
            Zero(tag);
            Zero(ciphertext);
            Zero(associatedData);
            Zero(serialized);
        }
    }

    private async Task<WrappedRootKeyEnvelope> ReadNodeLockedRootKeyAsync(
        CancellationToken cancellationToken)
    {
        FileInfo file = new(_nodeLockPath);
        if (!file.Exists ||
            file.Length <= NodeLockEnvelopeMagic.Length ||
            file.Length > MaximumNodeLockEnvelopeLength)
        {
            throw new InvalidDataException("Aero Node Lock envelope is missing or invalid.");
        }

        byte[] serialized = await File.ReadAllBytesAsync(_nodeLockPath, cancellationToken);
        byte[]? nodeId = null;
        byte[]? wrappedNodeKey = null;
        byte[]? nonce = null;
        byte[]? tag = null;
        byte[]? ciphertext = null;
        byte[]? currentNodeId = null;
        byte[]? nodeWrappingKey = null;
        byte[]? associatedData = null;
        byte[]? wrappedRootKey = null;

        try
        {
            const int lengthFieldCount = 6;
            int minimumHeader = NodeLockEnvelopeMagic.Length + lengthFieldCount * sizeof(int);
            if (serialized.Length < minimumHeader ||
                !serialized.AsSpan(0, NodeLockEnvelopeMagic.Length)
                    .SequenceEqual(NodeLockEnvelopeMagic))
            {
                throw new InvalidDataException("Aero Node Lock envelope has an unknown version.");
            }

            int offset = NodeLockEnvelopeMagic.Length;
            int paddingValue = ReadInt32(serialized, ref offset);
            if (!Enum.IsDefined(typeof(RootKeyWrapPadding), paddingValue))
            {
                throw new InvalidDataException("Aero Node Lock contains an invalid root-key padding mode.");
            }
            RootKeyWrapPadding padding = (RootKeyWrapPadding)paddingValue;

            int nodeIdLength = ReadInt32(serialized, ref offset);
            int wrappedNodeKeyLength = ReadInt32(serialized, ref offset);
            int nonceLength = ReadInt32(serialized, ref offset);
            int tagLength = ReadInt32(serialized, ref offset);
            int ciphertextLength = ReadInt32(serialized, ref offset);
            if (nodeIdLength != NodeIdLength ||
                wrappedNodeKeyLength <= 0 || wrappedNodeKeyLength > 4096 ||
                nonceLength != NodeNonceLength ||
                tagLength != NodeTagLength ||
                ciphertextLength <= 0 || ciphertextLength > 16 * 1024)
            {
                throw new InvalidDataException("Aero Node Lock envelope contains invalid field lengths.");
            }

            int expectedLength = checked(
                offset + nodeIdLength + wrappedNodeKeyLength + nonceLength +
                tagLength + ciphertextLength);
            if (expectedLength != serialized.Length)
            {
                throw new InvalidDataException("Aero Node Lock envelope is truncated.");
            }

            nodeId = ReadAndAdvance(serialized, ref offset, nodeIdLength);
            wrappedNodeKey = ReadAndAdvance(serialized, ref offset, wrappedNodeKeyLength);
            nonce = ReadAndAdvance(serialized, ref offset, nonceLength);
            tag = ReadAndAdvance(serialized, ref offset, tagLength);
            ciphertext = ReadAndAdvance(serialized, ref offset, ciphertextLength);

            currentNodeId = DeviceNodeLockKey.GetExistingNodeId(_nodeLockCngKeyName);
            if (currentNodeId.Length != nodeId.Length ||
                !CryptographicOperations.FixedTimeEquals(currentNodeId, nodeId))
            {
                throw new CryptographicException(
                    "This Aero Vault is Node Locked to a different Windows device.");
            }

            nodeWrappingKey = DeviceNodeLockKey.DecryptKeyMaterial(
                _nodeLockCngKeyName,
                wrappedNodeKey);
            if (nodeWrappingKey.Length != NodeWrappingKeyLength)
            {
                throw new CryptographicException("Aero Node Lock returned an invalid wrapping key.");
            }

            associatedData = BuildNodeLockAssociatedData(nodeId, padding);
            wrappedRootKey = new byte[ciphertext.Length];
            using (AesGcm aes = new(nodeWrappingKey, NodeTagLength))
            {
                aes.Decrypt(
                    nonce,
                    ciphertext,
                    tag,
                    wrappedRootKey,
                    associatedData);
            }

            byte[] result = wrappedRootKey;
            wrappedRootKey = null;
            return new WrappedRootKeyEnvelope(result, padding);
        }
        catch (CryptographicException exception) when (
            !exception.Message.Contains("different Windows device", StringComparison.Ordinal))
        {
            throw new CryptographicException(
                "Aero Node Lock could not open this vault on the current TPM-backed node.",
                exception);
        }
        finally
        {
            Zero(serialized);
            Zero(nodeId);
            Zero(wrappedNodeKey);
            Zero(nonce);
            Zero(tag);
            Zero(ciphertext);
            Zero(currentNodeId);
            Zero(nodeWrappingKey);
            Zero(associatedData);
            Zero(wrappedRootKey);
        }
    }

    private static byte[] BuildNodeLockAssociatedData(
        ReadOnlySpan<byte> nodeId,
        RootKeyWrapPadding padding)
    {
        byte[] associatedData = GC.AllocateUninitializedArray<byte>(
            NodeLockAadLabel.Length + NodeLockEnvelopeMagic.Length +
            sizeof(int) + nodeId.Length);
        int offset = 0;
        NodeLockAadLabel.CopyTo(associatedData, offset);
        offset += NodeLockAadLabel.Length;
        NodeLockEnvelopeMagic.CopyTo(associatedData, offset);
        offset += NodeLockEnvelopeMagic.Length;
        BinaryPrimitives.WriteInt32LittleEndian(
            associatedData.AsSpan(offset, sizeof(int)),
            (int)padding);
        offset += sizeof(int);
        nodeId.CopyTo(associatedData.AsSpan(offset));
        return associatedData;
    }

    private static void WriteInt32(byte[] destination, ref int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(
            destination.AsSpan(offset, sizeof(int)),
            value);
        offset += sizeof(int);
    }

    private static int ReadInt32(byte[] source, ref int offset)
    {
        if (offset < 0 || source.Length - offset < sizeof(int))
        {
            throw new InvalidDataException("Aero Node Lock envelope is truncated.");
        }

        int value = BinaryPrimitives.ReadInt32LittleEndian(
            source.AsSpan(offset, sizeof(int)));
        offset += sizeof(int);
        return value;
    }

    private static void CopyAndAdvance(
        ReadOnlySpan<byte> source,
        byte[] destination,
        ref int offset)
    {
        source.CopyTo(destination.AsSpan(offset, source.Length));
        offset += source.Length;
    }

    private static byte[] ReadAndAdvance(
        byte[] source,
        ref int offset,
        int length)
    {
        if (length < 0 || offset < 0 || source.Length - offset < length)
        {
            throw new InvalidDataException("Aero Node Lock envelope is truncated.");
        }

        byte[] value = source.AsSpan(offset, length).ToArray();
        offset += length;
        return value;
    }

    private async Task<List<VaultRecord>> ReadRecordsAsync(CancellationToken cancellationToken)
    {
        List<VaultRecord> records = [];
        if (!File.Exists(_vaultPath))
        {
            return records;
        }

        FileInfo fileInfo = new(_vaultPath);
        if (fileInfo.Length > MaximumVaultLength)
        {
            throw new InvalidDataException("Aero password vault is unexpectedly large.");
        }

        byte[] data = await File.ReadAllBytesAsync(_vaultPath, cancellationToken);

        try
        {
            int offset = data.Length >= VaultMagic.Length &&
                data.AsSpan(0, VaultMagic.Length).SequenceEqual(VaultMagic)
                    ? VaultMagic.Length
                    : 0;
            while (offset < data.Length)
            {
                if (data.Length - offset < OriginTagLength + sizeof(int))
                {
                    throw new InvalidDataException("Aero password vault is truncated.");
                }

                byte[] tag = data.AsSpan(offset, OriginTagLength).ToArray();
                offset += OriginTagLength;

                int ciphertextLength = BinaryPrimitives.ReadInt32LittleEndian(
                    data.AsSpan(offset, sizeof(int)));
                offset += sizeof(int);

                if (ciphertextLength <= 0 ||
                    ciphertextLength > MaximumRecordLength ||
                    ciphertextLength > data.Length - offset)
                {
                    Zero(tag);
                    throw new InvalidDataException("Aero password vault contains an invalid record.");
                }

                byte[] ciphertext = data.AsSpan(offset, ciphertextLength).ToArray();
                offset += ciphertextLength;
                records.Add(new VaultRecord(tag, ciphertext));
            }

            return records;
        }
        catch
        {
            ZeroRecords(records);
            throw;
        }
        finally
        {
            Zero(data);
        }
    }

    private async Task WriteRecordsAsync(
        List<VaultRecord> records,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            DeleteFileIfExists(_vaultPath);
            return;
        }

        int totalLength = VaultMagic.Length;
        foreach (VaultRecord record in records)
        {
            totalLength = checked(
                totalLength + OriginTagLength + sizeof(int) + record.Ciphertext.Length);
        }

        byte[] data = GC.AllocateUninitializedArray<byte>(totalLength);

        try
        {
            VaultMagic.CopyTo(data, 0);
            int offset = VaultMagic.Length;
            foreach (VaultRecord record in records)
            {
                if (record.OriginTag.Length != OriginTagLength)
                {
                    throw new InvalidDataException("Invalid Aero vault origin tag.");
                }

                record.OriginTag.CopyTo(data, offset);
                offset += OriginTagLength;
                BinaryPrimitives.WriteInt32LittleEndian(
                    data.AsSpan(offset, sizeof(int)),
                    record.Ciphertext.Length);
                offset += sizeof(int);
                record.Ciphertext.CopyTo(data, offset);
                offset += record.Ciphertext.Length;
            }

            await WriteFileAtomicallyAsync(_vaultPath, data, cancellationToken);
        }
        finally
        {
            Zero(data);
        }
    }

    private async Task<bool> IsVaultV2Async(CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            _vaultPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            VaultMagic.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] header = new byte[VaultMagic.Length];
        try
        {
            int read = await stream.ReadAsync(header, cancellationToken);
            return read == header.Length && header.AsSpan().SequenceEqual(VaultMagic);
        }
        finally
        {
            Zero(header);
        }
    }

    private void DeleteCngKey()
    {
        WindowsHelloCngKey.Delete(_cngKeyName);
        WindowsHelloCngKey.Delete(_transitionalCngKeyName);
        WindowsHelloCngKey.Delete(_legacyCngKeyName);
        DeviceNodeLockKey.Delete(_nodeLockCngKeyName);
    }

    private void WriteAuditEvent(string action, string outcome, string? origin)
    {
        try
        {
            string? originFingerprint = null;
            if (!string.IsNullOrWhiteSpace(origin))
            {
                byte[] originBytes = Encoding.UTF8.GetBytes(origin.Trim().ToLowerInvariant());
                byte[] hash = SHA256.HashData(originBytes);
                try
                {
                    originFingerprint = Convert.ToHexString(hash.AsSpan(0, 12));
                }
                finally
                {
                    Zero(originBytes);
                    Zero(hash);
                }
            }

            string json = JsonSerializer.Serialize(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                sequence = Interlocked.Increment(ref _auditSequence),
                session = _processSessionId,
                action = SanitizeAuditToken(action),
                outcome = SanitizeAuditToken(outcome),
                originFingerprint
            });

            lock (_auditGate)
            {
                if (File.Exists(_auditPath) && new FileInfo(_auditPath).Length > MaximumAuditLength)
                {
                    File.Move(_auditPath, _auditPath + ".previous", overwrite: true);
                }

                File.AppendAllText(_auditPath, json + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Audit availability must never expose or block the protected operation.
        }
    }

    private static string SanitizeAuditToken(string value) =>
        new(value
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            .Take(32)
            .ToArray());

    private static byte[] BuildPayload(
        ReadOnlySpan<byte> origin,
        ReadOnlySpan<byte> username,
        ReadOnlySpan<byte> password)
    {
        int length = checked(
            sizeof(int) * 3 + origin.Length + username.Length + password.Length);
        byte[] payload = GC.AllocateUninitializedArray<byte>(length);
        int offset = 0;

        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, sizeof(int)), origin.Length);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, sizeof(int)), username.Length);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, sizeof(int)), password.Length);
        offset += sizeof(int);

        origin.CopyTo(payload.AsSpan(offset, origin.Length));
        offset += origin.Length;
        username.CopyTo(payload.AsSpan(offset, username.Length));
        offset += username.Length;
        password.CopyTo(payload.AsSpan(offset, password.Length));
        return payload;
    }

    private static PasswordMetadataEntryLease ParseMetadataPayload(byte[] payload)
    {
        if (payload.Length < sizeof(int) * 3)
        {
            throw new InvalidDataException("Aero password record is truncated.");
        }

        int offset = 0;
        int originLength = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, sizeof(int)));
        offset += sizeof(int);
        int usernameLength = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, sizeof(int)));
        offset += sizeof(int);
        int passwordLength = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, sizeof(int)));
        offset += sizeof(int);

        if (originLength <= 0 || usernameLength < 0 || passwordLength <= 0)
        {
            throw new InvalidDataException("Aero password record lengths are invalid.");
        }

        int required = checked(offset + originLength + usernameLength + passwordLength);
        if (required != payload.Length)
        {
            throw new InvalidDataException("Aero password record length does not match its payload.");
        }

        byte[] originBytes = payload.AsSpan(offset, originLength).ToArray();
        offset += originLength;
        byte[] usernameBytes = payload.AsSpan(offset, usernameLength).ToArray();
        return new PasswordMetadataEntryLease(originBytes, usernameBytes);
    }

    private static PasswordExportEntryLease ParseExportPayload(byte[] payload)
    {
        if (payload.Length < sizeof(int) * 3)
        {
            throw new InvalidDataException("Aero password record is truncated.");
        }

        int offset = 0;
        int originLength = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, sizeof(int)));
        offset += sizeof(int);
        int usernameLength = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, sizeof(int)));
        offset += sizeof(int);
        int passwordLength = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, sizeof(int)));
        offset += sizeof(int);

        if (originLength <= 0 || usernameLength < 0 || passwordLength <= 0)
        {
            throw new InvalidDataException("Aero password record lengths are invalid.");
        }

        int required = checked(offset + originLength + usernameLength + passwordLength);
        if (required != payload.Length)
        {
            throw new InvalidDataException("Aero password record length does not match its payload.");
        }

        byte[] originBytes = payload.AsSpan(offset, originLength).ToArray();
        offset += originLength;
        byte[] usernameBytes = payload.AsSpan(offset, usernameLength).ToArray();
        offset += usernameLength;
        byte[] passwordBytes = payload.AsSpan(offset, passwordLength).ToArray();
        return new PasswordExportEntryLease(originBytes, usernameBytes, passwordBytes);
    }

    private static PasswordLease ParsePayload(byte[] payload, string expectedOrigin)
    {
        if (payload.Length < sizeof(int) * 3)
        {
            throw new InvalidDataException("Aero password record is truncated.");
        }

        int offset = 0;
        int originLength = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, sizeof(int)));
        offset += sizeof(int);
        int usernameLength = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, sizeof(int)));
        offset += sizeof(int);
        int passwordLength = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, sizeof(int)));
        offset += sizeof(int);

        if (originLength <= 0 || usernameLength < 0 || passwordLength <= 0)
        {
            throw new InvalidDataException("Aero password record lengths are invalid.");
        }

        int required = checked(offset + originLength + usernameLength + passwordLength);
        if (required != payload.Length)
        {
            throw new InvalidDataException("Aero password record length does not match its payload.");
        }

        byte[] originBytes = payload.AsSpan(offset, originLength).ToArray();
        offset += originLength;
        byte[] usernameBytes = payload.AsSpan(offset, usernameLength).ToArray();
        offset += usernameLength;
        byte[] passwordBytes = payload.AsSpan(offset, passwordLength).ToArray();

        try
        {
            string storedOrigin = Encoding.UTF8.GetString(originBytes);
            if (!storedOrigin.Equals(expectedOrigin, StringComparison.OrdinalIgnoreCase))
            {
                Zero(usernameBytes);
                Zero(passwordBytes);
                throw new CryptographicException("Aero password origin verification failed.");
            }

            return new PasswordLease(usernameBytes, passwordBytes);
        }
        finally
        {
            Zero(originBytes);
        }
    }

    private static async Task WriteFileAtomicallyAsync(
        string destination,
        byte[] data,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("Aero vault directory is unavailable.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");

        try
        {
            await using (FileStream stream = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(data, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static int FindRecordIndex(List<VaultRecord> records, byte[] tag)
    {
        for (int index = 0; index < records.Count; index++)
        {
            if (records[index].OriginTag.Length == tag.Length &&
                CryptographicOperations.FixedTimeEquals(records[index].OriginTag, tag))
            {
                return index;
            }
        }

        return -1;
    }

    public static string NormalizeHttpsOrigin(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.IdnHost) ||
            uri.UserInfo.Length > 0)
        {
            throw new ArgumentException("Aero password vault accepts HTTPS origins only.", nameof(value));
        }

        if (IsSuspiciousHost(uri.IdnHost, out string reason))
        {
            throw new PasswordVaultSecurityException(reason);
        }

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/').ToLowerInvariant();
    }

    public static bool IsSuspiciousHost(string? value, out string reason)
    {
        reason = string.Empty;
        string host = value?.Trim().TrimEnd('.').ToLowerInvariant() ?? string.Empty;
        if (host.Length == 0 || Uri.CheckHostName(host) != UriHostNameType.Dns)
        {
            reason = "Aero Vault does not fill passwords on IP addresses or invalid hosts.";
            return true;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Aero Vault does not fill passwords on local or development hosts.";
            return true;
        }

        string[] labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Any(label => label.StartsWith("xn--", StringComparison.OrdinalIgnoreCase)))
        {
            reason = "Aero Vault blocked this internationalized domain because it may imitate another site.";
            return true;
        }

        if (labels.Length > 6 || host.Count(character => character == '-') >= 5)
        {
            reason = "Aero Vault blocked an unusually complex domain name.";
            return true;
        }

        int registrableIndex = Math.Max(0, labels.Length - 2);
        if (labels.Length >= 3 &&
            labels[^1].Length == 2 &&
            labels[^2] is "co" or "com" or "net" or "org")
        {
            registrableIndex = labels.Length - 3;
        }

        string registrableLabel = labels[registrableIndex];
        string skeleton = registrableLabel
            .Replace('0', 'o')
            .Replace('1', 'l')
            .Replace('3', 'e')
            .Replace('5', 's');
        string[] protectedBrands =
        [
            "aero", "amazon", "apple", "facebook", "github", "google",
            "instagram", "microsoft", "netflix", "paypal"
        ];

        foreach (string brand in protectedBrands)
        {
            if (!registrableLabel.Equals(brand, StringComparison.Ordinal) &&
                (skeleton.Equals(brand, StringComparison.Ordinal) ||
                 registrableLabel.StartsWith(brand + '-', StringComparison.Ordinal) ||
                 registrableLabel.EndsWith('-' + brand, StringComparison.Ordinal) ||
                 EditDistanceAtMostOne(skeleton, brand)))
            {
                reason = $"Aero Vault blocked a domain that resembles {brand}.";
                return true;
            }

            for (int index = 0; index < registrableIndex; index++)
            {
                string label = labels[index];
                if (label.Equals(brand, StringComparison.Ordinal) ||
                    label.StartsWith(brand + '-', StringComparison.Ordinal) ||
                    label.EndsWith('-' + brand, StringComparison.Ordinal))
                {
                    reason = $"Aero Vault blocked a misleading {brand} label outside the site's registered domain.";
                    return true;
                }
            }
        }

        return false;
    }

    private static bool EditDistanceAtMostOne(string left, string right)
    {
        if (Math.Abs(left.Length - right.Length) > 1)
        {
            return false;
        }

        int leftIndex = 0;
        int rightIndex = 0;
        int edits = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            if (left[leftIndex] == right[rightIndex])
            {
                leftIndex++;
                rightIndex++;
                continue;
            }

            if (++edits > 1)
            {
                return false;
            }

            if (left.Length > right.Length)
            {
                leftIndex++;
            }
            else if (right.Length > left.Length)
            {
                rightIndex++;
            }
            else
            {
                leftIndex++;
                rightIndex++;
            }
        }

        edits += left.Length - leftIndex + right.Length - rightIndex;
        return edits == 1;
    }

    private static void Zero(byte[]? buffer)
    {
        if (buffer is not null)
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static void ZeroRecord(VaultRecord record)
    {
        Zero(record.OriginTag);
        Zero(record.Ciphertext);
    }

    private static void ZeroRecords(IEnumerable<VaultRecord> records)
    {
        foreach (VaultRecord record in records)
        {
            ZeroRecord(record);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cleanup is best effort; the original operation remains authoritative.
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    public sealed class PasswordVaultWriteSession : IDisposable, IAsyncDisposable
    {
        private const int MaximumWrites = 5000;
        private static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(5);

        private PasswordVaultService? _owner;
        private RootKeyOperationLease? _rootKey;
        private List<VaultRecord>? _records;
        private readonly DateTimeOffset _expiresUtc = DateTimeOffset.UtcNow + MaximumLifetime;
        private string? _lastOrigin;
        private int _writeCount;
        private bool _committed;

        internal PasswordVaultWriteSession(
            PasswordVaultService owner,
            RootKeyOperationLease rootKey,
            List<VaultRecord> records)
        {
            _owner = owner;
            _rootKey = rootKey;
            _records = records;
        }

        public Task SavePasswordAsync(
            string origin,
            ReadOnlyMemory<byte> usernameUtf8,
            ReadOnlyMemory<byte> passwordUtf8,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureWritable();
            if (_writeCount >= MaximumWrites)
            {
                throw new InvalidDataException(
                    $"Aero Vault write sessions are limited to {MaximumWrites} records.");
            }

            PasswordVaultService owner = _owner!;
            RootKeyOperationLease rootKey = _rootKey!;
            List<VaultRecord> records = _records!;
            _lastOrigin = owner.StagePasswordWithOperationKey(
                rootKey,
                records,
                origin,
                usernameUtf8,
                passwordUtf8);
            _writeCount++;
            return Task.CompletedTask;
        }

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            EnsureWritable();
            PasswordVaultService owner = _owner!;
            List<VaultRecord> records = _records!;
            try
            {
                await owner.WriteRecordsAsync(records, cancellationToken);
                _committed = true;
                owner.WriteAuditEvent(
                    _writeCount == 1 ? "save" : "save-batch",
                    "success",
                    _writeCount == 1 ? _lastOrigin : null);
            }
            catch
            {
                owner.WriteAuditEvent(
                    _writeCount == 1 ? "save" : "save-batch",
                    "failed",
                    _writeCount == 1 ? _lastOrigin : null);
                throw;
            }
        }

        private void EnsureWritable()
        {
            ObjectDisposedException.ThrowIf(
                _owner is null || _rootKey is null || _records is null,
                this);
            if (_committed)
            {
                throw new InvalidOperationException("This Aero Vault write session was already committed.");
            }
            if (DateTimeOffset.UtcNow >= _expiresUtc)
            {
                throw new PasswordVaultVerificationException(
                    "The protected write session expired. Start the operation again.");
            }
        }

        public void Dispose()
        {
            PasswordVaultService? owner = Interlocked.Exchange(ref _owner, null);
            RootKeyOperationLease? rootKey = Interlocked.Exchange(ref _rootKey, null);
            List<VaultRecord>? records = Interlocked.Exchange(ref _records, null);
            rootKey?.Dispose();
            if (records is not null)
            {
                ZeroRecords(records);
            }

            if (owner is not null)
            {
                owner.WriteAuditEvent(
                    "write-session",
                    _committed ? "committed-closed" : "discarded",
                    origin: null);
                try
                {
                    owner._gate.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Process shutdown already destroyed the service gate.
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class RootKeyOperationLease : IDisposable
    {
        private SecureBuffer? _key;

        private RootKeyOperationLease(SecureBuffer key)
        {
            _key = key;
        }

        public ReadOnlySpan<byte> Span => (_key
            ?? throw new ObjectDisposedException(nameof(RootKeyOperationLease))).ReadOnlySpan;

        public bool IsPageLocked => (_key
            ?? throw new ObjectDisposedException(nameof(RootKeyOperationLease))).IsPageLocked;

        public static RootKeyOperationLease From(ReadOnlySpan<byte> key) =>
            new(SecureBuffer.From(key));

        public void Dispose()
        {
            SecureBuffer? key = Interlocked.Exchange(ref _key, null);
            key?.Dispose();
        }
    }

    internal sealed record VaultRecord(byte[] OriginTag, byte[] Ciphertext);

    private sealed record WrappedRootKeyEnvelope(
        byte[] WrappedKey,
        RootKeyWrapPadding Padding);

    private enum RootKeyWrapPadding
    {
        Pkcs1,
        OaepSha256
    }
}

public enum PasswordVaultLookupState
{
    Locked,
    NotFound,
    Found
}

public sealed class PasswordLease : IDisposable
{
    private SecureBuffer? _usernameUtf8;
    private SecureBuffer? _passwordUtf8;

    internal PasswordLease(byte[] usernameUtf8, byte[] passwordUtf8)
    {
        try
        {
            _usernameUtf8 = SecureBuffer.From(usernameUtf8);
            _passwordUtf8 = SecureBuffer.From(passwordUtf8);
        }
        catch
        {
            _usernameUtf8?.Dispose();
            _usernameUtf8 = null;
            _passwordUtf8?.Dispose();
            _passwordUtf8 = null;
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(usernameUtf8);
            CryptographicOperations.ZeroMemory(passwordUtf8);
        }
    }

    public ReadOnlySpan<byte> UsernameUtf8
    {
        get
        {
            SecureBuffer buffer = _usernameUtf8
                ?? throw new ObjectDisposedException(nameof(PasswordLease));
            return buffer.ReadOnlySpan;
        }
    }

    public ReadOnlySpan<byte> PasswordUtf8
    {
        get
        {
            SecureBuffer buffer = _passwordUtf8
                ?? throw new ObjectDisposedException(nameof(PasswordLease));
            return buffer.ReadOnlySpan;
        }
    }

    public bool IsPageLocked =>
        (_usernameUtf8?.IsPageLocked ?? false) &&
        (_passwordUtf8?.IsPageLocked ?? false);

    public void Dispose()
    {
        SecureBuffer? username = Interlocked.Exchange(ref _usernameUtf8, null);
        SecureBuffer? password = Interlocked.Exchange(ref _passwordUtf8, null);
        username?.Dispose();
        password?.Dispose();
    }
}

public sealed class PasswordMetadataListLease : IDisposable
{
    private List<PasswordMetadataEntryLease>? _entries;

    internal PasswordMetadataListLease(List<PasswordMetadataEntryLease> entries)
    {
        _entries = entries;
    }

    public IReadOnlyList<PasswordMetadataEntryLease> Entries => _entries
        ?? throw new ObjectDisposedException(nameof(PasswordMetadataListLease));

    public void Dispose()
    {
        List<PasswordMetadataEntryLease>? entries = Interlocked.Exchange(ref _entries, null);
        if (entries is null)
        {
            return;
        }

        foreach (PasswordMetadataEntryLease entry in entries)
        {
            entry.Dispose();
        }

        entries.Clear();
    }
}

public sealed class PasswordMetadataEntryLease : IDisposable
{
    private byte[]? _originUtf8;
    private byte[]? _usernameUtf8;

    internal PasswordMetadataEntryLease(byte[] originUtf8, byte[] usernameUtf8)
    {
        _originUtf8 = originUtf8;
        _usernameUtf8 = usernameUtf8;
    }

    public ReadOnlyMemory<byte> OriginUtf8 => _originUtf8
        ?? throw new ObjectDisposedException(nameof(PasswordMetadataEntryLease));

    public ReadOnlyMemory<byte> UsernameUtf8 => _usernameUtf8
        ?? throw new ObjectDisposedException(nameof(PasswordMetadataEntryLease));

    public void Dispose()
    {
        byte[]? origin = Interlocked.Exchange(ref _originUtf8, null);
        byte[]? username = Interlocked.Exchange(ref _usernameUtf8, null);

        if (origin is not null)
        {
            CryptographicOperations.ZeroMemory(origin);
        }

        if (username is not null)
        {
            CryptographicOperations.ZeroMemory(username);
        }
    }
}

public sealed class PasswordExportLease : IDisposable
{
    private List<PasswordExportEntryLease>? _entries;

    internal PasswordExportLease(List<PasswordExportEntryLease> entries)
    {
        _entries = entries;
    }

    public IReadOnlyList<PasswordExportEntryLease> Entries => _entries
        ?? throw new ObjectDisposedException(nameof(PasswordExportLease));

    public void Dispose()
    {
        List<PasswordExportEntryLease>? entries = Interlocked.Exchange(ref _entries, null);
        if (entries is null)
        {
            return;
        }

        foreach (PasswordExportEntryLease entry in entries)
        {
            entry.Dispose();
        }

        entries.Clear();
    }
}

public sealed class PasswordExportEntryLease : IDisposable
{
    private byte[]? _originUtf8;
    private byte[]? _usernameUtf8;
    private SecureBuffer? _passwordUtf8;

    internal PasswordExportEntryLease(
        byte[] originUtf8,
        byte[] usernameUtf8,
        byte[] passwordUtf8)
    {
        _originUtf8 = originUtf8;
        _usernameUtf8 = usernameUtf8;
        try
        {
            _passwordUtf8 = SecureBuffer.From(passwordUtf8);
        }
        catch
        {
            _passwordUtf8?.Dispose();
            _passwordUtf8 = null;
            CryptographicOperations.ZeroMemory(originUtf8);
            CryptographicOperations.ZeroMemory(usernameUtf8);
            _originUtf8 = null;
            _usernameUtf8 = null;
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordUtf8);
        }
    }

    public ReadOnlyMemory<byte> OriginUtf8 => _originUtf8
        ?? throw new ObjectDisposedException(nameof(PasswordExportEntryLease));

    public ReadOnlyMemory<byte> UsernameUtf8 => _usernameUtf8
        ?? throw new ObjectDisposedException(nameof(PasswordExportEntryLease));

    public ReadOnlySpan<byte> PasswordUtf8
    {
        get
        {
            SecureBuffer buffer = _passwordUtf8
                ?? throw new ObjectDisposedException(nameof(PasswordExportEntryLease));
            return buffer.ReadOnlySpan;
        }
    }

    public bool IsPasswordPageLocked => _passwordUtf8?.IsPageLocked ?? false;

    public void Dispose()
    {
        byte[]? origin = Interlocked.Exchange(ref _originUtf8, null);
        byte[]? username = Interlocked.Exchange(ref _usernameUtf8, null);
        SecureBuffer? password = Interlocked.Exchange(ref _passwordUtf8, null);

        if (origin is not null)
        {
            CryptographicOperations.ZeroMemory(origin);
        }
        if (username is not null)
        {
            CryptographicOperations.ZeroMemory(username);
        }
        password?.Dispose();
    }
}

public sealed class PasswordVaultVerificationException : Exception
{
    public PasswordVaultVerificationException(string message)
        : base(message)
    {
    }
}

public sealed class PasswordVaultSecurityException : Exception
{
    public PasswordVaultSecurityException(string message)
        : base(message)
    {
    }
}
