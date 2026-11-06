using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Aero.WebView.Permissions;

public sealed class SitePermissionService
{
    private static readonly CoreWebView2PermissionKind[] SupportedPermissionKindsArray =
    [
        CoreWebView2PermissionKind.Camera,
        CoreWebView2PermissionKind.Microphone,
        CoreWebView2PermissionKind.Geolocation,
        CoreWebView2PermissionKind.Notifications,
        CoreWebView2PermissionKind.ClipboardRead,
        CoreWebView2PermissionKind.MultipleAutomaticDownloads
    ];

    public static IReadOnlyList<CoreWebView2PermissionKind> SupportedPermissionKinds { get; } =
        Array.AsReadOnly(SupportedPermissionKindsArray);

    public static bool IsSupported(CoreWebView2PermissionKind permissionKind) =>
        Array.IndexOf(SupportedPermissionKindsArray, permissionKind) >= 0;

    public static bool TryGetPermissionKind(
        string? key,
        out CoreWebView2PermissionKind permissionKind)
    {
        permissionKind = key switch
        {
            "Camera" => CoreWebView2PermissionKind.Camera,
            "Microphone" => CoreWebView2PermissionKind.Microphone,
            "Geolocation" => CoreWebView2PermissionKind.Geolocation,
            "Notifications" => CoreWebView2PermissionKind.Notifications,
            "ClipboardRead" => CoreWebView2PermissionKind.ClipboardRead,
            "MultipleAutomaticDownloads" => CoreWebView2PermissionKind.MultipleAutomaticDownloads,
            _ => CoreWebView2PermissionKind.UnknownPermission
        };

        return IsSupported(permissionKind);
    }

    public static string? NormalizeOrigin(Uri? uri)
    {
        if (uri is null ||
            !uri.IsAbsoluteUri ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return null;
        }

        return uri.GetComponents(
            UriComponents.SchemeAndServer,
            UriFormat.UriEscaped).TrimEnd('/');
    }

    public static string? NormalizeOrigin(string? uri)
    {
        return Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed)
            ? NormalizeOrigin(parsed)
            : null;
    }

    public async Task<SitePermissionSnapshot> GetSnapshotAsync(
        CoreWebView2Profile profile,
        string origin)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string normalizedOrigin = NormalizeOrigin(origin) ??
            throw new ArgumentException("A valid HTTP or HTTPS origin is required.", nameof(origin));

        Dictionary<CoreWebView2PermissionKind, SitePermissionValue> states = [];
        foreach (CoreWebView2PermissionKind permissionKind in SupportedPermissionKindsArray)
        {
            states[permissionKind] = SitePermissionValue.Ask;
        }

        IReadOnlyList<CoreWebView2PermissionSetting> settings =
            await profile.GetNonDefaultPermissionSettingsAsync();
        foreach (CoreWebView2PermissionSetting setting in settings)
        {
            if (!IsSupported(setting.PermissionKind) ||
                !string.Equals(
                    NormalizeOrigin(setting.PermissionOrigin),
                    normalizedOrigin,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            states[setting.PermissionKind] = ToSitePermissionValue(setting.PermissionState);
        }

        return new SitePermissionSnapshot(
            normalizedOrigin,
            new ReadOnlyDictionary<CoreWebView2PermissionKind, SitePermissionValue>(states));
    }

    public async Task SetStateAsync(
        CoreWebView2Profile profile,
        string origin,
        CoreWebView2PermissionKind permissionKind,
        SitePermissionValue state)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!IsSupported(permissionKind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(permissionKind),
                permissionKind,
                "This permission kind is not managed by Aero's WebView2 site-permission panel.");
        }

        string normalizedOrigin = NormalizeOrigin(origin) ??
            throw new ArgumentException("A valid HTTP or HTTPS origin is required.", nameof(origin));

        await profile.SetPermissionStateAsync(
            permissionKind,
            normalizedOrigin,
            ToCorePermissionState(state));
    }

    public async Task ResetAsync(CoreWebView2Profile profile, string origin)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string normalizedOrigin = NormalizeOrigin(origin) ??
            throw new ArgumentException("A valid HTTP or HTTPS origin is required.", nameof(origin));

        foreach (CoreWebView2PermissionKind permissionKind in SupportedPermissionKindsArray)
        {
            await profile.SetPermissionStateAsync(
                permissionKind,
                normalizedOrigin,
                CoreWebView2PermissionState.Default);
        }
    }

    private static SitePermissionValue ToSitePermissionValue(
        CoreWebView2PermissionState state) => state switch
    {
        CoreWebView2PermissionState.Allow => SitePermissionValue.Allow,
        CoreWebView2PermissionState.Deny => SitePermissionValue.Block,
        _ => SitePermissionValue.Ask
    };

    private static CoreWebView2PermissionState ToCorePermissionState(
        SitePermissionValue state) => state switch
    {
        SitePermissionValue.Allow => CoreWebView2PermissionState.Allow,
        SitePermissionValue.Block => CoreWebView2PermissionState.Deny,
        _ => CoreWebView2PermissionState.Default
    };
}
