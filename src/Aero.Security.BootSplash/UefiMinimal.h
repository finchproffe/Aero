#pragma once

#if defined(_MSC_VER)
#define EFIAPI __cdecl
#define NORETURN __declspec(noreturn)
#else
#define EFIAPI
#define NORETURN __attribute__((noreturn))
#endif

typedef unsigned char UINT8;
typedef unsigned short UINT16;
typedef unsigned int UINT32;
typedef signed int INT32;
typedef unsigned long long UINT64;
typedef unsigned long long UINTN;
typedef UINT8 BOOLEAN;
typedef UINT64 EFI_STATUS;
typedef void VOID;
typedef VOID* EFI_HANDLE;
typedef wchar_t CHAR16;

typedef struct _EFI_SYSTEM_TABLE EFI_SYSTEM_TABLE;
typedef struct _EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL;
typedef struct _EFI_DEVICE_PATH_PROTOCOL EFI_DEVICE_PATH_PROTOCOL;

#define EFI_SUCCESS 0ULL
#define EFI_ERROR_BIT 0x8000000000000000ULL
#define EFI_ERROR(Status) (((Status) & EFI_ERROR_BIT) != 0)
#define EFI_NOT_FOUND (EFI_ERROR_BIT | 14ULL)
#define EFI_INVALID_PARAMETER (EFI_ERROR_BIT | 2ULL)

#define FALSE 0
#define TRUE 1

#define EFI_BLACK 0x00
#define EFI_BLUE 0x01
#define EFI_GREEN 0x02
#define EFI_CYAN 0x03
#define EFI_RED 0x04
#define EFI_MAGENTA 0x05
#define EFI_BROWN 0x06
#define EFI_LIGHTGRAY 0x07
#define EFI_DARKGRAY 0x08
#define EFI_LIGHTBLUE 0x09
#define EFI_LIGHTGREEN 0x0A
#define EFI_LIGHTCYAN 0x0B
#define EFI_LIGHTRED 0x0C
#define EFI_WHITE 0x0F
#define EFI_BACKGROUND_BLACK 0x00

#define MEDIA_DEVICE_PATH 0x04
#define MEDIA_FILEPATH_DP 0x04
#define END_DEVICE_PATH_TYPE 0x7F
#define END_ENTIRE_DEVICE_PATH_SUBTYPE 0xFF

typedef struct _EFI_GUID {
    UINT32 Data1;
    UINT16 Data2;
    UINT16 Data3;
    UINT8 Data4[8];
} EFI_GUID;

typedef struct _EFI_TABLE_HEADER {
    UINT64 Signature;
    UINT32 Revision;
    UINT32 HeaderSize;
    UINT32 CRC32;
    UINT32 Reserved;
} EFI_TABLE_HEADER;

typedef struct _SIMPLE_TEXT_OUTPUT_MODE {
    INT32 MaxMode;
    INT32 Mode;
    INT32 Attribute;
    INT32 CursorColumn;
    INT32 CursorRow;
    BOOLEAN CursorVisible;
} SIMPLE_TEXT_OUTPUT_MODE;

typedef EFI_STATUS (EFIAPI *EFI_TEXT_RESET)(
    EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL* This,
    BOOLEAN ExtendedVerification);

typedef EFI_STATUS (EFIAPI *EFI_TEXT_STRING)(
    EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL* This,
    CHAR16* String);

typedef EFI_STATUS (EFIAPI *EFI_TEXT_TEST_STRING)(
    EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL* This,
    CHAR16* String);

typedef EFI_STATUS (EFIAPI *EFI_TEXT_QUERY_MODE)(
    EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL* This,
    UINTN ModeNumber,
    UINTN* Columns,
    UINTN* Rows);

typedef EFI_STATUS (EFIAPI *EFI_TEXT_SET_MODE)(
    EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL* This,
    UINTN ModeNumber);

typedef EFI_STATUS (EFIAPI *EFI_TEXT_SET_ATTRIBUTE)(
    EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL* This,
    UINTN Attribute);

typedef EFI_STATUS (EFIAPI *EFI_TEXT_CLEAR_SCREEN)(
    EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL* This);

typedef EFI_STATUS (EFIAPI *EFI_TEXT_SET_CURSOR_POSITION)(
    EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL* This,
    UINTN Column,
    UINTN Row);

typedef EFI_STATUS (EFIAPI *EFI_TEXT_ENABLE_CURSOR)(
    EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL* This,
    BOOLEAN Visible);

