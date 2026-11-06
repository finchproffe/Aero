#include "AeroSecurityDriver.h"
#include "ProcessProtection.h"

static PVOID gObRegistrationHandle = NULL;
static BOOLEAN gProcessNotifyRegistered = FALSE;
static KSPIN_LOCK gProtectionLock;
static PEPROCESS gProtectedProcess = NULL;
static ULONG gProtectedProcessId = 0;
static ULONG gProtectionGeneration = 0;
static volatile LONG64 gProcessHandleDowngrades = 0;
static volatile LONG64 gThreadHandleDowngrades = 0;

static
OB_PREOP_CALLBACK_STATUS
AeroSecurityObjectPreOperation(
    _In_ PVOID RegistrationContext,
    _Inout_ POB_PRE_OPERATION_INFORMATION OperationInformation
    );

static
VOID
AeroSecurityProcessNotify(
    _Inout_ PEPROCESS Process,
    _In_ HANDLE ProcessId,
    _Inout_opt_ PPS_CREATE_NOTIFY_INFO CreateInfo
    );

static
PACCESS_MASK
AeroSecurityGetDesiredAccessPointer(
    _Inout_ POB_PRE_OPERATION_INFORMATION OperationInformation
    );

#ifdef ALLOC_PRAGMA
#pragma alloc_text(PAGE, AeroSecurityInitializeProcessProtection)
#pragma alloc_text(PAGE, AeroSecurityUninitializeProcessProtection)
#pragma alloc_text(PAGE, AeroSecurityValidateProtectingProcess)
#pragma alloc_text(PAGE, AeroSecurityProtectProcess)
#pragma alloc_text(PAGE, AeroSecurityUnprotectProcess)
#endif

static
PACCESS_MASK
AeroSecurityGetDesiredAccessPointer(
    _Inout_ POB_PRE_OPERATION_INFORMATION OperationInformation
    )
{
    if (OperationInformation->Operation == OB_OPERATION_HANDLE_CREATE) {
        return &OperationInformation->Parameters->CreateHandleInformation.DesiredAccess;
    }

    if (OperationInformation->Operation == OB_OPERATION_HANDLE_DUPLICATE) {
        return &OperationInformation->Parameters->DuplicateHandleInformation.DesiredAccess;
    }

    return NULL;
}

_Use_decl_annotations_
static
OB_PREOP_CALLBACK_STATUS
AeroSecurityObjectPreOperation(
    PVOID RegistrationContext,
    POB_PRE_OPERATION_INFORMATION OperationInformation
    )
{
    KIRQL oldIrql;
    PEPROCESS protectedProcess;
    ULONG protectedProcessId;
    PACCESS_MASK desiredAccess;
    ACCESS_MASK blockedAccess;
    ACCESS_MASK originalAccess;
    BOOLEAN protectsTarget = FALSE;
    BOOLEAN isProcessObject;

    UNREFERENCED_PARAMETER(RegistrationContext);

    if (OperationInformation->KernelHandle != 0) {
        return OB_PREOP_SUCCESS;
    }

    desiredAccess = AeroSecurityGetDesiredAccessPointer(OperationInformation);
    if (desiredAccess == NULL) {
        return OB_PREOP_SUCCESS;
    }

    KeAcquireSpinLock(&gProtectionLock, &oldIrql);

    protectedProcess = gProtectedProcess;
    protectedProcessId = gProtectedProcessId;

    if (protectedProcess == NULL || protectedProcessId == 0) {
        KeReleaseSpinLock(&gProtectionLock, oldIrql);
        return OB_PREOP_SUCCESS;
    }

    if (PsGetCurrentProcess() == protectedProcess) {
        KeReleaseSpinLock(&gProtectionLock, oldIrql);
        return OB_PREOP_SUCCESS;
    }

    isProcessObject = (OperationInformation->ObjectType == *PsProcessType);

    if (isProcessObject) {
        protectsTarget = (OperationInformation->Object == protectedProcess);
        blockedAccess = AERO_SECURITY_BLOCKED_PROCESS_ACCESS;
    }
    else if (OperationInformation->ObjectType == *PsThreadType) {
        HANDLE threadProcessId;

        threadProcessId = PsGetThreadProcessId(
            (PETHREAD)OperationInformation->Object);
        protectsTarget =
            ((ULONG)(ULONG_PTR)threadProcessId == protectedProcessId);
        blockedAccess = AERO_SECURITY_BLOCKED_THREAD_ACCESS;
    }
    else {
        blockedAccess = 0;
    }

    if (!protectsTarget || blockedAccess == 0) {
        KeReleaseSpinLock(&gProtectionLock, oldIrql);
        return OB_PREOP_SUCCESS;
    }

    originalAccess = *desiredAccess;
    *desiredAccess &= ~blockedAccess;

    if (*desiredAccess != originalAccess) {
        if (isProcessObject) {
            InterlockedIncrement64(&gProcessHandleDowngrades);
        }
        else {
            InterlockedIncrement64(&gThreadHandleDowngrades);
        }
    }

    KeReleaseSpinLock(&gProtectionLock, oldIrql);
    return OB_PREOP_SUCCESS;
}

