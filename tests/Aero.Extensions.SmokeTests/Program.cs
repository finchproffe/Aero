using Aero.Extensions;
using System.Text.Json;

string root = Path.Combine(Path.GetTempPath(), "AeroExtensionsSmoke", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    string packageRoot = Path.Combine(root, "package");
    Directory.CreateDirectory(packageRoot);
    Directory.CreateDirectory(Path.Combine(packageRoot, "_locales", "en"));
    File.WriteAllText(
        Path.Combine(packageRoot, "manifest.json"),
        """
        {
          "manifest_version": 3,
          "name": "__MSG_title__",
          "description": "Test extension",
          "version": "1.2.3",
          "action": { "default_popup": "popup.html" },
          "permissions": ["storage", "tabs"],
          "host_permissions": ["https://example.com/*"],
          "icons": { "128": "icon.png" }
        }
        """);
    File.WriteAllText(
        Path.Combine(packageRoot, "_locales", "en", "messages.json"),
        JsonSerializer.Serialize(new { title = new { message = "Localized extension" } }));
    File.WriteAllBytes(Path.Combine(packageRoot, "icon.png"), [1, 2, 3]);

    ExtensionPackage package = ExtensionCatalog.ReadPackage(packageRoot, "test-id") ??
        throw new InvalidOperationException("Manifest package was not parsed.");
    Assert(package.Name == "Localized extension", "Localized name was not resolved.");
    Assert(package.ActionPage == "popup.html", "Action page was not parsed.");
    Assert(package.IconPath is not null, "Safe icon path was not selected.");
    Assert(
        ExtensionPermissionClassifier.Classify(package.Permissions) == ExtensionPermissionRisk.High,
        "Permission risk classification is incorrect.");

    string registryRoot = Path.Combine(root, "registry");
    ExtensionRegistry registry = ExtensionRegistry.Load(registryRoot);
    registry.SetPath("test-id", packageRoot);
    registry.SetPinned("test-id", true);

    ExtensionRegistry reloaded = ExtensionRegistry.Load(registryRoot);
    Assert(reloaded.GetPath("TEST-ID") == packageRoot, "Managed path was not persisted.");
    Assert(reloaded.IsPinned("TEST-ID"), "Pinned state was not persisted.");

    File.WriteAllText(Path.Combine(registryRoot, "extensions.json"), "{ broken");
    ExtensionRegistry recovered = ExtensionRegistry.Load(registryRoot);
    Assert(recovered.GetPath("test-id") == packageRoot, "Registry backup recovery failed.");

    Console.WriteLine("Aero.Extensions smoke tests passed.");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
