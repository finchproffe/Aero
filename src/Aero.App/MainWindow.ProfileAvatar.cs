using Aero.App.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace Aero.App;

// Custom profile photos. The picked file is decoded and re-encoded as a 128 px PNG, so only a
// real image ever reaches the profile folder or the settings page.
public sealed partial class MainWindow
{
    private const uint AvatarSize = 128;
    private const ulong MaximumAvatarSourceBytes = 25 * 1024 * 1024;

    // A round photo when the profile has one, otherwise its emoji icon.
    private static FrameworkElement BuildProfileAvatarVisual(BrowserProfile profile, double size, double emojiSize) =>
        BrowserProfileManager.GetAvatarPath(profile) is string path
            ? BuildAvatarEllipse(new BitmapImage
            {
                CreateOptions = BitmapCreateOptions.IgnoreImageCache, // the file is replaced in place
                DecodePixelWidth = (int)Math.Ceiling(size * 2),
                UriSource = new Uri(path)
            }, size)
            : new TextBlock
            {
                Text = profile.Icon,
                FontSize = emojiSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

    private static Ellipse BuildAvatarEllipse(ImageSource image, double size) => new()
    {
        Width = size,
        Height = size,
        Fill = new ImageBrush { ImageSource = image, Stretch = Stretch.UniformToFill }
    };

    private static string? GetProfileAvatarDataUrl(BrowserProfile profile)
    {
        try
        {
            return BrowserProfileManager.GetAvatarPath(profile) is string path
                ? "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(path))
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Returns the re-encoded PNG, or null when the user cancels or the file is not an image.
    private async Task<byte[]?> PickProfileAvatarAsync()
    {
        FileOpenPicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.Thumbnail
        };
        foreach (string extension in new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" })
        {
            picker.FileTypeFilter.Add(extension);
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return null;
        }

        try
        {
            return await EncodeAvatarAsync(file);
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or ArgumentException or IOException)
        {
            return null; // not a decodable image
        }
    }

    private static async Task<byte[]?> EncodeAvatarAsync(StorageFile file)
    {
        using IRandomAccessStream input = await file.OpenReadAsync();
        if (input.Size == 0 || input.Size > MaximumAvatarSourceBytes)
        {
            return null;
        }

        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);
        uint width = decoder.OrientedPixelWidth, height = decoder.OrientedPixelHeight;
        if (width == 0 || height == 0)
        {
            return null;
        }

        // Scale the short side to AvatarSize, then crop the centre square.
        double scale = (double)AvatarSize / Math.Min(width, height);
        uint scaledWidth = Math.Max(AvatarSize, (uint)Math.Round(width * scale));
        uint scaledHeight = Math.Max(AvatarSize, (uint)Math.Round(height * scale));
        BitmapTransform transform = new()
        {
            ScaledWidth = scaledWidth,
            ScaledHeight = scaledHeight,
            InterpolationMode = BitmapInterpolationMode.Fant,
            Bounds = new BitmapBounds
            {
                X = (scaledWidth - AvatarSize) / 2,
                Y = (scaledHeight - AvatarSize) / 2,
                Width = AvatarSize,
                Height = AvatarSize
            }
        };
        PixelDataProvider pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);

        using InMemoryRandomAccessStream output = new();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, AvatarSize, AvatarSize, 96, 96, pixels.DetachPixelData());
        await encoder.FlushAsync();

        byte[] png = new byte[output.Size];
        using DataReader reader = new(output.GetInputStreamAt(0));
        await reader.LoadAsync((uint)output.Size);
        reader.ReadBytes(png);
        return png;
    }

    private async Task SetProfileAvatarAsync(BrowserTabState? settingsTab, string profileId, bool remove)
    {
        if (BrowserProfileManager.Find(profileId) is not BrowserProfile profile)
        {
            return;
        }

        byte[]? png = null;
        if (!remove)
        {
            png = await PickProfileAvatarAsync();
            if (png is null)
            {
                return;
            }
        }

        try
        {
            BrowserProfileManager.SetAvatar(profile, png);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (settingsTab is not null)
            {
                await PostSettingsStatusAsync(settingsTab, "The profile photo could not be saved.", isError: true);
            }

            return;
        }

        foreach (MainWindow window in OpenWindows)
        {
            window.RefreshProfileSwitcher();
        }

        if (settingsTab is not null)
        {
            await RefreshSettingsPageAsync(settingsTab, remove ? "Profile photo removed." : "Profile photo updated.", "profiles");
        }
    }
}
