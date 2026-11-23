using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Aero.App.Data;

/// <summary>
/// Reads the tabs that are open right now in another browser: the current page of each tab in
/// each open window. Back/forward history, closed tabs and closed windows are not tabs.
/// </summary>
public static class BrowserSessionReader
{
    // Chromium's SNSS command ids (components/sessions/core/session_service_commands.cc).
    private const byte SetTabWindow = 0;
    private const byte UpdateTabNavigation = 6;
    private const byte SetSelectedNavigationIndex = 7;
    private const byte TabClosed = 16;
    private const byte WindowClosed = 17;
    private const int MaximumFirefoxSessionBytes = 64 * 1024 * 1024;

    /// <summary>Replays a Chromium "Session_*" (SNSS) file. Encrypted or unknown versions yield nothing.</summary>
    public static List<string> ReadChromiumSession(ReadOnlySpan<byte> file)
    {
        List<string> result = [];
        if (file.Length < 8 || !file[..4].SequenceEqual("SNSS"u8))
        {
            return result;
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(file[4..]);
        if (version is not (1 or 3))
        {
            return result; // 2 and 4 are encrypted with a key we do not have
        }

        Dictionary<int, SortedDictionary<int, string>> navigations = [];
        Dictionary<int, int> selectedIndex = [];
        Dictionary<int, int> tabWindow = [];
        HashSet<int> closedTabs = [];
        HashSet<int> closedWindows = [];
        List<int> tabOrder = [];

        int offset = 8;
        while (offset + 2 <= file.Length)
        {
            int size = BinaryPrimitives.ReadUInt16LittleEndian(file[offset..]);
            offset += 2;
            if (size == 0 || offset + size > file.Length)
            {
                break; // a torn write at the end of the file
            }

            byte id = file[offset];
            ReadOnlySpan<byte> payload = file.Slice(offset + 1, size - 1);
            offset += size;
            switch (id)
            {
                case SetTabWindow when payload.Length >= 8:
                    tabWindow[ReadInt(payload, 4)] = ReadInt(payload, 0);
                    break;
                case UpdateTabNavigation when TryReadNavigation(payload, out int tab, out int index, out string url):
                    if (!navigations.TryGetValue(tab, out SortedDictionary<int, string>? entries))
                    {
                        navigations[tab] = entries = [];
                        tabOrder.Add(tab);
                    }
                    entries[index] = url;
                    closedTabs.Remove(tab); // a restored tab reuses its id
                    break;
                case SetSelectedNavigationIndex when payload.Length >= 8:
                    selectedIndex[ReadInt(payload, 0)] = ReadInt(payload, 4);
                    break;
                case TabClosed when payload.Length >= 4:
                    closedTabs.Add(ReadInt(payload, 0));
                    break;
                case WindowClosed when payload.Length >= 4:
                    closedWindows.Add(ReadInt(payload, 0));
                    break;
            }
        }

        foreach (int tab in tabOrder)
        {
            if (closedTabs.Contains(tab) ||
                (tabWindow.TryGetValue(tab, out int window) && closedWindows.Contains(window)))
            {
                continue;
            }

            SortedDictionary<int, string> entries = navigations[tab];
            string current = selectedIndex.TryGetValue(tab, out int selected) && entries.TryGetValue(selected, out string? chosen)
                ? chosen
                : entries.Values.Last();
            result.Add(current);
        }

        return result;
    }

    /// <summary>Reads a Firefox sessionstore (.jsonlz4) file: the current entry of each tab in each open window.</summary>
    public static List<string> ReadFirefoxSession(ReadOnlySpan<byte> file)
    {
        List<string> result = [];
        if (file.Length < 12 || !file[..8].SequenceEqual("mozLz40\0"u8))
        {
            return result;
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(file[8..]);
        if (length <= 0 || length > MaximumFirefoxSessionBytes)
        {
            return result;
        }

        using JsonDocument document = JsonDocument.Parse(DecodeLz4Block(file[12..], length));
        if (!document.RootElement.TryGetProperty("windows", out JsonElement windows) ||
            windows.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        // "_closedWindows" and each window's "_closedTabs" are history, so they are skipped.
        foreach (JsonElement window in windows.EnumerateArray())
        {
            if (!window.TryGetProperty("tabs", out JsonElement tabs) || tabs.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement tab in tabs.EnumerateArray())
            {
                if (!tab.TryGetProperty("entries", out JsonElement entries) ||
                    entries.ValueKind != JsonValueKind.Array ||
                    entries.GetArrayLength() == 0)
                {
                    continue;
                }

                int count = entries.GetArrayLength();
                int index = tab.TryGetProperty("index", out JsonElement indexElement) &&
                    indexElement.TryGetInt32(out int oneBased)
                        ? Math.Clamp(oneBased - 1, 0, count - 1)
                        : count - 1;
                if (entries[index].TryGetProperty("url", out JsonElement url) &&
                    url.ValueKind == JsonValueKind.String)
                {
                    result.Add(url.GetString()!);
                }
            }
        }

        return result;
    }

    private static int ReadInt(ReadOnlySpan<byte> payload, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]);

    // A base::Pickle: uint32 payload size, int32 tab id, int32 index, then the URL as
    // int32 length + bytes. Everything after the URL is not needed.
    private static bool TryReadNavigation(ReadOnlySpan<byte> payload, out int tab, out int index, out string url)
    {
        tab = index = 0;
        url = string.Empty;
        if (payload.Length < 16)
        {
            return false;
        }

        tab = ReadInt(payload, 4);
        index = ReadInt(payload, 8);
        int length = ReadInt(payload, 12);
        if (length <= 0 || length > payload.Length - 16)
        {
            return false;
        }

        url = Encoding.UTF8.GetString(payload.Slice(16, length));
        return true;
    }

    private static byte[] DecodeLz4Block(ReadOnlySpan<byte> source, int length)
    {
        byte[] output = new byte[length];
        int input = 0;
        int written = 0;
        while (input < source.Length)
        {
            int token = source[input++];
            int literals = ReadLz4Length(source, ref input, token >> 4);
            if (literals > source.Length - input || literals > length - written)
            {
                throw new InvalidDataException("The Firefox session file is damaged.");
            }

            source.Slice(input, literals).CopyTo(output.AsSpan(written));
            input += literals;
            written += literals;
            if (input >= source.Length)
            {
                break; // the last sequence has literals only
            }

            if (input + 2 > source.Length)
            {
                throw new InvalidDataException("The Firefox session file is damaged.");
            }

            int distance = source[input] | source[input + 1] << 8;
            input += 2;
            int match = ReadLz4Length(source, ref input, token & 15) + 4;
            if (distance == 0 || distance > written || match > length - written)
            {
                throw new InvalidDataException("The Firefox session file is damaged.");
            }

            // Byte by byte: a match may overlap the bytes it is producing.
            for (int i = 0; i < match; i++, written++)
            {
                output[written] = output[written - distance];
            }
        }

        return written == length ? output : output[..written];
    }

    private static int ReadLz4Length(ReadOnlySpan<byte> source, ref int input, int length)
    {
        if (length != 15)
        {
            return length;
        }

        byte next;
        do
        {
            if (input >= source.Length)
            {
                throw new InvalidDataException("The Firefox session file is damaged.");
            }

            next = source[input++];
            length += next;
        }
        while (next == 255 && length < MaximumFirefoxSessionBytes);
        return length;
    }
}
