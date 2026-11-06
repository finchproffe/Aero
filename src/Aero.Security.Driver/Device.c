#include "AeroSecurityDriver.h"

#ifdef ALLOC_PRAGMA
#pragma alloc_text(PAGE, AeroSecurityCreateControlDevice)
#endif

static
VOID
AeroSecurityFillProtectionStatus(
    _Out_ PAERO_SECURITY_PROCESS_PROTECTION_STATUS Output
    )
{
    AERO_SECURITY_PROTECTION_SNAPSHOT snapshot;

    AeroSecurityGetProcessProtectionSnapshot(&snapshot);

    RtlZeroMemory(Output, sizeof(*Output));
    Output->Magic = AERO_SECURITY_MAGIC;
    Output->ProtocolVersion = AERO_SECURITY_PROTOCOL_VERSION;
    Output->ProcessId = snapshot.ProcessId;
    Output->Active = snapshot.Active;
    Output->BlockedAccessMask = snapshot.BlockedProcessAccessMask;
}

static
VOID
AeroSecurityFillProtectionStatusV2(
    _Out_ PAERO_SECURITY_PROCESS_PROTECTION_STATUS_V2 Output
    )
{
    AERO_SECURITY_PROTECTION_SNAPSHOT snapshot;

    AeroSecurityGetProcessProtectionSnapshot(&snapshot);

    RtlZeroMemory(Output, sizeof(*Output));
    Output->Magic = AERO_SECURITY_MAGIC;
    Output->ProtocolVersion = AERO_SECURITY_PROTOCOL_VERSION;
    Output->ProcessId = snapshot.ProcessId;
    Output->Active = snapshot.Active;
    Output->Generation = snapshot.Generation;
    Output->BlockedProcessAccessMask = snapshot.BlockedProcessAccessMask;
    Output->BlockedThreadAccessMask = snapshot.BlockedThreadAccessMask;
    Output->ProcessHandleDowngrades = snapshot.ProcessHandleDowngrades;
    Output->ThreadHandleDowngrades = snapshot.ThreadHandleDowngrades;
}

_Use_decl_annotations_
NTSTATUS
AeroSecurityCreateControlDevice(
    WDFDRIVER Driver
    )
{
    DECLARE_CONST_UNICODE_STRING(deviceName, AERO_SECURITY_DEVICE_NT_NAME);
    DECLARE_CONST_UNICODE_STRING(symbolicLinkName, AERO_SECURITY_DEVICE_DOS_NAME);
    /*
     * The on-demand Broker runs in the interactive user's session. Access is
     * limited again in the PROTECT IOCTL by validating the requestor PID and
     * executable image before any process can occupy the protected slot.
     */
    DECLARE_CONST_UNICODE_STRING(
        securityDescriptor,
        L"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;IU)");

    PWDFDEVICE_INIT deviceInit = NULL;
    WDF_OBJECT_ATTRIBUTES deviceAttributes;
    WDF_IO_QUEUE_CONFIG queueConfig;
    WDF_OBJECT_ATTRIBUTES queueAttributes;
    WDFDEVICE device = NULL;
    NTSTATUS status;

    PAGED_CODE();

    deviceInit = WdfControlDeviceInitAllocate(Driver, &securityDescriptor);
    if (deviceInit == NULL) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    WdfDeviceInitSetExclusive(deviceInit, FALSE);
    WdfDeviceInitSetIoType(deviceInit, WdfDeviceIoBuffered);

    status = WdfDeviceInitAssignName(deviceInit, &deviceName);
    if (!NT_SUCCESS(status)) {
        goto Exit;
    }

    WDF_OBJECT_ATTRIBUTES_INIT(&deviceAttributes);
    deviceAttributes.ExecutionLevel = WdfExecutionLevelPassive;
    deviceAttributes.SynchronizationScope = WdfSynchronizationScopeNone;

    status = WdfDeviceCreate(&deviceInit, &deviceAttributes, &device);
    if (!NT_SUCCESS(status)) {
        goto Exit;
    }

    status = WdfDeviceCreateSymbolicLink(device, &symbolicLinkName);
    if (!NT_SUCCESS(status)) {
        goto Exit;
    }

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(
        &queueConfig,
        WdfIoQueueDispatchSequential);

    queueConfig.PowerManaged = WdfFalse;
    queueConfig.EvtIoDeviceControl = AeroSecurityEvtIoDeviceControl;

    WDF_OBJECT_ATTRIBUTES_INIT(&queueAttributes);
    queueAttributes.ExecutionLevel = WdfExecutionLevelPassive;
    queueAttributes.SynchronizationScope = WdfSynchronizationScopeQueue;

    status = WdfIoQueueCreate(
        device,
        &queueConfig,
        &queueAttributes,
        WDF_NO_HANDLE);

    if (!NT_SUCCESS(status)) {
        goto Exit;
    }

    WdfControlFinishInitializing(device);
    return STATUS_SUCCESS;

Exit:
    if (deviceInit != NULL) {
        WdfDeviceInitFree(deviceInit);
    }

    if (device != NULL) {
        WdfObjectDelete(device);
    }

    return status;
}

