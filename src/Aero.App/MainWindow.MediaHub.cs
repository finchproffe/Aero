using Aero.App.Media;
using Aero.Downloads;
using Aero.Search;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using System.Diagnostics;
using System.Text.Json;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Aero.App;

public sealed partial class MainWindow
{
    private const string MediaBridgeScript = """
(() => {
    if (window.__aeroMediaHubInstalled) {
        window.__aeroMediaHubReport?.();
        return;
    }

    window.__aeroMediaHubInstalled = true;
    let lastPayload = "";
    let reportTimer = 0;

    const mediaElements = () => Array.from(document.querySelectorAll("video, audio"));

    const selectMedia = () => {
        const items = mediaElements();
        const playing = items.find(item => !item.paused && !item.ended && item.readyState > 0);
        if (playing) return playing;

        return items
            .filter(item => item.currentSrc || item.src || item.readyState > 0)
            .sort((left, right) => {
                const leftArea = (left.clientWidth || 0) * (left.clientHeight || 0);
                const rightArea = (right.clientWidth || 0) * (right.clientHeight || 0);
                return rightArea - leftArea;
            })[0] || null;
    };

    const report = () => {
        try {
            const media = selectMedia();
            const metadata = navigator.mediaSession?.metadata || null;
            const artwork = metadata?.artwork?.length
                ? metadata.artwork[metadata.artwork.length - 1].src
                : "";
            const hasMedia = !!media && !!(media.currentSrc || media.src || media.readyState > 0);
            const payload = {
                type: "aero-media-state",
                hasMedia,
                isPlaying: hasMedia && !media.paused && !media.ended,
                isMuted: hasMedia && (media.muted || media.volume === 0),
                title: metadata?.title || document.title || "Media",
                artist: metadata?.artist || metadata?.album || "",
                artwork
            };
            const serialized = JSON.stringify(payload);
            if (serialized !== lastPayload) {
                lastPayload = serialized;
                window.chrome?.webview?.postMessage(payload);
            }
        } catch {
            // Media detection must never interfere with the page.
        }
    };

    const scheduleReport = () => {
        if (reportTimer) return;
        reportTimer = window.setTimeout(() => {
            reportTimer = 0;
            report();
        }, 250);
    };

    window.__aeroMediaHubReport = report;
    window.__aeroMediaHubCommand = async action => {
        const media = selectMedia();
        if (!media) return false;

        try {
            if (action === "playPause") {
                if (media.paused || media.ended) await media.play();
                else media.pause();
            } else if (action === "mute") {
                media.muted = !media.muted;
            }
            report();
            return true;
        } catch {
            return false;
        }
    };

    for (const eventName of ["play", "pause", "ended", "volumechange", "loadedmetadata", "emptied"]) {
        document.addEventListener(eventName, scheduleReport, true);
    }

    // YouTube mutates thousands of DOM nodes while loading and while scrolling.
    // Debounce those mutations and do not observe every src attribute change.
    new MutationObserver(scheduleReport).observe(document.documentElement, {
        childList: true,
        subtree: true
    });

    setInterval(report, 3000);
    report();
})();
""";

    private CancellationTokenSource? _mediaDownloadCancellation;
    private bool _mediaDownloadInProgress;
    private string? _lastMediaDownloadFile;

    private async Task EnsureMediaBridgeOnCurrentDocumentAsync(BrowserTabState state)
    {
        if (!_tabs.Contains(state) || state.Browser.CoreWebView2 is not CoreWebView2 core)
        {
            return;
        }

        try
        {
            await core.ExecuteScriptAsync(MediaBridgeScript);
        }
        catch
        {
            // The page may be navigating or closing.
        }
    }

