using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace Aero.CompactInstaller
{
    internal static class Program
    {
        private const string ResourcePrefix = "Aero.Compact.Payload.";

        private static readonly KeyValuePair<string, string>[] Payloads =
        {
            new KeyValuePair<string, string>("Aero.msix", "Aero.msix"),
            new KeyValuePair<string, string>("Aero.cer", "Aero.cer"),
            new KeyValuePair<string, string>("Aero.Source", "Aero-Source.zip"),
            new KeyValuePair<string, string>("Driver.sys", "Aero.Security.Driver.sys"),
            new KeyValuePair<string, string>("Driver.cer", "Aero.Security.Driver.Lab.cer"),
            new KeyValuePair<string, string>("Install.ps1", "Install-AeroCompact.ps1"),
            new KeyValuePair<string, string>("Driver.Install.ps1", "Install-AeroSecurityDriver.ps1"),
            new KeyValuePair<string, string>("Driver.Uninstall.ps1", "Uninstall-AeroSecurityDriver.ps1"),
            new KeyValuePair<string, string>("Driver.Query.ps1", "Query-AeroSecurityDriver.ps1")
        };

        [STAThread]
        private static int Main()
        {
            DialogResult consent = MessageBox.Show(
                "Install Aero Browser and unpack the compact driver/source payload?",
                "Aero Compact Installer",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1);
            if (consent != DialogResult.Yes)
            {
                return 0;
            }

            string workingDirectory = Path.Combine(
                Path.GetTempPath(),
                "AeroCompactInstaller",
                Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(workingDirectory);
                ExtractPayload(workingDirectory);

                string scriptPath = Path.Combine(
                    workingDirectory,
                    "Install-AeroCompact.ps1");
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.System),
                        "WindowsPowerShell",
                        "v1.0",
                        "powershell.exe"),
                    Arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass -File " +
                        QuoteArgument(scriptPath),
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = false
                };
                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        throw new InvalidOperationException(
                            "Windows PowerShell could not be started.");
                    }
                    process.WaitForExit();
                    if (process.ExitCode != 0)
                    {
                        throw new InvalidOperationException(
                            "Aero installation failed with exit code " + process.ExitCode + ".");
                    }
                }

                MessageBox.Show(
                    "Aero installation completed successfully.",
                    "Aero Compact Installer",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return 0;
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    exception.Message,
                    "Aero Compact Installer",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 1;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(workingDirectory))
                    {
                        Directory.Delete(workingDirectory, true);
                    }
                }
                catch
                {
                    // Windows will clear any remaining temporary files later.
                }
            }
        }

        private static void ExtractPayload(string workingDirectory)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            foreach (KeyValuePair<string, string> payload in Payloads)
            {
                string resourceName = ResourcePrefix + payload.Key;
                string destination = Path.Combine(workingDirectory, payload.Value);
                using (Stream source = assembly.GetManifestResourceStream(resourceName))
                {
                    if (source == null)
                    {
                        throw new InvalidDataException(
                            "Compact installer resource is missing: " + resourceName);
                    }
                    using (FileStream target = new FileStream(
                        destination,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        1024 * 128,
                        FileOptions.SequentialScan))
                    {
                        source.CopyTo(target, 1024 * 128);
                        target.Flush(true);
                    }
                }
            }
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
