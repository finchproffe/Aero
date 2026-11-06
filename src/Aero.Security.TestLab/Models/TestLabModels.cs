using System.Text.Json.Serialization;

namespace Aero.Security.TestLab.Models;

internal enum FindingSeverity
{
    Info = 0,
    Warning = 1,
    High = 2,
    Critical = 3
}

internal sealed record ScanFinding(
    FindingSeverity Severity,
    string Component,
    string Rule,
    string Target,
    string Message,
    string? Encoding = null,
    string? Location = null);

internal sealed record ScanStatistics(
    int FilesVisited,
    long FileBytesRead,
    int ProcessesVisited,
    long ProcessBytesRead,
    int AccessDeniedCount,
    int ErrorCount);

internal sealed record TestLabReport(
    int SchemaVersion,
    string TestLabVersion,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    string MachineName,
    string WindowsUser,
    string MarkerFingerprint,
    int MarkerLength,
    string ScenarioLabel,
    bool DeepFileScan,
    long MaximumFileBytes,
    long MaximumProcessBytes,
    IReadOnlyList<string> ProfileRoots,
    IReadOnlyList<string> BackupPaths,
    IReadOnlyList<int> RequestedProcessIds,
    ScanStatistics Statistics,
    IReadOnlyList<ScanFinding> Findings,
    IReadOnlyList<string> Notes)
{
    [JsonIgnore]
    public int CriticalCount => Findings.Count(item => item.Severity == FindingSeverity.Critical);

    [JsonIgnore]
    public int HighCount => Findings.Count(item => item.Severity == FindingSeverity.High);
}