_Use_decl_annotations_
VOID
AeroSecurityEvtIoDeviceControl(
    WDFQUEUE Queue,
    WDFREQUEST Request,
    size_t OutputBufferLength,
    size_t InputBufferLength,
    ULONG IoControlCode
    )
{
    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;
    ULONG_PTR information = 0;

    UNREFERENCED_PARAMETER(Queue);

    switch (IoControlCode) {
    case IOCTL_AERO_SECURITY_PING:
    {
        PAERO_SECURITY_PING_REQUEST input = NULL;
        PAERO_SECURITY_PING_RESPONSE output = NULL;
        ULONGLONG requestNonce = 0;

        if (InputBufferLength < sizeof(*input) ||
            OutputBufferLength < sizeof(*output)) {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }

        status = WdfRequestRetrieveInputBuffer(
            Request,
            sizeof(*input),
            (PVOID*)&input,
            NULL);

        if (!NT_SUCCESS(status)) {
            break;
        }

        status = WdfRequestRetrieveOutputBuffer(
            Request,
            sizeof(*output),
            (PVOID*)&output,
            NULL);

        if (!NT_SUCCESS(status)) {
            break;
        }

        if (input->Magic != AERO_SECURITY_MAGIC ||
            input->ProtocolVersion != AERO_SECURITY_PROTOCOL_VERSION) {
            status = STATUS_REVISION_MISMATCH;
            break;
        }

        /*
         * METHOD_BUFFERED can expose the same system buffer as both input and
         * output. Preserve the caller nonce before clearing the response so a
         * rebuilt driver can never return a zeroed/stale nonce.
         */
        requestNonce = input->Nonce;

        RtlZeroMemory(output, sizeof(*output));
        output->Magic = AERO_SECURITY_MAGIC;
        output->ProtocolVersion = AERO_SECURITY_PROTOCOL_VERSION;
        output->Nonce = requestNonce;
        output->DriverVersionBuild = AERO_SECURITY_DRIVER_VERSION_BUILD;
        output->Capabilities = AERO_SECURITY_CAPABILITIES;

        information = sizeof(*output);
        status = STATUS_SUCCESS;
        break;
    }

    case IOCTL_AERO_SECURITY_GET_VERSION:
    {
        PAERO_SECURITY_VERSION_RESPONSE output = NULL;

        UNREFERENCED_PARAMETER(InputBufferLength);

        if (OutputBufferLength < sizeof(*output)) {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }

        status = WdfRequestRetrieveOutputBuffer(
            Request,
            sizeof(*output),
            (PVOID*)&output,
            NULL);

        if (!NT_SUCCESS(status)) {
            break;
        }

        RtlZeroMemory(output, sizeof(*output));
        output->Magic = AERO_SECURITY_MAGIC;
        output->ProtocolVersion = AERO_SECURITY_PROTOCOL_VERSION;
        output->Major = AERO_SECURITY_DRIVER_VERSION_MAJOR;
        output->Minor = AERO_SECURITY_DRIVER_VERSION_MINOR;
        output->Patch = AERO_SECURITY_DRIVER_VERSION_PATCH;
        output->Build = AERO_SECURITY_DRIVER_VERSION_BUILD;
        output->Capabilities = AERO_SECURITY_CAPABILITIES;
#if defined(AERO_SECURITY_DEBUG_BUILD)
        output->IsDebugBuild = 1;
#else
        output->IsDebugBuild = 0;
#endif

        information = sizeof(*output);
        status = STATUS_SUCCESS;
        break;
    }

    case IOCTL_AERO_SECURITY_PROTECT_CURRENT_PROCESS:
    {
        PAERO_SECURITY_PROCESS_PROTECTION_REQUEST input = NULL;
        PAERO_SECURITY_PROCESS_PROTECTION_STATUS output = NULL;
        ULONGLONG requestedProcessId;
        ULONG requestorProcessId;

        if (InputBufferLength < sizeof(*input) ||
            OutputBufferLength < sizeof(*output)) {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }

        status = WdfRequestRetrieveInputBuffer(
            Request,
            sizeof(*input),
            (PVOID*)&input,
            NULL);

        if (!NT_SUCCESS(status)) {
            break;
        }

        status = WdfRequestRetrieveOutputBuffer(
            Request,
            sizeof(*output),
            (PVOID*)&output,
            NULL);

        if (!NT_SUCCESS(status)) {
            break;
        }

        if (input->Magic != AERO_SECURITY_MAGIC ||
            input->ProtocolVersion != AERO_SECURITY_PROTOCOL_VERSION) {
            status = STATUS_REVISION_MISMATCH;
            break;
        }

        requestedProcessId = input->ProcessId;
        requestorProcessId = WdfRequestGetRequestorProcessId(Request);

        if (requestedProcessId == 0 ||
            requestedProcessId > MAXULONG ||
            requestedProcessId != requestorProcessId) {
            status = STATUS_ACCESS_DENIED;
            break;
        }

        status = AeroSecurityValidateProtectingProcess(
            (ULONG)requestedProcessId);
        if (!NT_SUCCESS(status)) {
            break;
        }

        status = AeroSecurityProtectProcess((ULONG)requestedProcessId);
        if (!NT_SUCCESS(status)) {
            break;
        }

        AeroSecurityFillProtectionStatus(output);
        information = sizeof(*output);
        break;
    }

    case IOCTL_AERO_SECURITY_UNPROTECT_CURRENT_PROCESS:
    {
        PAERO_SECURITY_PROCESS_PROTECTION_REQUEST input = NULL;
        PAERO_SECURITY_PROCESS_PROTECTION_STATUS output = NULL;
        ULONGLONG requestedProcessId;
        ULONG requestorProcessId;

        if (InputBufferLength < sizeof(*input) ||
            OutputBufferLength < sizeof(*output)) {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }

        status = WdfRequestRetrieveInputBuffer(
            Request,
            sizeof(*input),
            (PVOID*)&input,
            NULL);

        if (!NT_SUCCESS(status)) {
            break;
        }

        status = WdfRequestRetrieveOutputBuffer(
            Request,
            sizeof(*output),
            (PVOID*)&output,
            NULL);

        if (!NT_SUCCESS(status)) {
            break;
        }

        if (input->Magic != AERO_SECURITY_MAGIC ||
            input->ProtocolVersion != AERO_SECURITY_PROTOCOL_VERSION) {
            status = STATUS_REVISION_MISMATCH;
            break;
        }

        requestedProcessId = input->ProcessId;
        requestorProcessId = WdfRequestGetRequestorProcessId(Request);

        if (requestedProcessId == 0 ||
            requestedProcessId > MAXULONG ||
            requestedProcessId != requestorProcessId) {
            status = STATUS_ACCESS_DENIED;
            break;
        }

        status = AeroSecurityUnprotectProcess((ULONG)requestedProcessId);
        if (!NT_SUCCESS(status)) {
            break;
        }

        AeroSecurityFillProtectionStatus(output);
        information = sizeof(*output);
        break;
    }

    case IOCTL_AERO_SECURITY_GET_PROTECTION_STATUS:
    {
        PAERO_SECURITY_PROCESS_PROTECTION_STATUS output = NULL;

        UNREFERENCED_PARAMETER(InputBufferLength);

        if (OutputBufferLength < sizeof(*output)) {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }

        status = WdfRequestRetrieveOutputBuffer(
            Request,
            sizeof(*output),
            (PVOID*)&output,
            NULL);

        if (!NT_SUCCESS(status)) {
            break;
        }

        AeroSecurityFillProtectionStatus(output);
        information = sizeof(*output);
        status = STATUS_SUCCESS;
        break;
    }

    case IOCTL_AERO_SECURITY_GET_PROTECTION_STATUS_V2:
    {
        PAERO_SECURITY_PROCESS_PROTECTION_STATUS_V2 output = NULL;

        UNREFERENCED_PARAMETER(InputBufferLength);

        if (OutputBufferLength < sizeof(*output)) {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }

        status = WdfRequestRetrieveOutputBuffer(
            Request,
            sizeof(*output),
            (PVOID*)&output,
            NULL);

        if (!NT_SUCCESS(status)) {
            break;
        }

        AeroSecurityFillProtectionStatusV2(output);
        information = sizeof(*output);
        status = STATUS_SUCCESS;
        break;
    }

    default:
        status = STATUS_INVALID_DEVICE_REQUEST;
        break;
    }

    WdfRequestCompleteWithInformation(Request, status, information);
}
