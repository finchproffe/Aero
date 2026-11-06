using Microsoft.Win32;
using System.Runtime.Versioning;

namespace Aero.Security.Driver.Setup;

[SupportedOSPlatform("windows")]
internal static class VmGuard
{
    private static readonly string[] VirtualMachineMarkers =
    [
        "VMWARE",
        "VIRTUALBOX",
        "VBOX",
        "QEMU",
        "KVM",
        "VIRTUAL MACHINE",
        "HYPER-V",
        "XEN",
        "PARALLELS"
    ];

    public static string RequireSupportedVirtualMachine()
    {
        using RegistryKey? bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        if (bios is null)
        {
            throw new InvalidOperationException("Cannot read VM firmware information from the registry.");
        }

        string fingerprint = string.Join(
            " | ",
            new[]
            {
                bios.GetValue("SystemManufacturer")?.ToString(),
                bios.GetValue("SystemProductName")?.ToString(),
                bios.GetValue("BIOSVendor")?.ToString(),
                bios.GetValue("BaseBoardManufacturer")?.ToString()
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

        string upper = fingerprint.ToUpperInvariant();
        if (!VirtualMachineMarkers.Any(upper.Contains))
        {
            throw new InvalidOperationException(
                "A supported virtual machine was not detected. This laboratory installer refuses to install the kernel driver on physical Windows.");
        }

        return fingerprint;
    }
}
