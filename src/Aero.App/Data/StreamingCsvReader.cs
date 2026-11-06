using System.Buffers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Aero.App.Data;

/// <summary>
/// Streams CSV records into disposable, zeroable character buffers. Password fields
/// never become immutable managed strings and the complete file is never retained.
/// </summary>
internal static class StreamingCsvReader
{
    private const int SampleCharacterLimit = 16 * 1024;
    private const int ReadBufferLength = 16 * 1024;
    private const int MaximumColumns = 256;
    private const int MaximumFieldCharacters = 1024 * 1024;

    public static async Task<char> DetectDelimiterAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: ReadBufferLength,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        using StreamReader reader = new(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: ReadBufferLength,
            leaveOpen: false);

        char[] sampleBuffer = ArrayPool<char>.Shared.Rent(SampleCharacterLimit);
        int read = 0;
        try
        {
            read = await reader.ReadAsync(
                sampleBuffer.AsMemory(0, SampleCharacterLimit),
                cancellationToken);
            return DetectDelimiter(sampleBuffer.AsSpan(0, read));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                    sampleBuffer.AsSpan(0, Math.Max(read, 0))));
            ArrayPool<char>.Shared.Return(sampleBuffer, clearArray: true);
        }
    }

    public static async IAsyncEnumerable<SecureCsvRow> ReadRowsAsync(
        string path,
        char delimiter,
        int maximumRows,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRows);

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: ReadBufferLength,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        using StreamReader reader = new(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: ReadBufferLength,
            leaveOpen: false);
        using PooledCharBuilder field = new();

        char[] readBuffer = ArrayPool<char>.Shared.Rent(ReadBufferLength);
        List<char[]> fields = [];
        Queue<SecureCsvRow> readyRows = new();
        bool inQuotedField = false;
        bool afterClosingQuote = false;
        bool fieldAtStart = true;
        bool rowHasContent = false;
        bool skipLineFeed = false;
        int emittedRows = 0;
        int bufferedCharacters = 0;

        try
        {
            while (emittedRows < maximumRows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = await reader.ReadAsync(
                    readBuffer.AsMemory(0, ReadBufferLength),
                    cancellationToken);
                if (read == 0)
                {
                    break;
                }

                bufferedCharacters = read;
                for (int index = 0; index < read && emittedRows < maximumRows; index++)
                {
                    char value = readBuffer[index];
                    if (skipLineFeed)
                    {
                        skipLineFeed = false;
                        if (value == '\n')
                        {
                            continue;
                        }
                    }

                    if (inQuotedField)
                    {
                        if (value == '"')
                        {
                            inQuotedField = false;
                            afterClosingQuote = true;
                        }
                        else
                        {
                            field.Append(value);
                            rowHasContent = true;
                        }
                        continue;
                    }

                    if (afterClosingQuote)
                    {
                        if (value == '"')
                        {
                            field.Append('"');
                            rowHasContent = true;
                            inQuotedField = true;
                            afterClosingQuote = false;
                            continue;
                        }

                        if (value == delimiter)
                        {
                            CommitField(fields, field);
                            fieldAtStart = true;
                            afterClosingQuote = false;
                            rowHasContent = true;
                            continue;
                        }

                        if (value is '\r' or '\n')
                        {
                            CommitField(fields, field);
                            afterClosingQuote = false;
                            fieldAtStart = true;
                            if (value == '\r')
                            {
                                skipLineFeed = true;
                            }

                            emittedRows++;
                            readyRows.Enqueue(DetachRow(fields));
                            rowHasContent = false;
                            continue;
                        }

                        if (value is ' ' or '\t')
                        {
                            continue;
                        }

                        throw new InvalidDataException(
                            "The password CSV contains characters after a closing quote.");
                    }

                    if (fieldAtStart && value == '"')
                    {
                        inQuotedField = true;
                        fieldAtStart = false;
                        rowHasContent = true;
                        continue;
                    }

                    if (value == delimiter)
                    {
                        CommitField(fields, field);
                        fieldAtStart = true;
                        rowHasContent = true;
                        continue;
                    }

                    if (value is '\r' or '\n')
                    {
                        CommitField(fields, field);
                        fieldAtStart = true;
                        if (value == '\r')
                        {
                            skipLineFeed = true;
                        }

                        emittedRows++;
                        readyRows.Enqueue(DetachRow(fields));
                        rowHasContent = false;
                        continue;
                    }

                    field.Append(value);
                    fieldAtStart = false;
                    rowHasContent = true;
                }

                CryptographicOperations.ZeroMemory(
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                        readBuffer.AsSpan(0, read)));
                bufferedCharacters = 0;

                // Never suspend the iterator while the raw read buffer still holds
                // future CSV rows. Only zeroable detached row buffers survive a yield.
                while (readyRows.Count > 0)
                {
                    yield return readyRows.Dequeue();
                }
            }

            if (inQuotedField)
            {
                throw new InvalidDataException(
                    "The password CSV ends inside a quoted field.");
            }

            if (emittedRows < maximumRows &&
                (rowHasContent || field.Length > 0 || fields.Count > 0 || afterClosingQuote))
            {
                CommitField(fields, field);
                readyRows.Enqueue(DetachRow(fields));
            }

            while (readyRows.Count > 0)
            {
                yield return readyRows.Dequeue();
            }
        }
        finally
        {
            if (bufferedCharacters > 0)
            {
                CryptographicOperations.ZeroMemory(
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                        readBuffer.AsSpan(0, bufferedCharacters)));
            }

            foreach (char[] value in fields)
            {
                value.AsSpan().Clear();
            }
            fields.Clear();
            while (readyRows.TryDequeue(out SecureCsvRow? row))
            {
                row?.Dispose();
            }
            ArrayPool<char>.Shared.Return(readBuffer, clearArray: true);
        }
    }

    private static void CommitField(List<char[]> fields, PooledCharBuilder field)
    {
        if (fields.Count >= MaximumColumns)
        {
            throw new InvalidDataException("The password CSV contains too many columns.");
        }

        fields.Add(field.Detach());
    }

    private static SecureCsvRow DetachRow(List<char[]> fields)
    {
        char[][] row = fields.ToArray();
        fields.Clear();
        return new SecureCsvRow(row);
    }

    private static char DetectDelimiter(ReadOnlySpan<char> sample)
    {
        Span<int> counts = stackalloc int[3];
        ReadOnlySpan<char> candidates = ",;\t";
        bool quoted = false;

        for (int index = 0; index < sample.Length; index++)
        {
            char value = sample[index];
            if (value == '"')
            {
                if (quoted && index + 1 < sample.Length && sample[index + 1] == '"')
                {
                    index++;
                    continue;
                }

                quoted = !quoted;
                continue;
            }

            if (quoted)
            {
                continue;
            }

            for (int candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++)
            {
                if (value == candidates[candidateIndex])
                {
                    counts[candidateIndex]++;
                }
            }

            if (value is '\r' or '\n')
            {
                break;
            }
        }

        int bestIndex = 0;
        for (int index = 1; index < counts.Length; index++)
        {
            if (counts[index] > counts[bestIndex])
            {
                bestIndex = index;
            }
        }

        return candidates[bestIndex];
    }

    internal sealed class SecureCsvRow : IDisposable
    {
        private char[][]? _fields;

        internal SecureCsvRow(char[][] fields)
        {
            _fields = fields;
        }

        public int Count => (_fields
            ?? throw new ObjectDisposedException(nameof(SecureCsvRow))).Length;

        public ReadOnlySpan<char> GetField(int index)
        {
            char[][] fields = _fields
                ?? throw new ObjectDisposedException(nameof(SecureCsvRow));
            return index >= 0 && index < fields.Length
                ? fields[index]
                : ReadOnlySpan<char>.Empty;
        }

        public void Dispose()
        {
            char[][]? fields = Interlocked.Exchange(ref _fields, null);
            if (fields is null)
            {
                return;
            }

            foreach (char[] field in fields)
            {
                field.AsSpan().Clear();
            }
            Array.Clear(fields, 0, fields.Length);
        }
    }

    private sealed class PooledCharBuilder : IDisposable
    {
        private char[] _buffer = ArrayPool<char>.Shared.Rent(128);
        private int _length;

        public int Length => _length;

        public void Append(char value)
        {
            if (_length >= MaximumFieldCharacters)
            {
                throw new InvalidDataException(
                    "A password CSV field exceeds Aero's safety limit.");
            }

            if (_length == _buffer.Length)
            {
                Grow();
            }
            _buffer[_length++] = value;
        }

        public char[] Detach()
        {
            char[] value = GC.AllocateUninitializedArray<char>(_length);
            _buffer.AsSpan(0, _length).CopyTo(value);
            _buffer.AsSpan(0, _length).Clear();
            _length = 0;
            return value;
        }

        private void Grow()
        {
            int nextLength = Math.Min(
                MaximumFieldCharacters,
                checked(_buffer.Length * 2));
            char[] replacement = ArrayPool<char>.Shared.Rent(nextLength);
            _buffer.AsSpan(0, _length).CopyTo(replacement);
            _buffer.AsSpan(0, _length).Clear();
            ArrayPool<char>.Shared.Return(_buffer, clearArray: true);
            _buffer = replacement;
        }

        public void Dispose()
        {
            char[] buffer = Interlocked.Exchange(ref _buffer, Array.Empty<char>());
            if (buffer.Length == 0)
            {
                return;
            }

            buffer.AsSpan().Clear();
            ArrayPool<char>.Shared.Return(buffer, clearArray: true);
            _length = 0;
        }
    }
}
