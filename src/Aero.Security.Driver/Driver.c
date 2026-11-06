#include "AeroSecurityDriver.h"

#ifdef ALLOC_PRAGMA
#pragma alloc_text(INIT, DriverEntry)
#pragma alloc_text(PAGE, AeroSecurityEvtDriverUnload)
#endif

_Use_decl_annotations_
NTSTATUS
DriverEntry(
    PDRIVER_OBJECT DriverObject,
    PUNICODE_STRING RegistryPath
    )
{
    WDF_DRIVER_CONFIG config;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDFDRIVER driver;
    NTSTATUS status;

    KdPrintEx((DPFLTR_IHVDRIVER_ID,
               DPFLTR_INFO_LEVEL,
               "AeroSecurity: DriverEntry v%lu.%lu.%lu.%lu\n",
               AERO_SECURITY_DRIVER_VERSION_MAJOR,
               AERO_SECURITY_DRIVER_VERSION_MINOR,
               AERO_SECURITY_DRIVER_VERSION_PATCH,
               AERO_SECURITY_DRIVER_VERSION_BUILD));

    WDF_DRIVER_CONFIG_INIT(&config, WDF_NO_EVENT_CALLBACK);
    config.DriverInitFlags |= WdfDriverInitNonPnpDriver;
    config.EvtDriverUnload = AeroSecurityEvtDriverUnload;
    config.DriverPoolTag = 'rSeA';

    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);

    status = WdfDriverCreate(
        DriverObject,
        RegistryPath,
        &attributes,
        &config,
        &driver);

    if (!NT_SUCCESS(status)) {
        KdPrintEx((DPFLTR_IHVDRIVER_ID,
                   DPFLTR_ERROR_LEVEL,
                   "AeroSecurity: WdfDriverCreate failed: 0x%08X\n",
                   status));
        return status;
    }

    status = AeroSecurityInitializeProcessProtection();
    if (!NT_SUCCESS(status)) {
        KdPrintEx((DPFLTR_IHVDRIVER_ID,
                   DPFLTR_ERROR_LEVEL,
                   "AeroSecurity: protection callback initialization failed: 0x%08X\n",
                   status));
        return status;
    }

    status = AeroSecurityCreateControlDevice(driver);
    if (!NT_SUCCESS(status)) {
        AeroSecurityUninitializeProcessProtection();

        KdPrintEx((DPFLTR_IHVDRIVER_ID,
                   DPFLTR_ERROR_LEVEL,
                   "AeroSecurity: control device creation failed: 0x%08X\n",
                   status));
        return status;
    }

    KdPrintEx((DPFLTR_IHVDRIVER_ID,
               DPFLTR_INFO_LEVEL,
               "AeroSecurity: driver initialized successfully\n"));

    return STATUS_SUCCESS;
}

_Use_decl_annotations_
VOID
AeroSecurityEvtDriverUnload(
    WDFDRIVER Driver
    )
{
    UNREFERENCED_PARAMETER(Driver);
    PAGED_CODE();

    AeroSecurityUninitializeProcessProtection();

    KdPrintEx((DPFLTR_IHVDRIVER_ID,
               DPFLTR_INFO_LEVEL,
               "AeroSecurity: driver unloaded\n"));
}
