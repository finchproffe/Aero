using Microsoft.Win32;

namespace Aero.WebView;

public sealed class WebViewLaunchBlockedException(string message) : Exception(message);

/// <summary>
/// The WebView2 loader takes browser arguments, runtime folder and profile folder from
/// environment variables (settable by any process of the user, persistently via HKCU\Environment)
/// and from Edge WebView2 policies (admin-only ACL by default), and they override what Aero
/// passes. Info-stealers use this to start the browser with --remote-debugging-port and then
/// pull every cookie over the DevTools protocol.
/// </summary>
public static class WebViewLaunchGuard
{
    private const string PolicyRoot = @"Software\Policies\Microsoft\Edge\WebView2\";

    private static readonly string[] LoaderEnvironmentVariables =
    [
        "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
        "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER",
        "WEBVIEW2_USER_DATA_FOLDER",
        "WEBVIEW2_RELEASE_CHANNEL_PREFERENCE",
        "WEBVIEW2_CHANNEL_SEARCH_KIND",
        "WEBVIEW2_PIPE_FOR_SCRIPT_DEBUGGER"
    ];

    // Switches that expose the profile to another process or remove a security boundary.
    private static readonly string[] DangerousSwitches =
    [
        "remote-debugging",          // -port and -pipe: full cookie/DOM access over CDP
        "remote-allow-origins",
        "load-extension",
        "disable-extensions-except",
        "user-data-dir",
        "disable-web-security",
        "ignore-certificate-errors",
        "ignore-certificate-errors-spki-list",
        "no-sandbox",
        "enable-automation",
        "headless",
        "auto-open-devtools-for-tabs",
        "renderer-cmd-prefix",
        "utility-cmd-prefix",
        "gpu-launcher",
        "browser-subprocess-path"
    ];

    /// <summary>Removes loader overrides from this process before WebView2 starts.</summary>
    public static IReadOnlyList<string> ScrubLoaderEnvironment()
    {
        List<string> removed = [];
        foreach (string name in LoaderEnvironmentVariables)
        {
            if (Environment.GetEnvironmentVariable(name) is not null)
            {
                Environment.SetEnvironmentVariable(name, null);
                removed.Add(name);
            }
        }

        return removed;
    }

    /// <summary>Returns the first dangerous Chromium switch in an argument string, or null.</summary>
    public static string? FindDangerousSwitch(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return null;
        }

        foreach (string raw in arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            // Chromium on Windows accepts "--", "-" and "/" prefixes, optionally quoted.
            string token = raw.Trim('"', '\'');
            string name = token.TrimStart('-', '/');
            if (name.Length == token.Length)
            {
                continue;
            }

            int equals = name.IndexOf('=');
            string switchName = (equals >= 0 ? name[..equals] : name).ToLowerInvariant();
            foreach (string dangerous in DangerousSwitches)
            {
                if (switchName.StartsWith(dangerous, StringComparison.Ordinal))
                {
                    return token;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Finds a WebView2 policy that would redirect this executable to another runtime or
    /// profile, or start it with a dangerous switch. Policies cannot be overridden by the
    /// app, so the caller must refuse to start.
    /// </summary>
    public static string? FindHijackingPolicy(string executableName)
    {
        foreach ((RegistryKey hive, string hiveName) in new[]
                 {
                     (Registry.CurrentUser, "HKCU"),
                     (Registry.LocalMachine, "HKLM")
                 })
        {
            foreach (string policy in new[] { "BrowserExecutableFolder", "UserDataFolder", "AdditionalBrowserArguments" })
            {
                using RegistryKey? key = OpenPolicyKey(hive, PolicyRoot + policy);
                if (key is null)
                {
                    continue;
                }

                foreach (string valueName in key.GetValueNames())
                {
                    if (!AppliesTo(valueName, executableName) ||
                        key.GetValue(valueName) is not string value ||
                        string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    if (policy != "AdditionalBrowserArguments" || FindDangerousSwitch(value) is not null)
                    {
                        return $@"{hiveName}\{PolicyRoot}{policy} ({valueName} = {value})";
                    }
                }
            }
        }

        return null;
    }

    private static bool AppliesTo(string valueName, string executableName) =>
        valueName == "*" ||
        valueName.Equals(executableName, StringComparison.OrdinalIgnoreCase) ||
        // Packaged builds are matched by AUMID, e.g. "Aero_xxxx!App".
        valueName.Contains('!', StringComparison.Ordinal) &&
        valueName.StartsWith(Path.GetFileNameWithoutExtension(executableName), StringComparison.OrdinalIgnoreCase);

    private static RegistryKey? OpenPolicyKey(RegistryKey hive, string path)
    {
        try
        {
            return hive.OpenSubKey(path, writable: false);
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
