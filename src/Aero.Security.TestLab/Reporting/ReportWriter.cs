using System.Globalization;
using System.Text;
using System.Text.Json;
using Aero.Security.TestLab.Models;

namespace Aero.Security.TestLab.Reporting;

internal static class ReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static (string JsonPath, string TextPath) Write(
        TestLabReport report,
        string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        string stamp = report.StartedAtUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        string jsonPath = Path.Combine(outputDirectory, $"Aero-TestLab-{stamp}.json");
        string textPath = Path.Combine(outputDirectory, $"Aero-TestLab-{stamp}.txt");

        File.WriteAllText(
            jsonPath,
            JsonSerializer.Serialize(report, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(
            textPath,
            BuildText(report),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return (jsonPath, textPath);
    }

    private static void AppendInvariant<T>(StringBuilder builder, string label, T value)
        where T : IFormattable
    {
        builder.Append(label)
            .Append(": ")
            .Append(value.ToString(null, CultureInfo.InvariantCulture))
            .AppendLine();
    }

    private static void AppendInvariant(StringBuilder builder, string label, string? value)
    {
        builder.Append(label)
            .Append(": ")
            .Append(value ?? string.Empty)
            .AppendLine();
    }

    private static string BuildText(TestLabReport report)
    {
        StringBuilder builder = new();
        builder.AppendLine("Aero.Security.TestLab report");
        AppendInvariant(builder, "Started", report.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        AppendInvariant(builder, "Finished", report.FinishedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        AppendInvariant(builder, "Marker fingerprint", report.MarkerFingerprint);
        AppendInvariant(builder, "Marker length", report.MarkerLength);
        AppendInvariant(builder, "Scenario", report.ScenarioLabel);
        builder.AppendLine();
        AppendInvariant(
            builder,
            "Critical findings",
            report.Findings.Count(item => item.Severity == FindingSeverity.Critical));
        AppendInvariant(
            builder,
            "High findings",
            report.Findings.Count(item => item.Severity == FindingSeverity.High));
        AppendInvariant(
            builder,
            "Warnings",
            report.Findings.Count(item => item.Severity == FindingSeverity.Warning));
        AppendInvariant(builder, "Files visited", report.Statistics.FilesVisited);
        AppendInvariant(builder, "File bytes read", report.Statistics.FileBytesRead);
        AppendInvariant(builder, "Processes visited", report.Statistics.ProcessesVisited);
        AppendInvariant(builder, "Process bytes read", report.Statistics.ProcessBytesRead);
        AppendInvariant(builder, "Access denied", report.Statistics.AccessDeniedCount);
        AppendInvariant(builder, "Errors", report.Statistics.ErrorCount);
        builder.AppendLine();

        foreach (ScanFinding finding in report.Findings
                     .OrderByDescending(item => item.Severity)
                     .ThenBy(item => item.Component, StringComparer.Ordinal)
                     .ThenBy(item => item.Target, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append('[').Append(finding.Severity.ToString().ToUpperInvariant()).Append("] ")
                .Append(finding.Component).Append(" / ").Append(finding.Rule).AppendLine();
            builder.Append("Target: ").AppendLine(finding.Target);
            builder.Append("Message: ").AppendLine(finding.Message);
            if (!string.IsNullOrWhiteSpace(finding.Encoding))
            {
                builder.Append("Encoding: ").AppendLine(finding.Encoding);
            }

            if (!string.IsNullOrWhiteSpace(finding.Location))
            {
                builder.Append("Location: ").AppendLine(finding.Location);
            }

            builder.AppendLine();
        }

        if (report.Notes.Count > 0)
        {
            builder.AppendLine("Notes:");
            foreach (string note in report.Notes)
            {
                builder.Append("- ").AppendLine(note);
            }
        }

        return builder.ToString();
    }
}