    private void Browser_WebMessageReceived(
        BrowserTabState state,
        CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using JsonDocument message = JsonDocument.Parse(args.WebMessageAsJson);
            JsonElement root = message.RootElement;
            if (!root.TryGetProperty("type", out JsonElement typeElement))
            {
                return;
            }

            string? messageType = typeElement.GetString();
            if (string.Equals(messageType, "aero-home-search", StringComparison.Ordinal) &&
                state.IsStartPage)
            {
                string query = ReadString(root, "query");
                if (!string.IsNullOrWhiteSpace(query))
                {
                    // Same resolution as the address bar: "youtube.com" opens the site, not a search.
                    RootGrid.DispatcherQueue.TryEnqueue(() => Navigate(
                        state,
                        AddressInputResolver.Resolve(query.Trim(), _settings.SearchEngine, _settings.CustomSearchUrl).Uri));
                }
                return;
            }

            if (string.Equals(messageType, "aero-home-quick-link", StringComparison.Ordinal) &&
                state.IsStartPage)
            {
                JsonElement quickLinkMessage = root.Clone();
                RootGrid.DispatcherQueue.TryEnqueue(
                    async () => await HandleHomeQuickLinkMessageAsync(state, quickLinkMessage));
                return;
            }

            if (messageType is not null &&
                TryHandleSettingsMessage(state, root, messageType))
            {
                return;
            }

            if (TryHandleVaultFillResultMessage(state, args, root, messageType) ||
                TryHandlePasswordCaptureMessage(state, args, root, messageType))
            {
                return;
            }

            if (string.Equals(messageType, "aero-page-zoom", StringComparison.Ordinal))
            {
                string action = ReadString(root, "action");
                RootGrid.DispatcherQueue.TryEnqueue(() => HandlePageZoomMessage(state, action));
                return;
            }

            if (string.Equals(messageType, "aero-network-error", StringComparison.Ordinal))
            {
                string action = ReadString(root, "action");
                RootGrid.DispatcherQueue.TryEnqueue(() => HandleNetworkErrorMessage(state, action));
                return;
            }

            if (!string.Equals(messageType, "aero-media-state", StringComparison.Ordinal))
            {
                return;
            }

            state.HasMedia = ReadBoolean(root, "hasMedia");
            state.MediaIsPlaying = ReadBoolean(root, "isPlaying");
            state.MediaIsMuted = ReadBoolean(root, "isMuted");
            state.MediaTitle = ReadString(root, "title");
            state.MediaArtist = ReadString(root, "artist");
            state.MediaArtwork = ReadString(root, "artwork");
            state.LastMediaUpdate = DateTimeOffset.UtcNow;

            RootGrid.DispatcherQueue.TryEnqueue(() =>
            {
                if (!_tabs.Contains(state))
                {
                    return;
                }

                UpdateMediaHubAvailability();
                UpdateMediaHubUi();
            });
        }
        catch
        {
            // Ignore messages that do not belong to Aero Media Hub.
        }
    }


    private async Task HandleHomeQuickLinkMessageAsync(
        BrowserTabState state,
        JsonElement message)
    {
        if (!_tabs.Contains(state) || !state.IsStartPage)
        {
            return;
        }

        try
        {
            string action = ReadString(message, "action");
            switch (action)
            {
                case "upsert":
                    _dataStore.UpsertQuickLink(
                        ReadString(message, "id"),
                        ReadString(message, "url"),
                        ReadString(message, "title"),
                        ReadString(message, "icon"));
                    break;
                case "remove":
                    _dataStore.RemoveQuickLink(ReadString(message, "id"));
                    break;
                case "reorder":
                    List<string> order = [];
                    if (message.TryGetProperty("order", out JsonElement orderElement) &&
                        orderElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in orderElement.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String &&
                                !string.IsNullOrWhiteSpace(item.GetString()))
                            {
                                order.Add(item.GetString()!);
                            }
                        }
                    }
                    _dataStore.ReorderQuickLinks(order);
                    break;
                default:
                    return;
            }

            RefreshStartPages();
        }
        catch (Exception exception)
        {
            if (state.Browser.CoreWebView2 is CoreWebView2 core)
            {
                string messageJson = JsonSerializer.Serialize(exception.Message);
                try
                {
                    await core.ExecuteScriptAsync($"window.aeroHomeStatus?.({messageJson}, true);");
                }
                catch
                {
                    // The start page may already be refreshing or closing.
                }
            }
        }
    }

    private static bool ReadBoolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean();

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private void ResetMediaState(BrowserTabState state)
    {
        state.HasMedia = false;
        state.MediaIsPlaying = false;
        state.MediaIsMuted = false;
        state.MediaTitle = string.Empty;
        state.MediaArtist = string.Empty;
        state.MediaArtwork = string.Empty;
        state.LastMediaUpdate = null;
        UpdateMediaHubAvailability();
    }

    private void UpdateMediaHubAvailability()
    {
        bool hasMedia = _tabs.Any(state => state.HasMedia || state.IsPlayingAudio);
        bool isPlaying = _tabs.Any(state => state.MediaIsPlaying || state.IsPlayingAudio);

        MediaHubButton.Visibility = hasMedia && !_narrowNavigation
            ? Visibility.Visible
            : Visibility.Collapsed;
        MediaHubIcon.Glyph = isPlaying ? "\uE769" : "\uE768";
        ToolTipService.SetToolTip(
            MediaHubButton,
            isPlaying ? "Media is playing" : "Media controls");
    }

    private void MediaHubFlyout_Opened(object sender, object e)
    {
        UpdateMediaHubUi();
    }

    private void UpdateMediaHubUi()
    {
        if (MediaSessionsPanel is null)
        {
            return;
        }

        List<BrowserTabState> mediaTabs = _tabs
            .Where(state => state.HasMedia || state.IsPlayingAudio)
            .OrderByDescending(state => state.MediaIsPlaying || state.IsPlayingAudio)
            .ThenByDescending(state => state.LastMediaUpdate)
            .ToList();

        MediaSessionsPanel.Children.Clear();
        MediaHubEmptyState.Visibility = mediaTabs.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        MediaHubSummaryText.Text = mediaTabs.Count switch
        {
            0 => "No active media",
            1 => "1 tab with media",
            _ => $"{mediaTabs.Count} tabs with media"
        };

        foreach (BrowserTabState state in mediaTabs)
        {
            MediaSessionsPanel.Children.Add(BuildMediaSessionCard(state));
        }

        CancelMediaDownloadButton.Visibility = _mediaDownloadInProgress
            ? Visibility.Visible
            : Visibility.Collapsed;
        OpenMediaDownloadButton.Visibility = !_mediaDownloadInProgress &&
            !string.IsNullOrWhiteSpace(_lastMediaDownloadFile)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private Border BuildMediaSessionCard(BrowserTabState state)
    {
        string title = string.IsNullOrWhiteSpace(state.MediaTitle)
            ? string.IsNullOrWhiteSpace(state.Title) ? "Media" : state.Title
            : state.MediaTitle;
        string source = state.CurrentUri?.Host ?? state.PendingUri?.Host ?? "Current tab";
        string secondary = string.IsNullOrWhiteSpace(state.MediaArtist)
            ? source
            : $"{state.MediaArtist} · {source}";
        bool isPlaying = state.MediaIsPlaying || state.IsPlayingAudio;
        bool isMuted = state.MediaIsMuted || state.Browser.CoreWebView2?.IsMuted == true;

        Grid header = new() { ColumnSpacing = 9 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid artworkHost = new();
        artworkHost.Children.Add(new FontIcon
        {
            Glyph = "\uE8D6",
            FontSize = 17,
            Foreground = GetMutedTextBrush(),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (Uri.TryCreate(state.MediaArtwork, UriKind.Absolute, out Uri? artworkUri))
        {
            artworkHost.Children.Add(new Image
            {
                Width = 42,
                Height = 42,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                Source = new BitmapImage(artworkUri)
            });
        }

        Border artwork = new()
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(8),
            Background = GetSoftBorderBrush(),
            Child = artworkHost
        };
        header.Children.Add(artwork);

        StackPanel labels = new() { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        });
        labels.Children.Add(new TextBlock
        {
            Text = secondary,
            FontSize = 11,
            Foreground = GetMutedTextBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        });
        Grid.SetColumn(labels, 1);
        header.Children.Add(labels);

        Button openTabButton = new()
        {
            Height = 28,
            Padding = new Thickness(10, 0, 10, 0),
            Content = "Open tab",
            Tag = state
        };
        openTabButton.Click += MediaOpenTabButton_Click;
        Grid.SetColumn(openTabButton, 2);
        header.Children.Add(openTabButton);

        StackPanel controls = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };
        controls.Children.Add(BuildMediaActionButton(
            isPlaying ? "Pause" : "Play",
            new MediaActionTag(state, "playPause"),
            MediaControlButton_Click));
        controls.Children.Add(BuildMediaActionButton(
            isMuted ? "Unmute" : "Mute",
            new MediaActionTag(state, "mute"),
            MediaControlButton_Click));
        controls.Children.Add(BuildMediaActionButton(
            "Download video",
            new MediaDownloadTag(state, MediaDownloadKind.Video),
            MediaDownloadButton_Click,
            !_mediaDownloadInProgress && CanDownloadMedia(state)));
        controls.Children.Add(BuildMediaActionButton(
            "Audio",
            new MediaDownloadTag(state, MediaDownloadKind.Audio),
            MediaDownloadButton_Click,
            !_mediaDownloadInProgress && CanDownloadMedia(state)));

        StackPanel content = new() { Spacing = 11 };
        content.Children.Add(header);
        content.Children.Add(controls);

        return new Border
        {
            Padding = new Thickness(12),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                IsDarkTheme()
                    ? Windows.UI.Color.FromArgb(255, 24, 29, 33)
                    : Windows.UI.Color.FromArgb(255, 255, 255, 255)),
            CornerRadius = new CornerRadius(10),
            Child = content
        };
    }

    private static Button BuildMediaActionButton(
        string text,
        object tag,
        RoutedEventHandler handler,
        bool enabled = true)
    {
        Button button = new()
        {
            Height = 30,
            Padding = new Thickness(11, 0, 11, 0),
            Content = text,
            Tag = tag,
            IsEnabled = enabled
        };
        button.Click += handler;
        return button;
    }

    private async void MediaControlButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MediaActionTag action } ||
            !_tabs.Contains(action.State) ||
            action.State.Browser.CoreWebView2 is not CoreWebView2 core)
        {
            return;
        }

        try
        {
            string serializedAction = JsonSerializer.Serialize(action.Action);
            await core.ExecuteScriptAsync($"window.__aeroMediaHubCommand?.({serializedAction});");
        }
        catch
        {
            // The media document may be navigating.
        }
    }

    private void MediaOpenTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BrowserTabState state } || !_tabs.Contains(state))
        {
            return;
        }

        SelectTab(state);
        state.Browser.Focus(FocusState.Programmatic);
        MediaHubFlyout.Hide();
    }

    private async void MediaDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MediaDownloadTag request })
        {
            return;
        }

        await DownloadMediaFromPageAsync(request.State, request.Kind);
    }

    private async Task DownloadMediaFromPageAsync(
        BrowserTabState state,
        MediaDownloadKind kind)
    {
        if (_mediaDownloadInProgress ||
            !TryGetDownloadPageUri(state, out Uri? pageUri) ||
            pageUri is null)
        {
            return;
        }

        _mediaDownloadCancellation?.Cancel();
        _mediaDownloadCancellation?.Dispose();
        _mediaDownloadCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _mediaDownloadCancellation.Token;

        _mediaDownloadInProgress = true;
        _lastMediaDownloadFile = null;
        MediaDownloadStatusText.Text = kind == MediaDownloadKind.Video
            ? "Reading available video qualities…"
            : "Reading available audio formats…";
        MediaDownloadStatusText.Visibility = Visibility.Visible;
        UpdateMediaHubUi();

        Progress<string> progress = new(message =>
        {
            RootGrid.DispatcherQueue.TryEnqueue(() =>
            {
                MediaDownloadStatusText.Text = message;
            });
        });

        MediaDownloadCatalog catalog;
        string? lookupCookies = null;
        try
        {
            lookupCookies = await WriteYouTubeCookieFileAsync(state, pageUri);
            catalog = await YtDlpService.GetDownloadOptionsAsync(
                pageUri,
                kind,
                lookupCookies,
                progress,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _mediaDownloadInProgress = false;
            MediaDownloadStatusText.Text = "Format lookup cancelled.";
            UpdateMediaHubUi();
            return;
        }
        catch (Exception exception)
        {
            _mediaDownloadInProgress = false;
            MediaDownloadStatusText.Text = exception.Message;
            UpdateMediaHubUi();
            return;
        }
        finally
        {
            DeleteMediaCookieFile(lookupCookies);
        }

        _mediaDownloadInProgress = false;
        MediaDownloadStatusText.Text = $"{catalog.Options.Count} download options available.";
        UpdateMediaHubUi();

        MediaHubFlyout.Hide();
        MediaDownloadOption? option = await ShowMediaDownloadOptionsAsync(catalog, kind);
        if (option is null)
        {
            MediaDownloadStatusText.Text = "Download selection cancelled.";
            return;
        }

        StorageFolder? folder = await PickMediaDownloadFolderAsync();
        if (folder is null)
        {
            MediaDownloadStatusText.Text = "Download cancelled.";
            return;
        }

        _mediaDownloadInProgress = true;
        _lastMediaDownloadFile = null;
        MediaDownloadStatusText.Text = $"Preparing {option.Title}…";
        MediaDownloadStatusText.Visibility = Visibility.Visible;
        UpdateMediaHubUi();

        MediaDownloadResult result;
        string? downloadCookies = null;
        try
        {
            downloadCookies = await WriteYouTubeCookieFileAsync(state, pageUri);
            result = await YtDlpService.DownloadAsync(
                pageUri,
                folder.Path,
                option,
                downloadCookies,
                progress,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            result = new MediaDownloadResult(false, null, "Download cancelled.");
        }
        catch (Exception exception)
        {
            result = new MediaDownloadResult(false, null, exception.Message);
        }
        finally
        {
            DeleteMediaCookieFile(downloadCookies);
        }

        _mediaDownloadInProgress = false;
        _lastMediaDownloadFile = result.Success ? result.FilePath : null;
        MediaDownloadStatusText.Text = result.Message;
        UpdateMediaHubUi();
    }

    private async Task<MediaDownloadOption?> ShowMediaDownloadOptionsAsync(
        MediaDownloadCatalog catalog,
        MediaDownloadKind kind)
    {
        ListView optionsList = new()
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            MaxHeight = 470,
            MinWidth = 540,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        foreach (MediaDownloadOption downloadOption in catalog.Options)
        {
            optionsList.Items.Add(BuildMediaDownloadOptionRow(downloadOption));
        }

        if (optionsList.Items.Count > 0)
        {
            optionsList.SelectedIndex = 0;
        }

        StackPanel content = new()
        {
            Spacing = 12,
            MaxWidth = 680
        };
        content.Children.Add(new TextBlock
        {
            Text = catalog.MediaTitle,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2
        });
        content.Children.Add(new TextBlock
        {
            Text = kind == MediaDownloadKind.Video
                ? "Choose the real container, resolution and frame rate reported by yt-dlp. Maximum quality has no resolution or FPS cap."
                : "Choose the original audio stream or an MP3 conversion quality.",
            FontSize = 12,
            Foreground = GetMutedTextBrush(),
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(optionsList);

        ContentDialog dialog = new()
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = kind == MediaDownloadKind.Video
                ? "Download video"
                : "Download audio",
            Content = content,
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = optionsList.Items.Count > 0
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary ||
            optionsList.SelectedItem is not FrameworkElement { Tag: MediaDownloadOption option })
        {
            return null;
        }

        return option;
    }

    private Border BuildMediaDownloadOptionRow(MediaDownloadOption option)
    {
        Grid row = new()
        {
            ColumnSpacing = 12,
            Padding = new Thickness(4, 7, 4, 7)
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        StackPanel labels = new() { Spacing = 3 };
        labels.Children.Add(new TextBlock
        {
            Text = option.Title,
            FontSize = 13,
            FontWeight = option.IsRecommended
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        });
        labels.Children.Add(new TextBlock
        {
            Text = option.Description,
            FontSize = 11,
            Foreground = GetMutedTextBrush(),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2
        });
        row.Children.Add(labels);

        if (option.IsRecommended)
        {
            Border badge = new()
            {
                Padding = new Thickness(8, 3, 8, 3),
                CornerRadius = new CornerRadius(10),
                Background = GetSoftBorderBrush(),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "Best",
                    FontSize = 10,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                }
            };
            Grid.SetColumn(badge, 1);
            row.Children.Add(badge);
        }

        return new Border
        {
            Tag = option,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = row
        };
    }

    private async Task<StorageFolder?> PickMediaDownloadFolderAsync()
    {
        FolderPicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.Downloads
        };
        picker.FileTypeFilter.Add("*");

        nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
        return await picker.PickSingleFolderAsync();
    }

    private static bool CanDownloadMedia(BrowserTabState state) =>
        TryGetDownloadPageUri(state, out _);

    // Hands yt-dlp the cookies of the YouTube account signed in on this profile, otherwise
    // YouTube answers with "Sign in to confirm you're not a bot". Only youtube.com cookies are
    // exported, into the profile's private folder, and the file lives only for one yt-dlp run.
    // shortcut: the cookies sit on disk while yt-dlp runs (it only reads a file path); a same-user
    // stealer could read them in that window, but it can already decrypt the WebView2 cookie DB.
    internal const string MediaCookiesFolderName = "MediaCookies";

    private async Task<string?> WriteYouTubeCookieFileAsync(BrowserTabState state, Uri pageUri)
    {
        if (!IsYouTubeHost(pageUri.Host) || state.Browser.CoreWebView2 is not CoreWebView2 core)
        {
            return null;
        }

        IReadOnlyList<CoreWebView2Cookie> cookies;
        try
        {
            cookies = await core.CookieManager.GetCookiesAsync("https://www.youtube.com/");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null; // Fall back to an anonymous yt-dlp run.
        }

        if (cookies.Count == 0)
        {
            return null;
        }

        string folder = Path.Combine(_browserProfile.StorageRoot, MediaCookiesFolderName);
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (string stale in Directory.EnumerateFiles(folder, "*.txt"))
        {
            // Leftovers from a crash mid-download.
            if (File.GetLastWriteTimeUtc(stale) < DateTime.UtcNow.AddHours(-1))
            {
                DeleteMediaCookieFile(stale);
            }
        }

        string path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".txt");
        string content = NetscapeCookieFile.Format(cookies.Select(static cookie => new ExportedCookie(
            cookie.Domain,
            cookie.Path,
            cookie.IsSecure,
            cookie.IsHttpOnly,
            cookie.IsSession ? null : cookie.Expires,
            cookie.Name,
            cookie.Value)));
        try
        {
            await File.WriteAllTextAsync(path, content);
            File.SetAttributes(path, System.IO.FileAttributes.Hidden | System.IO.FileAttributes.Temporary | System.IO.FileAttributes.NotContentIndexed);
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DeleteMediaCookieFile(path);
            return null;
        }
    }

    private static void DeleteMediaCookieFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Swept on the next download once it is older than an hour.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool TryGetDownloadPageUri(BrowserTabState state, out Uri? uri)
    {
        uri = state.CurrentUri ?? state.PendingUri;
        return uri is not null &&
            (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }

    private void CancelMediaDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        _mediaDownloadCancellation?.Cancel();
        MediaDownloadStatusText.Text = "Cancelling download…";
    }

    private void OpenMediaDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        string? path = _lastMediaDownloadFile;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
        }
        catch
        {
            // Explorer may be unavailable in restricted environments.
        }
    }

    private void CleanupMediaHub()
    {
        _mediaDownloadCancellation?.Cancel();
        _mediaDownloadCancellation?.Dispose();
        _mediaDownloadCancellation = null;
    }

    private readonly record struct MediaActionTag(BrowserTabState State, string Action);

    private readonly record struct MediaDownloadTag(BrowserTabState State, MediaDownloadKind Kind);
}