_Use_decl_annotations_
static
VOID
AeroSecurityProcessNotify(
    PEPROCESS Process,
    HANDLE ProcessId,
    PPS_CREATE_NOTIFY_INFO CreateInfo
    )
{
    KIRQL oldIrql;
    PEPROCESS releasedProcess = NULL;

    if (CreateInfo != NULL) {
        return;
    }

    KeAcquireSpinLock(&gProtectionLock, &oldIrql);

    if (gProtectedProcess == Process &&
        gProtectedProcessId == (ULONG)(ULONG_PTR)ProcessId) {
        releasedProcess = gProtectedProcess;
        gProtectedProcess = NULL;
        gProtectedProcessId = 0;
        gProtectionGeneration += 1;
    }

    KeReleaseSpinLock(&gProtectionLock, oldIrql);

    if (releasedProcess != NULL) {
        ObDereferenceObject(releasedProcess);
    }
}


static
BOOLEAN
AeroSecurityImageFileNameEquals(
    _In_ PCUNICODE_STRING ImageName,
    _In_ PCWSTR ExpectedFileName
    )
{
    UNICODE_STRING expected;
    UNICODE_STRING actual;
    USHORT characterCount;
    USHORT fileNameStart;
    USHORT index;

    if (ImageName == NULL ||
        ImageName->Buffer == NULL ||
        ImageName->Length == 0 ||
        ExpectedFileName == NULL) {
        return FALSE;
    }

    characterCount = ImageName->Length / sizeof(WCHAR);
    fileNameStart = 0;

    for (index = 0; index < characterCount; index += 1) {
        if (ImageName->Buffer[index] == L'\\' ||
            ImageName->Buffer[index] == L'/') {
            fileNameStart = index + 1;
        }
    }

    actual.Buffer = ImageName->Buffer + fileNameStart;
    actual.Length = (USHORT)((characterCount - fileNameStart) * sizeof(WCHAR));
    actual.MaximumLength = actual.Length;

    RtlInitUnicodeString(&expected, ExpectedFileName);
    return RtlEqualUnicodeString(&actual, &expected, TRUE);
}

_Use_decl_annotations_
NTSTATUS
AeroSecurityValidateProtectingProcess(
    ULONG ProcessId
    )
{
    PEPROCESS process = NULL;
    PUNICODE_STRING imageName = NULL;
    NTSTATUS status;
    BOOLEAN accepted = FALSE;

    PAGED_CODE();

    if (ProcessId == 0) {
        return STATUS_INVALID_PARAMETER;
    }

    status = PsLookupProcessByProcessId(
        ULongToHandle(ProcessId),
        &process);

    if (!NT_SUCCESS(status)) {
        return status;
    }

    if (PsGetProcessExitStatus(process) != STATUS_PENDING) {
        status = STATUS_PROCESS_IS_TERMINATING;
        goto Exit;
    }

    status = SeLocateProcessImageName(process, &imageName);
    if (!NT_SUCCESS(status)) {
        goto Exit;
    }

    accepted = AeroSecurityImageFileNameEquals(
        imageName,
        L"Aero.Security.Broker.exe");

#if defined(AERO_SECURITY_DEBUG_BUILD)
    /*
     * Preserve the explicit VM-only lab tests that protect PowerShell or the
     * test client. Release builds accept only the real Broker executable.
     */
    if (!accepted) {
        accepted =
            AeroSecurityImageFileNameEquals(imageName, L"powershell.exe") ||
            AeroSecurityImageFileNameEquals(imageName, L"pwsh.exe") ||
            AeroSecurityImageFileNameEquals(
                imageName,
                L"Aero.Security.Driver.TestClient.exe");
    }
#endif

    status = accepted ? STATUS_SUCCESS : STATUS_ACCESS_DENIED;

Exit:
    if (imageName != NULL) {
        ExFreePool(imageName);
    }

    if (process != NULL) {
        ObDereferenceObject(process);
    }

    return status;
}

