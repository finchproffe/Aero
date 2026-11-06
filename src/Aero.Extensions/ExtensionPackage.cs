namespace Aero.Extensions;

public sealed record ExtensionPackage(
    string Id,
    string Name,
    string Description,
    string Version,
    string FolderPath,
    string? ActionPage,
    string? OptionsPage,
    IReadOnlyList<string> Permissions,
    string? IconPath);
