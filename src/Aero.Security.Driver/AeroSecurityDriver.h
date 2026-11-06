#pragma once

#include <ntifs.h>
#include <wdf.h>

#include "AeroSecurityProtocol.h"
#include "ProcessProtection.h"

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_UNLOAD AeroSecurityEvtDriverUnload;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL AeroSecurityEvtIoDeviceControl;

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
AeroSecurityCreateControlDevice(
    _In_ WDFDRIVER Driver
    );