struct _EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL {
    EFI_TEXT_RESET Reset;
    EFI_TEXT_STRING OutputString;
    EFI_TEXT_TEST_STRING TestString;
    EFI_TEXT_QUERY_MODE QueryMode;
    EFI_TEXT_SET_MODE SetMode;
    EFI_TEXT_SET_ATTRIBUTE SetAttribute;
    EFI_TEXT_CLEAR_SCREEN ClearScreen;
    EFI_TEXT_SET_CURSOR_POSITION SetCursorPosition;
    EFI_TEXT_ENABLE_CURSOR EnableCursor;
    SIMPLE_TEXT_OUTPUT_MODE* Mode;
};

struct _EFI_DEVICE_PATH_PROTOCOL {
    UINT8 Type;
    UINT8 SubType;
    UINT8 Length[2];
};

typedef EFI_STATUS (EFIAPI *EFI_HANDLE_PROTOCOL)(
    EFI_HANDLE Handle,
    EFI_GUID* Protocol,
    VOID** Interface);

typedef EFI_STATUS (EFIAPI *EFI_IMAGE_LOAD)(
    BOOLEAN BootPolicy,
    EFI_HANDLE ParentImageHandle,
    EFI_DEVICE_PATH_PROTOCOL* DevicePath,
    VOID* SourceBuffer,
    UINTN SourceSize,
    EFI_HANDLE* ImageHandle);

typedef EFI_STATUS (EFIAPI *EFI_IMAGE_START)(
    EFI_HANDLE ImageHandle,
    UINTN* ExitDataSize,
    CHAR16** ExitData);

typedef EFI_STATUS (EFIAPI *EFI_STALL)(UINTN Microseconds);

typedef struct _EFI_BOOT_SERVICES {
    EFI_TABLE_HEADER Hdr;
    VOID* RaiseTPL;
    VOID* RestoreTPL;
    VOID* AllocatePages;
    VOID* FreePages;
    VOID* GetMemoryMap;
    VOID* AllocatePool;
    VOID* FreePool;
    VOID* CreateEvent;
    VOID* SetTimer;
    VOID* WaitForEvent;
    VOID* SignalEvent;
    VOID* CloseEvent;
    VOID* CheckEvent;
    VOID* InstallProtocolInterface;
    VOID* ReinstallProtocolInterface;
    VOID* UninstallProtocolInterface;
    EFI_HANDLE_PROTOCOL HandleProtocol;
    VOID* Reserved;
    VOID* RegisterProtocolNotify;
    VOID* LocateHandle;
    VOID* LocateDevicePath;
    VOID* InstallConfigurationTable;
    EFI_IMAGE_LOAD LoadImage;
    EFI_IMAGE_START StartImage;
    VOID* Exit;
    VOID* UnloadImage;
    VOID* ExitBootServices;
    VOID* GetNextMonotonicCount;
    EFI_STALL Stall;
    VOID* SetWatchdogTimer;
    VOID* ConnectController;
    VOID* DisconnectController;
    VOID* OpenProtocol;
    VOID* CloseProtocol;
    VOID* OpenProtocolInformation;
    VOID* ProtocolsPerHandle;
    VOID* LocateHandleBuffer;
    VOID* LocateProtocol;
    VOID* InstallMultipleProtocolInterfaces;
    VOID* UninstallMultipleProtocolInterfaces;
    VOID* CalculateCrc32;
    VOID* CopyMem;
    VOID* SetMem;
    VOID* CreateEventEx;
} EFI_BOOT_SERVICES;

struct _EFI_SYSTEM_TABLE {
    EFI_TABLE_HEADER Hdr;
    CHAR16* FirmwareVendor;
    UINT32 FirmwareRevision;
    EFI_HANDLE ConsoleInHandle;
    VOID* ConIn;
    EFI_HANDLE ConsoleOutHandle;
    EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL* ConOut;
    EFI_HANDLE StandardErrorHandle;
    EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL* StdErr;
    VOID* RuntimeServices;
    EFI_BOOT_SERVICES* BootServices;
    UINTN NumberOfTableEntries;
    VOID* ConfigurationTable;
};

typedef EFI_STATUS (EFIAPI *EFI_IMAGE_UNLOAD)(EFI_HANDLE ImageHandle);

typedef struct _EFI_LOADED_IMAGE_PROTOCOL {
    UINT32 Revision;
    EFI_HANDLE ParentHandle;
    EFI_SYSTEM_TABLE* SystemTable;
    EFI_HANDLE DeviceHandle;
    EFI_DEVICE_PATH_PROTOCOL* FilePath;
    VOID* Reserved;
    UINT32 LoadOptionsSize;
    VOID* LoadOptions;
    VOID* ImageBase;
    UINT64 ImageSize;
    UINT32 ImageCodeType;
    UINT32 ImageDataType;
    EFI_IMAGE_UNLOAD Unload;
} EFI_LOADED_IMAGE_PROTOCOL;

