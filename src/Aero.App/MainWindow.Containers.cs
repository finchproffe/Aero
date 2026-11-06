using Aero.WebView;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace Aero.App;

internal sealed record TabContainer(string Id, string Name, Color Color, bool IsBurner);

public sealed partial class MainWindow
{
    internal const string BurnerContainerId = "burner";

    // Each container is its own WebView2 profile: separate cookies, storage, cache and logins,
    // side by side in one window. The burner is an InPrivate profile unique to one tab, so its
    // data disappears when that tab closes.
    private static readonly TabContainer[] TabContainers =
    [
        new("work", "Work", Color.FromArgb(255, 0x4C, 0x8D, 0xF6), IsBurner: false),
        new("personal", "Personal", Color.FromArgb(255, 0x3F, 0xB2, 0x7F), IsBurner: false),
        new("shopping", "Shopping", Color.FromArgb(255, 0xE8, 0xA3, 0x3D), IsBurner: false),
        new(BurnerContainerId, "Burner tab", Color.FromArgb(255, 0xE2, 0x5A, 0x5A), IsBurner: true)
    ];

    private static TabContainer? FindTabContainer(string? id) =>
        id is null ? null : Array.Find(TabContainers, container => container.Id == id);

    private static bool IsBurnerTab(BrowserTabState state) => state.ContainerId == BurnerContainerId;

    // uBlock Origin is installed into the main profile only; container profiles fall back to
    // the native Shield engine so they are not left without blocking.
    private bool HasAdvancedShield(BrowserTabState? state) =>
        IsUsingUBlockOriginEngine() && state?.ContainerId is null;

    private WebViewProfileOptions? GetWebViewProfileOptions(BrowserTabState state)
    {
        TabContainer? container = FindTabContainer(state.ContainerId);
        if (container is null)
        {
            return _isPrivateWindow ? new WebViewProfileOptions(PrivateProfileName, IsInPrivateModeEnabled: true) : null;
        }

        if (container.IsBurner)
        {
            state.BurnerProfileName ??= "AeroBurner" + Guid.NewGuid().ToString("N");
            return new WebViewProfileOptions(state.BurnerProfileName, IsInPrivateModeEnabled: true);
        }

        return _isPrivateWindow
            ? new WebViewProfileOptions($"{PrivateProfileName}-{container.Id}", IsInPrivateModeEnabled: true)
            : new WebViewProfileOptions($"AeroContainer-{container.Id}", IsInPrivateModeEnabled: false);
    }

    private Task<BrowserTabState> OpenInContainerAsync(Uri? uri, string containerId) =>
        CreateTabAsync(uri, selectTab: true, showStartPage: uri is null, containerId: containerId);

    /// <summary>Menu items "Work / Personal / Shopping / Burner tab" that open <paramref name="uri"/>.</summary>
    private IEnumerable<MenuFlyoutItem> BuildContainerMenuItems(Func<Uri?> uri)
    {
        foreach (TabContainer container in TabContainers)
        {
            MenuFlyoutItem item = new()
            {
                Text = Localization.AeroLocalizer.TranslateUi(container.Name),
                Icon = new FontIcon
                {
                    Glyph = container.IsBurner ? "" : "",
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(container.Color)
                }
            };
            string id = container.Id;
            item.Click += async (_, _) => await OpenInContainerAsync(uri(), id);
            yield return item;
        }
    }

    private void AttachAddTabContainerMenu()
    {
        MenuFlyout flyout = new();
        foreach (MenuFlyoutItem item in BuildContainerMenuItems(static () => null))
        {
            flyout.Items.Add(item);
        }

        AddTabButton.ContextFlyout = flyout;
    }

    private static void ApplyContainerVisual(BrowserTabState state)
    {
        TabContainer? container = FindTabContainer(state.ContainerId);
        state.ContainerStripe.Visibility = container is null ? Visibility.Collapsed : Visibility.Visible;
        if (container is not null)
        {
            state.ContainerStripe.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(container.Color);
            ToolTipService.SetToolTip(
                state.ContainerStripe,
                Localization.AeroLocalizer.TranslateUi(container.Name));
        }
    }
}