_Use_decl_annotations_
NTSTATUS
AeroSecurityInitializeProcessProtection(
    VOID
    )
{
    OB_OPERATION_REGISTRATION operationRegistrations[2];
    OB_CALLBACK_REGISTRATION callbackRegistration;
    UNICODE_STRING altitude;
    NTSTATUS status;

    PAGED_CODE();

    if (gObRegistrationHandle != NULL) {
        return STATUS_SUCCESS;
    }

    KeInitializeSpinLock(&gProtectionLock);

    status = PsSetCreateProcessNotifyRoutineEx(
        AeroSecurityProcessNotify,
        FALSE);

    if (!NT_SUCCESS(status)) {
        return status;
    }

    gProcessNotifyRegistered = TRUE;

    RtlZeroMemory(operationRegistrations, sizeof(operationRegistrations));
    RtlZeroMemory(&callbackRegistration, sizeof(callbackRegistration));
    RtlInitUnicodeString(&altitude, L"385200.20260805");

    operationRegistrations[0].ObjectType = PsProcessType;
    operationRegistrations[0].Operations =
        OB_OPERATION_HANDLE_CREATE |
        OB_OPERATION_HANDLE_DUPLICATE;
    operationRegistrations[0].PreOperation = AeroSecurityObjectPreOperation;
    operationRegistrations[0].PostOperation = NULL;

    operationRegistrations[1].ObjectType = PsThreadType;
    operationRegistrations[1].Operations =
        OB_OPERATION_HANDLE_CREATE |
        OB_OPERATION_HANDLE_DUPLICATE;
    operationRegistrations[1].PreOperation = AeroSecurityObjectPreOperation;
    operationRegistrations[1].PostOperation = NULL;

    callbackRegistration.Version = OB_FLT_REGISTRATION_VERSION;
    callbackRegistration.OperationRegistrationCount =
        RTL_NUMBER_OF(operationRegistrations);
    callbackRegistration.Altitude = altitude;
    callbackRegistration.RegistrationContext = NULL;
    callbackRegistration.OperationRegistration = operationRegistrations;

    status = ObRegisterCallbacks(
        &callbackRegistration,
        &gObRegistrationHandle);

    if (!NT_SUCCESS(status)) {
        gObRegistrationHandle = NULL;
        (VOID)PsSetCreateProcessNotifyRoutineEx(
            AeroSecurityProcessNotify,
            TRUE);
        gProcessNotifyRegistered = FALSE;
    }

    return status;
}

_Use_decl_annotations_
VOID
AeroSecurityUninitializeProcessProtection(
    VOID
    )
{
    KIRQL oldIrql;
    PVOID registrationHandle;
    PEPROCESS protectedProcess;

    PAGED_CODE();

    registrationHandle = gObRegistrationHandle;
    gObRegistrationHandle = NULL;

    if (registrationHandle != NULL) {
        ObUnRegisterCallbacks(registrationHandle);
    }

    if (gProcessNotifyRegistered) {
        (VOID)PsSetCreateProcessNotifyRoutineEx(
            AeroSecurityProcessNotify,
            TRUE);
        gProcessNotifyRegistered = FALSE;
    }

    KeAcquireSpinLock(&gProtectionLock, &oldIrql);
    protectedProcess = gProtectedProcess;
    gProtectedProcess = NULL;
    gProtectedProcessId = 0;
    gProtectionGeneration += 1;
    KeReleaseSpinLock(&gProtectionLock, oldIrql);

    if (protectedProcess != NULL) {
        ObDereferenceObject(protectedProcess);
    }
}

