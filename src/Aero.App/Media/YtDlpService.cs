using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Aero.App.Media;

internal enum MediaDownloadKind
{
    Video,
    Audio
}

internal sealed record MediaDownloadOption(
    string Id,
    MediaDownloadKind Kind,
    string Title,
    string Description,
    string FormatSelector,
    string OutputExtension,
    string? MergeOutputFormat,
    string? AudioFormat,
    string? AudioQuality,
    bool RequiresFfmpeg,
    long? EstimatedSizeBytes,
    bool IsRecommended = false);

internal sealed record MediaDownloadCatalog(
    string MediaTitle,
    IReadOnlyList<MediaDownloadOption> Options);

internal sealed record MediaDownloadResult(bool Success, string? FilePath, string Message);

internal static class YtDlpService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
    private const string LatestFfmpegReleaseApi = "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest";
    private static readonly HttpClient Client = CreateClient();

    public static async Task<MediaDownloadCatalog> GetDownloadOptionsAsync(
        Uri pageUri,
        MediaDownloadKind kind,
        string? cookiesFile,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        string executablePath;
        try
        {
            executablePath = await EnsureInstalledAsync(progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Could not install yt-dlp: {exception.Message}", exception);
        }

        progress?.Report("Reading available media formats…");

        ProcessStartInfo startInfo = CreateBaseStartInfo(executablePath);
        startInfo.ArgumentList.Add("--dump-single-json");
        startInfo.ArgumentList.Add("--skip-download");
        startInfo.ArgumentList.Add("--no-warnings");
        startInfo.ArgumentList.Add("--no-playlist");
        AddCookies(startInfo, cookiesFile);
        startInfo.ArgumentList.Add(pageUri.AbsoluteUri);

        ProcessCaptureResult capture = await RunCaptureAsync(startInfo, cancellationToken);
        if (capture.ExitCode != 0)
        {
            string message = GetProcessFailureMessage(capture, "yt-dlp could not read the available formats.");
            throw new InvalidOperationException(message);
        }

        string json = capture.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("yt-dlp returned no format information for this page.");
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string title = ReadString(root, "title");
        if (string.IsNullOrWhiteSpace(title))
        {
            title = pageUri.Host;
        }

        List<FormatInfo> formats = ReadFormats(root);
        List<MediaDownloadOption> options = kind == MediaDownloadKind.Video
            ? BuildVideoOptions(formats)
            : BuildAudioOptions(formats);

        if (options.Count == 0)
        {
            throw new InvalidOperationException(
                kind == MediaDownloadKind.Video
                    ? "No downloadable video formats were reported for this page."
                    : "No downloadable audio formats were reported for this page.");
        }

        return new MediaDownloadCatalog(title, options);
    }

    public static async Task<MediaDownloadResult> DownloadAsync(
        Uri pageUri,
        string destinationFolder,
        MediaDownloadOption option,
        string? cookiesFile,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        string executablePath;
        try
        {
            executablePath = await EnsureInstalledAsync(progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new MediaDownloadResult(false, null, "Download cancelled.");
        }
        catch (Exception exception)
        {
            return new MediaDownloadResult(false, null, $"Could not install yt-dlp: {exception.Message}");
        }

        string? ffmpegFolder = null;
        if (option.RequiresFfmpeg)
        {
            try
            {
                ffmpegFolder = await EnsureFfmpegInstalledAsync(progress, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new MediaDownloadResult(false, null, "Download cancelled.");
            }
            catch (Exception exception)
            {
                return new MediaDownloadResult(false, null, $"Could not install FFmpeg: {exception.Message}");
            }
        }

        Directory.CreateDirectory(destinationFolder);
        DateTimeOffset downloadStartedAt = DateTimeOffset.UtcNow;
        HashSet<string> existingFiles = SnapshotDestinationFiles(destinationFolder);

        ProcessStartInfo startInfo = CreateBaseStartInfo(executablePath);
        startInfo.WorkingDirectory = destinationFolder;
        startInfo.ArgumentList.Add("--no-simulate");
        startInfo.ArgumentList.Add("--newline");
        startInfo.ArgumentList.Add("--no-playlist");
        startInfo.ArgumentList.Add("--progress");
        startInfo.ArgumentList.Add("--concurrent-fragments");
        startInfo.ArgumentList.Add("4");
        startInfo.ArgumentList.Add("--windows-filenames");
        startInfo.ArgumentList.Add("--trim-filenames");
        startInfo.ArgumentList.Add("180");
        startInfo.ArgumentList.Add("--no-overwrites");
        startInfo.ArgumentList.Add("--print");
        startInfo.ArgumentList.Add("after_move:__AERO_FILE__%(filepath)s");
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(Path.Combine(destinationFolder, "%(title).180B [%(id)s].%(ext)s"));
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add(option.FormatSelector);

        if (!string.IsNullOrWhiteSpace(ffmpegFolder))
        {
            startInfo.ArgumentList.Add("--ffmpeg-location");
            startInfo.ArgumentList.Add(ffmpegFolder);
        }

        if (!string.IsNullOrWhiteSpace(option.AudioFormat))
        {
            startInfo.ArgumentList.Add("--extract-audio");
            startInfo.ArgumentList.Add("--audio-format");
            startInfo.ArgumentList.Add(option.AudioFormat);
            if (!string.IsNullOrWhiteSpace(option.AudioQuality))
            {
                startInfo.ArgumentList.Add("--audio-quality");
                startInfo.ArgumentList.Add(option.AudioQuality);
            }
        }
        else if (!string.IsNullOrWhiteSpace(option.MergeOutputFormat))
        {
            startInfo.ArgumentList.Add("--merge-output-format");
            startInfo.ArgumentList.Add(option.MergeOutputFormat);
        }

        AddCookies(startInfo, cookiesFile);
        startInfo.ArgumentList.Add(pageUri.AbsoluteUri);

        using Process process = new() { StartInfo = startInfo };
        List<string> errorTail = [];
        string? completedFile = null;

        try
        {
            if (!process.Start())
            {
                return new MediaDownloadResult(false, null, "yt-dlp could not be started.");
            }

            Task stdoutTask = PumpOutputAsync(
                process.StandardOutput,
                line =>
                {
                    if (line.StartsWith("__AERO_FILE__", StringComparison.Ordinal))
                    {
                        completedFile = line["__AERO_FILE__".Length..].Trim();
                        return;
                    }

                    if (line.StartsWith("[download]", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("[ExtractAudio]", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("[Merger]", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("[VideoRemuxer]", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("[Fixup", StringComparison.OrdinalIgnoreCase))
                    {
                        progress?.Report(CleanProgressLine(line));
                    }
                },
                cancellationToken);

            Task stderrTask = PumpOutputAsync(
                process.StandardError,
                line =>
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        return;
                    }

                    errorTail.Add(line.Trim());
                    if (errorTail.Count > 10)
                    {
                        errorTail.RemoveAt(0);
                    }
                },
                cancellationToken);

            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdoutTask, stderrTask);

            // yt-dlp can occasionally return a non-zero exit code after the final file has
            // already been moved into place (for example, after a harmless post-processing
            // warning). Treat an existing completed media file as success instead of showing
            // a false error before the downloads panel discovers the file.
            string? resolvedFile = ResolveCompletedDownloadFile(
                completedFile,
                destinationFolder,
                existingFiles,
                downloadStartedAt,
                option.OutputExtension);

            if (process.ExitCode == 0 || resolvedFile is not null)
            {
                string label = option.Kind == MediaDownloadKind.Video ? "Video" : "Audio";
                return new MediaDownloadResult(
                    true,
                    resolvedFile,
                    resolvedFile is null
                        ? $"{label} downloaded in {option.Title}."
                        : $"{label} downloaded: {Path.GetFileName(resolvedFile)}");
            }

            string details = errorTail.Count == 0
                ? $"yt-dlp exited with code {process.ExitCode}."
                : string.Join(Environment.NewLine, errorTail.TakeLast(4));
            return new MediaDownloadResult(false, null, details);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return new MediaDownloadResult(false, null, "Download cancelled.");
        }
        catch (Exception exception)
        {
            TryKill(process);
            return new MediaDownloadResult(false, null, exception.Message);
        }
    }

    private static List<MediaDownloadOption> BuildVideoOptions(IReadOnlyList<FormatInfo> formats)
    {
        List<MediaDownloadOption> options = [];
        List<FormatInfo> videos = formats.Where(item => item.HasVideo).ToList();
        List<FormatInfo> audioOnly = formats.Where(item => item.HasAudio && !item.HasVideo).ToList();
        FormatInfo? bestAudio = SelectBestAudio(audioOnly.Count > 0 ? audioOnly : formats.Where(item => item.HasAudio));
        FormatInfo? bestVideo = SelectBestVideo(videos);

        if (bestVideo is not null)
        {
            bool needsAudioMerge = !bestVideo.HasAudio && bestAudio is not null;
            string selector = needsAudioMerge
                ? $"{bestVideo.Id}+{bestAudio!.Id}"
                : bestVideo.Id;
            string outputExtension = needsAudioMerge
                ? ChooseMergeContainer(bestVideo, bestAudio!)
                : NormalizeExtension(bestVideo.Extension, "mp4");
            long? size = SumSizes(bestVideo.EstimatedSizeBytes, needsAudioMerge ? bestAudio!.EstimatedSizeBytes : null);

            options.Add(new MediaDownloadOption(
                "video-maximum",
                MediaDownloadKind.Video,
                "Maximum quality",
                BuildVideoDescription(bestVideo, needsAudioMerge ? bestAudio : null, outputExtension, size, "No resolution or FPS limit"),
                selector,
                outputExtension,
                needsAudioMerge ? outputExtension : null,
                null,
                null,
                needsAudioMerge,
                size,
                IsRecommended: true));
        }

        AddContainerVideoOptions(options, videos, audioOnly, "mp4", ["m4a", "mp4"]);
        AddContainerVideoOptions(options, videos, audioOnly, "webm", ["webm"]);

        if (options.Count == 0)
        {
            FormatInfo? progressive = SelectBestVideo(videos.Where(item => item.HasAudio));
            if (progressive is not null)
            {
                string extension = NormalizeExtension(progressive.Extension, "mp4");
                options.Add(new MediaDownloadOption(
                    $"video-{progressive.Id}",
                    MediaDownloadKind.Video,
                    $"Best available · {extension.ToUpperInvariant()}",
                    BuildVideoDescription(progressive, null, extension, progressive.EstimatedSizeBytes, null),
                    progressive.Id,
                    extension,
                    null,
                    null,
                    null,
                    false,
                    progressive.EstimatedSizeBytes,
                    IsRecommended: true));
            }
        }

        return options;
    }

    private static void AddContainerVideoOptions(
        List<MediaDownloadOption> options,
        IReadOnlyList<FormatInfo> videos,
        IReadOnlyList<FormatInfo> audioOnly,
        string container,
        IReadOnlyCollection<string> compatibleAudioExtensions)
    {
        List<FormatInfo> containerVideos = videos
            .Where(item => item.Extension.Equals(container, StringComparison.OrdinalIgnoreCase) && item.Height is > 0)
            .ToList();
        if (containerVideos.Count == 0)
        {
            return;
        }

        FormatInfo? compatibleAudio = SelectBestAudio(audioOnly.Where(item =>
            compatibleAudioExtensions.Contains(item.Extension, StringComparer.OrdinalIgnoreCase)));

        IEnumerable<IGrouping<(int Height, int Fps), FormatInfo>> groups = containerVideos
            .GroupBy(item => (Height: item.Height ?? 0, Fps: NormalizeFps(item.Fps)))
            .OrderByDescending(group => group.Key.Height)
            .ThenByDescending(group => group.Key.Fps)
            .Take(18);

        foreach (IGrouping<(int Height, int Fps), FormatInfo> group in groups)
        {
            FormatInfo? video = SelectBestVideo(group);
            if (video is null)
            {
                continue;
            }

            FormatInfo? audio = null;
            bool needsMerge = !video.HasAudio;
            if (needsMerge)
            {
                audio = compatibleAudio;
                if (audio is null)
                {
                    continue;
                }
            }

            string selector = needsMerge ? $"{video.Id}+{audio!.Id}" : video.Id;
            long? size = SumSizes(video.EstimatedSizeBytes, audio?.EstimatedSizeBytes);
            string fpsLabel = group.Key.Fps > 0 ? $" · {group.Key.Fps} FPS" : string.Empty;
            string title = $"{container.ToUpperInvariant()} · {group.Key.Height}p{fpsLabel}";

            options.Add(new MediaDownloadOption(
                $"video-{container}-{group.Key.Height}-{group.Key.Fps}-{video.Id}",
                MediaDownloadKind.Video,
                title,
                BuildVideoDescription(video, audio, container, size, null),
                selector,
                container,
                needsMerge ? container : null,
                null,
                null,
                needsMerge,
                size));
        }
    }

    private static List<MediaDownloadOption> BuildAudioOptions(IReadOnlyList<FormatInfo> formats)
    {
        List<MediaDownloadOption> options = [];
        List<FormatInfo> audioOnly = formats.Where(item => item.HasAudio && !item.HasVideo).ToList();
        IEnumerable<FormatInfo> source = audioOnly.Count > 0 ? audioOnly : formats.Where(item => item.HasAudio);
        FormatInfo? best = SelectBestAudio(source);
        if (best is null)
        {
            return options;
        }

        string bestExtension = NormalizeExtension(best.Extension, "m4a");
        options.Add(new MediaDownloadOption(
            "audio-original-best",
            MediaDownloadKind.Audio,
            $"Best original audio · {bestExtension.ToUpperInvariant()}",
            BuildAudioDescription(best, best.EstimatedSizeBytes, "No re-encoding"),
            best.Id,
            bestExtension,
            null,
            null,
            null,
            false,
            best.EstimatedSizeBytes,
            IsRecommended: true));

        AddOriginalAudioOption(options, source, "m4a", "M4A · best available");
        AddOriginalAudioOption(options, source, "webm", "Opus/WebM · best available");

        foreach ((string Bitrate, string Label) preset in new[]
        {
            ("320K", "MP3 · 320 kbps"),
            ("256K", "MP3 · 256 kbps"),
            ("192K", "MP3 · 192 kbps"),
            ("128K", "MP3 · 128 kbps")
        })
        {
            options.Add(new MediaDownloadOption(
                $"audio-mp3-{preset.Bitrate}",
                MediaDownloadKind.Audio,
                preset.Label,
                $"Converted from {FriendlyAudioCodec(best.AudioCodec)} · source {FormatBitrate(best.AudioBitrateKbps ?? best.TotalBitrateKbps)}",
                best.Id,
                "mp3",
                null,
                "mp3",
                preset.Bitrate,
                true,
                EstimateTranscodedAudioSize(best, preset.Bitrate)));
        }

        return options
            .GroupBy(option => option.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private static void AddOriginalAudioOption(
        List<MediaDownloadOption> options,
        IEnumerable<FormatInfo> source,
        string extension,
        string title)
    {
        FormatInfo? format = SelectBestAudio(source.Where(item =>
            item.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase)));
        if (format is null || options.Any(option => option.FormatSelector.Equals(format.Id, StringComparison.Ordinal)))
        {
            return;
        }

        options.Add(new MediaDownloadOption(
            $"audio-original-{extension}",
            MediaDownloadKind.Audio,
            title,
            BuildAudioDescription(format, format.EstimatedSizeBytes, "No re-encoding"),
            format.Id,
            extension,
            null,
            null,
            null,
            false,
            format.EstimatedSizeBytes));
    }

    private static List<FormatInfo> ReadFormats(JsonElement root)
    {
        List<FormatInfo> formats = [];
        double? mediaDuration = ReadDouble(root, "duration");
        if (!root.TryGetProperty("formats", out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return formats;
        }

        foreach (JsonElement element in array.EnumerateArray())
        {
            string id = ReadString(element, "format_id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            string videoCodec = ReadString(element, "vcodec");
            string audioCodec = ReadString(element, "acodec");
            bool hasVideo = !string.IsNullOrWhiteSpace(videoCodec) &&
                !videoCodec.Equals("none", StringComparison.OrdinalIgnoreCase);
            bool hasAudio = !string.IsNullOrWhiteSpace(audioCodec) &&
                !audioCodec.Equals("none", StringComparison.OrdinalIgnoreCase);
            if (!hasVideo && !hasAudio)
            {
                continue;
            }

            long? size = ReadLong(element, "filesize") ?? ReadLong(element, "filesize_approx");
            formats.Add(new FormatInfo(
                id,
                NormalizeExtension(ReadString(element, "ext"), "bin"),
                hasVideo,
                hasAudio,
                ReadInt(element, "width"),
                ReadInt(element, "height"),
                ReadDouble(element, "fps"),
                ReadDouble(element, "tbr"),
                ReadDouble(element, "vbr"),
                ReadDouble(element, "abr"),
                size,
                videoCodec,
                audioCodec,
                ReadString(element, "dynamic_range"),
                ReadDouble(element, "duration") ?? mediaDuration));
        }

        return formats;
    }

    private static FormatInfo? SelectBestVideo(IEnumerable<FormatInfo> formats) => formats
        .Where(item => item.HasVideo)
        .OrderByDescending(item => item.Height ?? 0)
        .ThenByDescending(item => item.Width ?? 0)
        .ThenByDescending(item => item.Fps ?? 0)
        .ThenByDescending(item => DynamicRangeRank(item.DynamicRange))
        .ThenByDescending(item => item.VideoBitrateKbps ?? item.TotalBitrateKbps ?? 0)
        .ThenByDescending(item => item.EstimatedSizeBytes ?? 0)
        .FirstOrDefault();

    private static FormatInfo? SelectBestAudio(IEnumerable<FormatInfo> formats) => formats
        .Where(item => item.HasAudio)
        .OrderByDescending(item => item.AudioBitrateKbps ?? item.TotalBitrateKbps ?? 0)
        .ThenByDescending(item => AudioCodecRank(item.AudioCodec))
        .ThenByDescending(item => item.EstimatedSizeBytes ?? 0)
        .FirstOrDefault();

    private static string BuildVideoDescription(
        FormatInfo video,
        FormatInfo? audio,
        string outputExtension,
        long? size,
        string? suffix)
    {
        List<string> parts = [];
        if (video.Width is > 0 && video.Height is > 0)
        {
            parts.Add($"{video.Width}×{video.Height}");
        }

        if (video.Fps is > 0)
        {
            parts.Add($"{Math.Round(video.Fps.Value):0} FPS");
        }

        if (!string.IsNullOrWhiteSpace(video.DynamicRange) &&
            !video.DynamicRange.Equals("SDR", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(video.DynamicRange.ToUpperInvariant());
        }

        parts.Add(FriendlyVideoCodec(video.VideoCodec));
        parts.Add(audio is null
            ? FriendlyAudioCodec(video.AudioCodec)
            : FriendlyAudioCodec(audio.AudioCodec));
        parts.Add(outputExtension.ToUpperInvariant());
        if (size is > 0)
        {
            parts.Add($"about {FormatBytes(size.Value)}");
        }

        if (!string.IsNullOrWhiteSpace(suffix))
        {
            parts.Add(suffix);
        }

        return string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string BuildAudioDescription(FormatInfo audio, long? size, string? suffix)
    {
        List<string> parts =
        [
            FriendlyAudioCodec(audio.AudioCodec),
            FormatBitrate(audio.AudioBitrateKbps ?? audio.TotalBitrateKbps)
        ];
        if (size is > 0)
        {
            parts.Add($"about {FormatBytes(size.Value)}");
        }

        if (!string.IsNullOrWhiteSpace(suffix))
        {
            parts.Add(suffix);
        }

        return string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static long? EstimateTranscodedAudioSize(FormatInfo source, string bitrate)
    {
        if (source.DurationSeconds is not double duration ||
            duration <= 0 ||
            !double.TryParse(bitrate.TrimEnd('K', 'k'), NumberStyles.Float, CultureInfo.InvariantCulture, out double kbps))
        {
            return null;
        }

        double bytes = duration * kbps * 1000d / 8d;
        return bytes >= long.MaxValue ? null : (long)bytes;
    }

    private static string ChooseMergeContainer(FormatInfo video, FormatInfo audio)
    {
        if (video.Extension.Equals("mp4", StringComparison.OrdinalIgnoreCase) &&
            (audio.Extension.Equals("m4a", StringComparison.OrdinalIgnoreCase) ||
             audio.Extension.Equals("mp4", StringComparison.OrdinalIgnoreCase)))
        {
            return "mp4";
        }

        if (video.Extension.Equals("webm", StringComparison.OrdinalIgnoreCase) &&
            audio.Extension.Equals("webm", StringComparison.OrdinalIgnoreCase))
        {
            return "webm";
        }

        return "mkv";
    }

    private static int NormalizeFps(double? fps) => fps is > 0
        ? Math.Max(1, (int)Math.Round(fps.Value))
        : 0;

    private static int DynamicRangeRank(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("SDR", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return value.Contains("HDR", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("HLG", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("DV", StringComparison.OrdinalIgnoreCase)
            ? 2
            : 1;
    }

    private static int AudioCodecRank(string codec)
    {
        if (codec.Contains("opus", StringComparison.OrdinalIgnoreCase)) return 4;
        if (codec.Contains("flac", StringComparison.OrdinalIgnoreCase)) return 4;
        if (codec.Contains("mp4a", StringComparison.OrdinalIgnoreCase) || codec.Contains("aac", StringComparison.OrdinalIgnoreCase)) return 3;
        if (codec.Contains("vorbis", StringComparison.OrdinalIgnoreCase)) return 2;
        return 1;
    }

    private static string FriendlyVideoCodec(string codec)
    {
        if (string.IsNullOrWhiteSpace(codec) || codec.Equals("none", StringComparison.OrdinalIgnoreCase)) return "video";
        if (codec.StartsWith("av01", StringComparison.OrdinalIgnoreCase)) return "AV1";
        if (codec.StartsWith("vp9", StringComparison.OrdinalIgnoreCase) || codec.StartsWith("vp09", StringComparison.OrdinalIgnoreCase)) return "VP9";
        if (codec.StartsWith("avc", StringComparison.OrdinalIgnoreCase) || codec.Contains("h264", StringComparison.OrdinalIgnoreCase)) return "H.264";
        if (codec.StartsWith("hev", StringComparison.OrdinalIgnoreCase) || codec.StartsWith("hvc", StringComparison.OrdinalIgnoreCase) || codec.Contains("h265", StringComparison.OrdinalIgnoreCase)) return "H.265";
        return codec.Split('.')[0].ToUpperInvariant();
    }

    private static string FriendlyAudioCodec(string codec)
    {
        if (string.IsNullOrWhiteSpace(codec) || codec.Equals("none", StringComparison.OrdinalIgnoreCase)) return "audio";
        if (codec.Contains("opus", StringComparison.OrdinalIgnoreCase)) return "Opus";
        if (codec.Contains("mp4a", StringComparison.OrdinalIgnoreCase) || codec.Contains("aac", StringComparison.OrdinalIgnoreCase)) return "AAC";
        if (codec.Contains("vorbis", StringComparison.OrdinalIgnoreCase)) return "Vorbis";
        if (codec.Contains("flac", StringComparison.OrdinalIgnoreCase)) return "FLAC";
        if (codec.Contains("mp3", StringComparison.OrdinalIgnoreCase)) return "MP3";
        return codec.Split('.')[0].ToUpperInvariant();
    }

    private static string FormatBitrate(double? kbps) => kbps is > 0
        ? $"{Math.Round(kbps.Value):0} kbps"
        : "bitrate varies";

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit <= 1
            ? $"{value:0} {units[unit]}"
            : $"{value:0.##} {units[unit]}";
    }

    private static long? SumSizes(long? first, long? second)
    {
        if (first is null && second is null)
        {
            return null;
        }

        try
        {
            return checked((first ?? 0) + (second ?? 0));
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static string NormalizeExtension(string extension, string fallback)
    {
        string value = extension.Trim().TrimStart('.').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static async Task<string> EnsureInstalledAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        string toolsFolder = GetToolsFolder();
        Directory.CreateDirectory(toolsFolder);

        string assetName = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "yt-dlp_arm64.exe"
            : "yt-dlp.exe";
        string executablePath = Path.Combine(toolsFolder, "yt-dlp.exe");

        if (File.Exists(executablePath) && new FileInfo(executablePath).Length > 1_000_000)
        {
            return executablePath;
        }

        progress?.Report("Installing official yt-dlp…");

        using JsonDocument release = await GetJsonAsync(LatestReleaseApi, cancellationToken);
        string tag = release.RootElement.GetProperty("tag_name").GetString()
            ?? throw new InvalidOperationException("The latest yt-dlp release has no tag.");

        string releaseBase = $"https://github.com/yt-dlp/yt-dlp/releases/download/{Uri.EscapeDataString(tag)}/";
        string checksumText = await Client.GetStringAsync(releaseBase + "SHA2-256SUMS", cancellationToken);
        string expectedHash = FindExpectedHash(checksumText, assetName)
            ?? throw new InvalidOperationException($"No SHA-256 checksum was published for {assetName}.");

        string temporaryPath = executablePath + ".download";
        try
        {
            await DownloadFileAsync(releaseBase + assetName, temporaryPath, cancellationToken);
            string actualHash = await ComputeSha256Async(temporaryPath, cancellationToken);
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The downloaded yt-dlp checksum does not match the official release.");
            }

            File.Move(temporaryPath, executablePath, overwrite: true);
            await File.WriteAllTextAsync(
                Path.Combine(toolsFolder, "yt-dlp-version.txt"),
                tag,
                cancellationToken);
            progress?.Report("yt-dlp is ready.");
            return executablePath;
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static async Task<string> EnsureFfmpegInstalledAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        string toolsFolder = GetToolsFolder();
        string ffmpegFolder = Path.Combine(toolsFolder, "ffmpeg");
        string executablePath = Path.Combine(ffmpegFolder, "ffmpeg.exe");
        if (File.Exists(executablePath) && new FileInfo(executablePath).Length > 500_000)
        {
            return ffmpegFolder;
        }

        progress?.Report("Installing verified FFmpeg for maximum quality…");
        Directory.CreateDirectory(toolsFolder);

        using JsonDocument release = await GetJsonAsync(LatestFfmpegReleaseApi, cancellationToken);
        ReleaseAsset archive = FindFfmpegArchive(release.RootElement)
            ?? throw new InvalidOperationException("No compatible Windows FFmpeg build was published.");
        ReleaseAsset checksums = FindReleaseAsset(release.RootElement, name =>
            name.Equals("checksums.sha256", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The FFmpeg release has no SHA-256 checksum file.");

        string checksumText = await Client.GetStringAsync(checksums.DownloadUrl, cancellationToken);
        string expectedHash = FindExpectedHash(checksumText, archive.Name)
            ?? throw new InvalidOperationException($"No SHA-256 checksum was published for {archive.Name}.");

        string archivePath = Path.Combine(toolsFolder, "ffmpeg.download.zip");
        string extractFolder = Path.Combine(toolsFolder, $"ffmpeg.extract.{Guid.NewGuid():N}");
        string installFolder = Path.Combine(toolsFolder, $"ffmpeg.install.{Guid.NewGuid():N}");

        try
        {
            await DownloadFileAsync(archive.DownloadUrl, archivePath, cancellationToken);
            string actualHash = await ComputeSha256Async(archivePath, cancellationToken);
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The downloaded FFmpeg checksum does not match the published release.");
            }

            Directory.CreateDirectory(extractFolder);
            ZipFile.ExtractToDirectory(archivePath, extractFolder, overwriteFiles: true);
            string? extractedExecutable = Directory
                .EnumerateFiles(extractFolder, "ffmpeg.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(extractedExecutable))
            {
                throw new InvalidDataException("The FFmpeg archive did not contain ffmpeg.exe.");
            }

            string sourceBinFolder = Path.GetDirectoryName(extractedExecutable)
                ?? throw new InvalidDataException("The FFmpeg binary folder is invalid.");
            CopyDirectory(sourceBinFolder, installFolder);

            TryDeleteDirectory(ffmpegFolder);
            Directory.Move(installFolder, ffmpegFolder);
            progress?.Report("FFmpeg is ready.");
            return ffmpegFolder;
        }
        finally
        {
            TryDelete(archivePath);
            TryDeleteDirectory(extractFolder);
            TryDeleteDirectory(installFolder);
        }
    }

    private static ReleaseAsset? FindFfmpegArchive(JsonElement releaseRoot)
    {
        string preferred = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "winarm64-lgpl-shared.zip"
            : "win64-lgpl-shared.zip";
        ReleaseAsset? asset = FindReleaseAsset(releaseRoot, name =>
            name.EndsWith(preferred, StringComparison.OrdinalIgnoreCase));

        return asset ?? FindReleaseAsset(releaseRoot, name =>
            name.EndsWith("win64-lgpl-shared.zip", StringComparison.OrdinalIgnoreCase));
    }

    private static ReleaseAsset? FindReleaseAsset(JsonElement releaseRoot, Func<string, bool> predicate)
    {
        if (!releaseRoot.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string name = ReadString(asset, "name");
            string downloadUrl = ReadString(asset, "browser_download_url");
            if (!string.IsNullOrWhiteSpace(name) &&
                !string.IsNullOrWhiteSpace(downloadUrl) &&
                predicate(name))
            {
                return new ReleaseAsset(name, downloadUrl);
            }
        }

        return null;
    }


    private static HashSet<string> SnapshotDestinationFiles(string destinationFolder)
    {
        try
        {
            return Directory.EnumerateFiles(destinationFolder)
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string? ResolveCompletedDownloadFile(
        string? reportedPath,
        string destinationFolder,
        HashSet<string> existingFiles,
        DateTimeOffset startedAt,
        string expectedExtension)
    {
        string? reported = NormalizeCompletedPath(reportedPath, destinationFolder);
        if (reported is not null && IsCompletedMediaFile(reported))
        {
            return reported;
        }

        try
        {
            string normalizedExtension = NormalizeExtension(expectedExtension, string.Empty);
            return Directory.EnumerateFiles(destinationFolder)
                .Select(Path.GetFullPath)
                .Where(IsCompletedMediaFile)
                .Select(path => new FileInfo(path))
                .Where(file =>
                    !existingFiles.Contains(file.FullName) ||
                    file.LastWriteTimeUtc >= startedAt.UtcDateTime.AddSeconds(-2))
                .OrderByDescending(file =>
                    normalizedExtension.Length > 0 &&
                    file.Extension.TrimStart('.').Equals(normalizedExtension, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeCompletedPath(string? path, string destinationFolder)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            string trimmed = path.Trim().Trim('"');
            return Path.IsPathRooted(trimmed)
                ? Path.GetFullPath(trimmed)
                : Path.GetFullPath(Path.Combine(destinationFolder, trimmed));
        }
        catch
        {
            return null;
        }
    }

    private static bool IsCompletedMediaFile(string path)
    {
        try
        {
            string extension = Path.GetExtension(path);
            if (extension.Equals(".part", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".ytdl", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".temp", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            FileInfo file = new(path);
            return file.Exists && file.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    // Signed-in YouTube cookies let yt-dlp pass "confirm you're not a bot" checks as the user's account.
    private static void AddCookies(ProcessStartInfo startInfo, string? cookiesFile)
    {
        if (!string.IsNullOrEmpty(cookiesFile))
        {
            startInfo.ArgumentList.Add("--cookies");
            startInfo.ArgumentList.Add(cookiesFile);
        }
    }

    private static ProcessStartInfo CreateBaseStartInfo(string executablePath) => new()
    {
        FileName = executablePath,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        ArgumentList =
        {
            "--ignore-config",
            "--no-color"
        }
    };

    private static async Task<ProcessCaptureResult> RunCaptureAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The process could not be started.");
            }

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new ProcessCaptureResult(
                process.ExitCode,
                await stdoutTask,
                await stderrTask);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static string GetProcessFailureMessage(ProcessCaptureResult capture, string fallback)
    {
        string stderr = capture.StandardError.Trim();
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return fallback;
        }

        string[] lines = stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? fallback : string.Join(Environment.NewLine, lines.TakeLast(4));
    }

    private static async Task<JsonDocument> GetJsonAsync(string address, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Client.GetAsync(address, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static async Task DownloadFileAsync(
        string address,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Client.GetAsync(
            address,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream destination = new(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static string? FindExpectedHash(string checksumText, string assetName)
    {
        foreach (string line in checksumText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string normalized = line.Replace(" *", "  ", StringComparison.Ordinal);
            string[] parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[^1].Equals(assetName, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0];
            }
        }

        return null;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLower(CultureInfo.InvariantCulture);
    }

    private static async Task PumpOutputAsync(
        StreamReader reader,
        Action<string> onLine,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return;
            }

            onLine(line);
        }
    }

    private static string CleanProgressLine(string line)
    {
        string cleaned = line.Trim();
        return cleaned.Length > 140 ? cleaned[..137] + "…" : cleaned;
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static int? ReadInt(JsonElement element, string name)
    {
        double? value = ReadDouble(element, name);
        if (value is null || value < int.MinValue || value > int.MaxValue)
        {
            return null;
        }

        return (int)Math.Round(value.Value);
    }

    private static long? ReadLong(JsonElement element, string name)
    {
        double? value = ReadDouble(element, name);
        if (value is null || value < 0 || value > long.MaxValue)
        {
            return null;
        }

        return (long)Math.Round(value.Value);
    }

    private static double? ReadDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.TryGetDouble(out double number) && double.IsFinite(number) ? number : null;
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (string file in Directory.EnumerateFiles(sourceDirectory))
        {
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            CopyDirectory(directory, Path.Combine(destinationDirectory, Path.GetFileName(directory)));
        }
    }

    private static string GetToolsFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Aero",
        "Tools");

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may already have exited.
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
            // A future launch can clean or overwrite the temporary file.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // A future launch can clean or overwrite the temporary folder.
        }
    }

    private static HttpClient CreateClient()
    {
        HttpClient client = new()
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Aero-Browser/0.16");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private sealed record FormatInfo(
        string Id,
        string Extension,
        bool HasVideo,
        bool HasAudio,
        int? Width,
        int? Height,
        double? Fps,
        double? TotalBitrateKbps,
        double? VideoBitrateKbps,
        double? AudioBitrateKbps,
        long? EstimatedSizeBytes,
        string VideoCodec,
        string AudioCodec,
        string DynamicRange,
        double? DurationSeconds);

    private sealed record ReleaseAsset(string Name, string DownloadUrl);

    private sealed record ProcessCaptureResult(int ExitCode, string StandardOutput, string StandardError);
}


//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
//shegol
