using System.Security.Cryptography;

namespace Aero.App.Security;

public enum PasswordBrokerOperation : byte
{
    Fill = 1,
    Reveal = 2,
    GenerateAndSave = 3
}

public enum PasswordBrokerFailure
{
    None = 0,
    NotFound,
    VerificationCancelled,
    ClientRejected,
    OriginRejected,
    WindowRejected,
    Timeout,
    ProtocolError,
    BrokerUnavailable,
    SecurityDriverUnavailable,
    VaultError,
    UserModeProtectionUnavailable
}

public sealed class PasswordBrokerException : Exception
{
    public PasswordBrokerException(PasswordBrokerFailure failure, string message)
        : base(message)
    {
        Failure = failure;
    }

    public PasswordBrokerException(
        PasswordBrokerFailure failure,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public PasswordBrokerFailure Failure { get; }
}

public sealed class BrokerCredentialLease : IDisposable
{
    private SecureBuffer? _usernameUtf8;
    private SecureBuffer? _passwordUtf8;

    internal BrokerCredentialLease(ReadOnlySpan<byte> usernameUtf8, ReadOnlySpan<byte> passwordUtf8)
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
    }

    public ReadOnlySpan<byte> UsernameUtf8 =>
        (_usernameUtf8 ?? throw new ObjectDisposedException(nameof(BrokerCredentialLease))).ReadOnlySpan;

    public ReadOnlySpan<byte> PasswordUtf8 =>
        (_passwordUtf8 ?? throw new ObjectDisposedException(nameof(BrokerCredentialLease))).ReadOnlySpan;

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
