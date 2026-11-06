using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Aero.Security.BrokerHost;

internal static class BrokerPasswordGenerator
{
    public const int DefaultLength = 24;

    private static ReadOnlySpan<byte> Uppercase => "ABCDEFGHJKLMNPQRSTUVWXYZ"u8;
    private static ReadOnlySpan<byte> Lowercase => "abcdefghijkmnopqrstuvwxyz"u8;
    private static ReadOnlySpan<byte> Digits => "23456789"u8;
    private static ReadOnlySpan<byte> Symbols => "!#$%&*+-=?@_"u8;
    private static ReadOnlySpan<byte> Alphabet =>
        "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!#$%&*+-=?@_"u8;

    public static byte[] GenerateUtf8(int length = DefaultLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 16);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, 128);

        byte[] password = new byte[length];
        password[0] = Choose(Uppercase);
        password[1] = Choose(Lowercase);
        password[2] = Choose(Digits);
        password[3] = Choose(Symbols);

        for (int index = 4; index < password.Length; index++)
        {
            password[index] = Choose(Alphabet);
        }

        for (int index = password.Length - 1; index > 0; index--)
        {
            int swapIndex = NextIndex(index + 1);
            (password[index], password[swapIndex]) = (password[swapIndex], password[index]);
        }

        return password;
    }

    private static byte Choose(ReadOnlySpan<byte> alphabet) =>
        alphabet[NextIndex(alphabet.Length)];

    private static int NextIndex(int exclusiveMaximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exclusiveMaximum);

        ulong range = (ulong)uint.MaxValue + 1UL;
        ulong limit = range - range % (uint)exclusiveMaximum;
        Span<byte> randomBytes = stackalloc byte[sizeof(uint)];
        uint value;
        do
        {
            RandomNumberGenerator.Fill(randomBytes);
            value = BinaryPrimitives.ReadUInt32LittleEndian(randomBytes);
        }
        while ((ulong)value >= limit);

        CryptographicOperations.ZeroMemory(randomBytes);
        return (int)(value % (uint)exclusiveMaximum);
    }
}
