// Aero Vault. Vault is Powered by Aero.
// Copyright (c) 2026 finchproffe. Licensed under the Mozilla Public License 2.0.
// Under MPL-2.0 section 3.4 this notice must stay in every copy and fork of this file.
using Microsoft.Win32.SafeHandles;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Aero.App.Security;

/// <summary>
/// Minimal native CNG wrapper for persistent Microsoft Passport KSP keys.
/// The Passport provider has provider-specific creation properties that are
/// more reliable when applied before NCryptFinalizeKey.
/// </summary>
internal static class WindowsHelloCngKey
{
    private const string ProviderName = "Microsoft Passport Key Storage Provider";
    private const string RsaAlgorithm = "RSA";
    private const string Sha256Algorithm = "SHA256";
    private const int NcryptAllowDecryptFlag = 0x00000001;
    private const int NcryptAllowSigningFlag = 0x00000002;
    private const int NcryptAllowKeyImportFlag = 0x00000008;
    private const int NcryptPadPkcs1Flag = 0x00000002;
    private const int NcryptPadOaepFlag = 0x00000004;
    private const int AuthenticationMandatory = 0x00000001;
    private const int NteBadData = unchecked((int)0x80090005);
    private const int NteNoKey = unchecked((int)0x8009000D);
    private const int NteBadKeyset = unchecked((int)0x80090016);
    private const int NteInvalidParameter = unchecked((int)0x80090027);

    public static bool Exists(string keyName)
    {
        SafeNCryptKeyHandle? key = TryOpen(keyName);
        key?.Dispose();
        return key is not null;
    }

    public static byte[] EncryptPkcs1(
        string keyName,
        byte[] plaintext,
        IntPtr ownerWindow) =>
        RunOnInteractiveStaThread(() =>
        {
            using SafeNCryptKeyHandle key = OpenOrCreate(keyName, ownerWindow);
            return Transform(
                key,
                plaintext,
                decrypt: false,
                NcryptPadPkcs1Flag,
                paddingInfo: IntPtr.Zero);
        });

    public static byte[] Decrypt(
        string keyName,
        byte[] ciphertext,
        IntPtr ownerWindow,
        string useContext,
        bool oaepSha256) =>
        RunOnInteractiveStaThread(() =>
        {
            using SafeNCryptKeyHandle key = TryOpen(keyName) ??
                throw new CryptographicException("The non-exportable Aero Vault key is missing.");

            ApplyUiContext(key, ownerWindow, useContext);
            SetIntProperty(key, "PinCacheIsGestureRequired", 1);

            if (!oaepSha256)
            {
                return Transform(
                    key,
                    ciphertext,
                    decrypt: true,
                    NcryptPadPkcs1Flag,
                    paddingInfo: IntPtr.Zero);
            }

            IntPtr algorithm = Marshal.StringToHGlobalUni(Sha256Algorithm);
            IntPtr paddingInfo = IntPtr.Zero;
            try
            {
                BCryptOaepPaddingInfo info = new()
                {
                    AlgorithmId = algorithm,
                    Label = IntPtr.Zero,
                    LabelLength = 0
                };
                paddingInfo = Marshal.AllocHGlobal(Marshal.SizeOf<BCryptOaepPaddingInfo>());
                Marshal.StructureToPtr(info, paddingInfo, false);
                return Transform(
                    key,
                    ciphertext,
                    decrypt: true,
                    NcryptPadOaepFlag,
                    paddingInfo);
            }
            finally
            {
                if (paddingInfo != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(paddingInfo);
                }
                Marshal.FreeHGlobal(algorithm);
            }
        });

    private static byte[] RunOnInteractiveStaThread(Func<byte[]> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return operation();
        }

