namespace Aero.Extensions;

public enum ExtensionPermissionRisk
{
    Low,
    Medium,
    High,
    Critical
}

public static class ExtensionPermissionClassifier
{
    private static readonly HashSet<string> CriticalPermissions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "<all_urls>",
        "*://*/*",
        "debugger",
        "nativeMessaging",
        "proxy"
    };

    private static readonly HashSet<string> HighPermissions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "cookies",
        "history",
        "management",
        "scripting",
        "tabs",
        "webNavigation",
        "webRequest",
        "webRequestBlocking"
    };

    private static readonly HashSet<string> MediumPermissions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "bookmarks",
        "clipboardRead",
        "clipboardWrite",
        "downloads",
        "geolocation",
        "notifications",
        "storage"
    };

    public static ExtensionPermissionRisk Classify(IEnumerable<string>? permissions)
    {
        ExtensionPermissionRisk result = ExtensionPermissionRisk.Low;
        if (permissions is null)
        {
            return result;
        }

        foreach (string rawPermission in permissions)
        {
            string permission = rawPermission?.Trim() ?? string.Empty;
            if (permission.Length == 0)
            {
                continue;
            }

            if (CriticalPermissions.Contains(permission) || IsBroadHostPattern(permission))
            {
                return ExtensionPermissionRisk.Critical;
            }

            if (HighPermissions.Contains(permission) || permission.Contains("://", StringComparison.Ordinal))
            {
                result = ExtensionPermissionRisk.High;
                continue;
            }

            if (result < ExtensionPermissionRisk.Medium && MediumPermissions.Contains(permission))
            {
                result = ExtensionPermissionRisk.Medium;
            }
        }

        return result;
    }

    public static bool CanObservePageContent(IEnumerable<string>? permissions) =>
        Classify(permissions) >= ExtensionPermissionRisk.High;

    private static bool IsBroadHostPattern(string permission)
    {
        if (!permission.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        return permission.StartsWith("*://", StringComparison.OrdinalIgnoreCase) ||
               permission.Contains("://*", StringComparison.Ordinal) ||
               permission.Contains("://*.", StringComparison.Ordinal);
    }
}
