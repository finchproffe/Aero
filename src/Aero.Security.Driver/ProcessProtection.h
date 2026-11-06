#pragma once

#include <ntifs.h>

/*
 * Process/thread access masks are Win32 object rights, but current WDK
 * kernel-mode headers do not consistently expose the PROCESS_* and THREAD_*
 * symbolic macros to every driver project configuration. Keep local,
 * namespaced constants with the documented Windows values instead of
 * depending on user-mode header visibility.
 */
#define AERO_PROCESS_TERMINATE                ((ACCESS_MASK)0x0001UL)
#define AERO_PROCESS_CREATE_THREAD            ((ACCESS_MASK)0x0002UL)
#define AERO_PROCESS_VM_OPERATION             ((ACCESS_MASK)0x0008UL)
#define AERO_PROCESS_VM_READ                  ((ACCESS_MASK)0x0010UL)
#define AERO_PROCESS_VM_WRITE                 ((ACCESS_MASK)0x0020UL)
#define AERO_PROCESS_DUP_HANDLE               ((ACCESS_MASK)0x0040UL)
#define AERO_PROCESS_CREATE_PROCESS           ((ACCESS_MASK)0x0080UL)
#define AERO_PROCESS_SET_QUOTA                ((ACCESS_MASK)0x0100UL)
#define AERO_PROCESS_SET_INFORMATION          ((ACCESS_MASK)0x0200UL)
#define AERO_PROCESS_SUSPEND_RESUME           ((ACCESS_MASK)0x0800UL)
#define AERO_PROCESS_SET_LIMITED_INFORMATION  ((ACCESS_MASK)0x2000UL)

#define AERO_THREAD_TERMINATE                 ((ACCESS_MASK)0x0001UL)
#define AERO_THREAD_SUSPEND_RESUME            ((ACCESS_MASK)0x0002UL)
#define AERO_THREAD_GET_CONTEXT               ((ACCESS_MASK)0x0008UL)
#define AERO_THREAD_SET_CONTEXT               ((ACCESS_MASK)0x0010UL)
#define AERO_THREAD_SET_INFORMATION           ((ACCESS_MASK)0x0020UL)
#define AERO_THREAD_SET_THREAD_TOKEN          ((ACCESS_MASK)0x0080UL)
#define AERO_THREAD_IMPERSONATE               ((ACCESS_MASK)0x0100UL)
#define AERO_THREAD_DIRECT_IMPERSONATION      ((ACCESS_MASK)0x0200UL)
#define AERO_THREAD_SET_LIMITED_INFORMATION   ((ACCESS_MASK)0x0400UL)

#define AERO_SECURITY_BLOCKED_PROCESS_ACCESS (       \
    AERO_PROCESS_TERMINATE |                         \
    AERO_PROCESS_CREATE_THREAD |                     \
    AERO_PROCESS_CREATE_PROCESS |                    \
    AERO_PROCESS_VM_OPERATION |                      \
    AERO_PROCESS_VM_READ |                           \
    AERO_PROCESS_VM_WRITE |                          \
    AERO_PROCESS_DUP_HANDLE |                        \
    AERO_PROCESS_SET_QUOTA |                         \
    AERO_PROCESS_SET_INFORMATION |                   \
    AERO_PROCESS_SUSPEND_RESUME |                    \
    AERO_PROCESS_SET_LIMITED_INFORMATION)

#define AERO_SECURITY_BLOCKED_THREAD_ACCESS (        \
    AERO_THREAD_TERMINATE |                          \
    AERO_THREAD_SUSPEND_RESUME |                     \
    AERO_THREAD_GET_CONTEXT |                        \
    AERO_THREAD_SET_CONTEXT |                        \
    AERO_THREAD_SET_INFORMATION |                    \
    AERO_THREAD_SET_THREAD_TOKEN |                   \
    AERO_THREAD_IMPERSONATE |                        \
    AERO_THREAD_DIRECT_IMPERSONATION |               \
    AERO_THREAD_SET_LIMITED_INFORMATION)

typedef struct _AERO_SECURITY_PROTECTION_SNAPSHOT {
    ULONG ProcessId;
    ULONG Active;
    ULONG Generation;
    ACCESS_MASK BlockedProcessAccessMask;
    ACCESS_MASK BlockedThreadAccessMask;
    ULONGLONG ProcessHandleDowngrades;
    ULONGLONG ThreadHandleDowngrades;
} AERO_SECURITY_PROTECTION_SNAPSHOT,
  *PAERO_SECURITY_PROTECTION_SNAPSHOT;

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
AeroSecurityInitializeProcessProtection(
    VOID
    );

_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
AeroSecurityUninitializeProcessProtection(
    VOID
    );



_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
AeroSecurityValidateProtectingProcess(
    _In_ ULONG ProcessId
    );

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
AeroSecurityProtectProcess(
    _In_ ULONG ProcessId
    );

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
AeroSecurityUnprotectProcess(
    _In_ ULONG ProcessId
    );

_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
AeroSecurityGetProcessProtectionSnapshot(
    _Out_ PAERO_SECURITY_PROTECTION_SNAPSHOT Snapshot
    );