        byte[]? result = null;
        ExceptionDispatchInfo? failure = null;
        Thread staThread = new(() =>
        {
            try
            {
                result = operation();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Aero Windows Hello operation"
        };

        if (!staThread.TrySetApartmentState(ApartmentState.STA))
        {
            throw new CryptographicException(
                "Aero Vault could not create an STA thread for Windows Hello.");
        }

        staThread.Start();
        staThread.Join();
        failure?.Throw();
        return result ?? throw new CryptographicException(
            "Aero Vault Windows Hello operation returned no result.");
    }

    public static void Delete(string keyName)
    {
        using SafeNCryptKeyHandle? key = TryOpen(keyName);
        if (key is null)
        {
            return;
        }

        ThrowIfFailed(NCryptDeleteKey(key, 0), "NCryptDeleteKey");
        key.SetHandleAsInvalid();
    }

    private static SafeNCryptKeyHandle OpenOrCreate(string keyName, IntPtr ownerWindow)
    {
        SafeNCryptKeyHandle? existing = TryOpen(keyName);
        if (existing is not null)
        {
            ApplyWindowHandle(existing, ownerWindow);
            VerifyKey(existing);
            return existing;
        }

        ThrowIfFailed(
            NCryptOpenStorageProvider(out SafeNCryptProviderHandle provider, ProviderName, 0),
            "NCryptOpenStorageProvider");
        using (provider)
        {
            ThrowIfFailed(
                NCryptCreatePersistedKey(
                    provider,
                    out SafeNCryptKeyHandle key,
                    RsaAlgorithm,
                    keyName,
                    0,
                    0),
                "NCryptCreatePersistedKey");

            try
            {
                SetIntProperty(key, "Length", 2048);
                SetIntProperty(
                    key,
                    "Key Usage",
                    NcryptAllowDecryptFlag | NcryptAllowSigningFlag);
                int cacheStatus = SetIntPropertyUnchecked(key, "NgcCacheType", AuthenticationMandatory);
                if (cacheStatus < 0)
                {
                    ThrowIfFailed(
                        SetIntPropertyUnchecked(
                            key,
                            "NgcCacheTypeProperty",
                            AuthenticationMandatory),
                        "NCryptSetProperty(NgcCacheTypeProperty)");
                }
                ApplyWindowHandle(key, ownerWindow);
                ThrowIfFailed(NCryptFinalizeKey(key, 0), "NCryptFinalizeKey");
                VerifyKey(key);
                return key;
            }
            catch
            {
                key.Dispose();
                throw;
            }
        }
    }

    private static SafeNCryptKeyHandle? TryOpen(string keyName)
    {
        ThrowIfFailed(
            NCryptOpenStorageProvider(out SafeNCryptProviderHandle provider, ProviderName, 0),
            "NCryptOpenStorageProvider");
        using (provider)
        {
            int status = NCryptOpenKey(provider, out SafeNCryptKeyHandle key, keyName, 0, 0);
            // Microsoft Passport KSP returns NTE_INVALID_PARAMETER on some
            // Windows builds when a correctly-shaped Hello container name is
            // queried before that container has ever been created. Treat that
            // provider-specific result as "not found" so OpenOrCreate can
            // proceed to NCryptCreatePersistedKey.
            if (status is NteNoKey or NteBadKeyset or NteInvalidParameter)
            {
                key?.Dispose();
                return null;
            }

            ThrowIfFailed(status, "NCryptOpenKey");
            return key;
        }
    }

    private static void VerifyKey(SafeNCryptKeyHandle key)
    {
        int usage = GetIntProperty(key, "Key Usage");
        if ((usage & NcryptAllowDecryptFlag) == 0 ||
            (usage & NcryptAllowKeyImportFlag) != 0)
        {
            throw new CryptographicException(
                "Aero Vault refused a CNG key with unsafe usage flags.");
        }

        int cacheType;
        try
        {
            cacheType = GetIntProperty(key, "NgcCacheType");
        }
        catch (CryptographicException)
        {
            cacheType = GetIntProperty(key, "NgcCacheTypeProperty");
        }
        if (cacheType != AuthenticationMandatory)
        {
            throw new CryptographicException(
                "The Aero Vault key does not require a Windows Hello gesture.");
        }
    }

    private static void ApplyUiContext(
        SafeNCryptKeyHandle key,
        IntPtr ownerWindow,
        string useContext)
    {
        ApplyWindowHandle(key, ownerWindow);
        byte[] context = Encoding.Unicode.GetBytes(useContext + '\0');
        try
        {
            ThrowIfFailed(
                NCryptSetProperty(key, "Use Context", context, context.Length, 0),
                "NCryptSetProperty(Use Context)");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(context);
        }
    }

    private static void ApplyWindowHandle(SafeNCryptKeyHandle key, IntPtr ownerWindow)
    {
        if (ownerWindow == IntPtr.Zero)
        {
            return;
        }

        byte[] handle = IntPtr.Size == sizeof(long)
            ? BitConverter.GetBytes(ownerWindow.ToInt64())
            : BitConverter.GetBytes(ownerWindow.ToInt32());
        int status = NCryptSetProperty(key, "HWND Handle", handle, handle.Length, 0);
        if (status != NteBadData)
        {
            ThrowIfFailed(status, "NCryptSetProperty(HWND Handle)");
        }
    }

    private static int GetIntProperty(SafeNCryptKeyHandle key, string name)
    {
        byte[] value = new byte[sizeof(int)];
        ThrowIfFailed(
            NCryptGetProperty(key, name, value, value.Length, out int written, 0),
            $"NCryptGetProperty({name})");
        if (written != sizeof(int))
        {
            throw new CryptographicException($"CNG property {name} has an invalid size.");
        }
        return BitConverter.ToInt32(value, 0);
    }

    private static void SetIntProperty(
        SafeNCryptKeyHandle key,
        string name,
        int value) =>
        ThrowIfFailed(
            SetIntPropertyUnchecked(key, name, value),
            $"NCryptSetProperty({name})");

    private static int SetIntPropertyUnchecked(
        SafeNCryptKeyHandle key,
        string name,
        int value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        return NCryptSetProperty(key, name, bytes, bytes.Length, 0);
    }

    private static byte[] Transform(
        SafeNCryptKeyHandle key,
        byte[] input,
        bool decrypt,
        int flags,
        IntPtr paddingInfo) =>
        decrypt
            ? DecryptSinglePass(key, input, flags, paddingInfo)
            : EncryptTwoPass(key, input, flags, paddingInfo);

    private static byte[] DecryptSinglePass(
        SafeNCryptKeyHandle key,
        byte[] input,
        int flags,
        IntPtr paddingInfo)
    {
        // Microsoft Passport KSP must receive a real output buffer on the
        // authenticated decrypt call. A preliminary NCryptDecrypt call with a
        // null output buffer can wait for credential UI without completing on
        // affected Windows builds. RSA plaintext cannot exceed ciphertext size,
        // so one ciphertext-sized buffer is sufficient for PKCS#1 and OAEP.
        byte[] output = GC.AllocateUninitializedArray<byte>(input.Length);
        try
        {
            int status = NCryptDecrypt(
                key,
                input,
                input.Length,
                paddingInfo,
                output,
                output.Length,
                out int written,
                flags);
            ThrowIfFailed(status, "NCryptDecrypt");
            if (written <= 0 || written > output.Length)
            {
                throw new CryptographicException(
                    "NCryptDecrypt returned an invalid plaintext length.");
            }

            if (written == output.Length)
            {
                return output;
            }

            byte[] exact = output.AsSpan(0, written).ToArray();
            CryptographicOperations.ZeroMemory(output);
            return exact;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(output);
            throw;
        }
    }

    private static byte[] EncryptTwoPass(
        SafeNCryptKeyHandle key,
        byte[] input,
        int flags,
        IntPtr paddingInfo)
    {
        int status = NCryptEncrypt(
            key,
            input,
            input.Length,
            paddingInfo,
            null,
            0,
            out int required,
            flags);
        ThrowIfFailed(status, "NCryptEncrypt(size)");
        if (required <= 0)
        {
            throw new CryptographicException(
                "NCryptEncrypt returned an invalid ciphertext length.");
        }

        byte[] output = GC.AllocateUninitializedArray<byte>(required);
        try
        {
            status = NCryptEncrypt(
                key,
                input,
                input.Length,
                paddingInfo,
                output,
                output.Length,
                out int written,
                flags);
            ThrowIfFailed(status, "NCryptEncrypt");
            if (written <= 0 || written > output.Length)
            {
                throw new CryptographicException(
                    "NCryptEncrypt returned an invalid ciphertext length.");
            }

            if (written == output.Length)
            {
                return output;
            }

            byte[] exact = output.AsSpan(0, written).ToArray();
            CryptographicOperations.ZeroMemory(output);
            return exact;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(output);
            throw;
        }
    }

    private static void ThrowIfFailed(int status, string operation)
    {
        if (status >= 0)
        {
            return;
        }

        throw new CryptographicException(
            $"{operation} failed with CNG status 0x{status:X8}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BCryptOaepPaddingInfo
    {
        public IntPtr AlgorithmId;
        public IntPtr Label;
        public int LabelLength;
    }

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptOpenStorageProvider(
        out SafeNCryptProviderHandle provider,
        string providerName,
        int flags);

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptOpenKey(
        SafeNCryptProviderHandle provider,
        out SafeNCryptKeyHandle key,
        string keyName,
        int legacyKeySpec,
        int flags);

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptCreatePersistedKey(
        SafeNCryptProviderHandle provider,
        out SafeNCryptKeyHandle key,
        string algorithmId,
        string keyName,
        int legacyKeySpec,
        int flags);

    [DllImport("ncrypt.dll")]
    private static extern int NCryptFinalizeKey(SafeNCryptKeyHandle key, int flags);

    [DllImport("ncrypt.dll")]
    private static extern int NCryptDeleteKey(SafeNCryptKeyHandle key, int flags);

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptGetProperty(
        SafeNCryptKeyHandle key,
        string property,
        [Out] byte[] output,
        int outputLength,
        out int resultLength,
        int flags);

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptSetProperty(
        SafeNCryptKeyHandle key,
        string property,
        [In] byte[] input,
        int inputLength,
        int flags);

    [DllImport("ncrypt.dll")]
    private static extern int NCryptEncrypt(
        SafeNCryptKeyHandle key,
        [In] byte[] input,
        int inputLength,
        IntPtr paddingInfo,
        [Out] byte[]? output,
        int outputLength,
        out int resultLength,
        int flags);

    [DllImport("ncrypt.dll")]
    private static extern int NCryptDecrypt(
        SafeNCryptKeyHandle key,
        [In] byte[] input,
        int inputLength,
        IntPtr paddingInfo,
        [Out] byte[]? output,
        int outputLength,
        out int resultLength,
        int flags);
}