_Use_decl_annotations_
NTSTATUS
AeroSecurityProtectProcess(
    ULONG ProcessId
    )
{
    KIRQL oldIrql;
    PEPROCESS process = NULL;
    NTSTATUS status;

    PAGED_CODE();

    if (ProcessId == 0) {
        return STATUS_INVALID_PARAMETER;
    }

    status = PsLookupProcessByProcessId(
        ULongToHandle(ProcessId),
        &process);

    if (!NT_SUCCESS(status)) {
        return status;
    }

    if (PsGetProcessExitStatus(process) != STATUS_PENDING) {
        ObDereferenceObject(process);
        return STATUS_PROCESS_IS_TERMINATING;
    }

    KeAcquireSpinLock(&gProtectionLock, &oldIrql);

    if (gProtectedProcess != NULL) {
        if (gProtectedProcess == process &&
            gProtectedProcessId == ProcessId) {
            KeReleaseSpinLock(&gProtectionLock, oldIrql);
            ObDereferenceObject(process);
            return STATUS_SUCCESS;
        }

        KeReleaseSpinLock(&gProtectionLock, oldIrql);
        ObDereferenceObject(process);
        return STATUS_DEVICE_BUSY;
    }

    gProtectedProcess = process;
    gProtectedProcessId = ProcessId;
    gProtectionGeneration += 1;
    InterlockedExchange64(&gProcessHandleDowngrades, 0);
    InterlockedExchange64(&gThreadHandleDowngrades, 0);

    KeReleaseSpinLock(&gProtectionLock, oldIrql);
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS
AeroSecurityUnprotectProcess(
    ULONG ProcessId
    )
{
    KIRQL oldIrql;
    PEPROCESS protectedProcess;

    PAGED_CODE();

    KeAcquireSpinLock(&gProtectionLock, &oldIrql);

    if (gProtectedProcess == NULL || gProtectedProcessId == 0) {
        KeReleaseSpinLock(&gProtectionLock, oldIrql);
        return STATUS_NOT_FOUND;
    }

    if (gProtectedProcessId != ProcessId) {
        KeReleaseSpinLock(&gProtectionLock, oldIrql);
        return STATUS_ACCESS_DENIED;
    }

    protectedProcess = gProtectedProcess;
    gProtectedProcess = NULL;
    gProtectedProcessId = 0;
    gProtectionGeneration += 1;

    KeReleaseSpinLock(&gProtectionLock, oldIrql);

    ObDereferenceObject(protectedProcess);
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
VOID
AeroSecurityGetProcessProtectionSnapshot(
    PAERO_SECURITY_PROTECTION_SNAPSHOT Snapshot
    )
{
    KIRQL oldIrql;

    RtlZeroMemory(Snapshot, sizeof(*Snapshot));

    KeAcquireSpinLock(&gProtectionLock, &oldIrql);
    Snapshot->ProcessId = gProtectedProcessId;
    Snapshot->Active =
        (gProtectedProcess != NULL && gProtectedProcessId != 0) ? 1UL : 0UL;
    Snapshot->Generation = gProtectionGeneration;
    Snapshot->BlockedProcessAccessMask =
        AERO_SECURITY_BLOCKED_PROCESS_ACCESS;
    Snapshot->BlockedThreadAccessMask =
        AERO_SECURITY_BLOCKED_THREAD_ACCESS;
    KeReleaseSpinLock(&gProtectionLock, oldIrql);

    Snapshot->ProcessHandleDowngrades =
        (ULONGLONG)InterlockedCompareExchange64(
            &gProcessHandleDowngrades,
            0,
            0);
    Snapshot->ThreadHandleDowngrades =
        (ULONGLONG)InterlockedCompareExchange64(
            &gThreadHandleDowngrades,
            0,
            0);
}
