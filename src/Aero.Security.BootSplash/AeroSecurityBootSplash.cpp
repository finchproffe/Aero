#include "UefiMinimal.h"

extern "C" EFI_STATUS EFIAPI EfiMain(EFI_HANDLE ImageHandle, EFI_SYSTEM_TABLE* SystemTable);

static VOID* volatile gRelocationAnchor = (VOID*)&EfiMain;

static const EFI_GUID kLoadedImageProtocolGuid = {
    0x5B1B31A1,
    0x9562,
    0x11D2,
    { 0x8E, 0x3F, 0x00, 0xA0, 0xC9, 0x69, 0x72, 0x3B }
};

static const EFI_GUID kDevicePathProtocolGuid = {
    0x09576E91,
    0x6D3F,
    0x11D2,
    { 0x8E, 0x39, 0x00, 0xA0, 0xC9, 0x69, 0x72, 0x3B }
};

enum AERO_BOOT_STAGE : UINTN {
    AeroBootStageNone = 0,
    AeroBootStageLoadedImage = 1,
    AeroBootStageDevicePath = 2,
    AeroBootStageBuildPath = 3,
    AeroBootStageLoadImage = 4,
    AeroBootStageStartImage = 5
};

static UINTN
AeroStringLength(
    const CHAR16* Text
    )
{
    UINTN length = 0;

    if (Text == 0) {
        return 0;
    }

    while (Text[length] != L'\0') {
        length += 1;
    }

    return length;
}

static VOID
AeroCopyBytes(
    UINT8* Destination,
    const UINT8* Source,
    UINTN Length
    )
{
    volatile UINT8* destination = (volatile UINT8*)Destination;
    UINTN index;

    for (index = 0; index < Length; index += 1) {
        destination[index] = Source[index];
    }
}

static UINT16
AeroDevicePathNodeLength(
    const EFI_DEVICE_PATH_PROTOCOL* Node
    )
{
    if (Node == 0) {
        return 0;
    }

    return (UINT16)((UINT16)Node->Length[0] |
        ((UINT16)Node->Length[1] << 8));
}

static BOOLEAN
AeroIsEndDevicePath(
    const EFI_DEVICE_PATH_PROTOCOL* Node
    )
{
    return Node != 0 &&
        Node->Type == END_DEVICE_PATH_TYPE &&
        Node->SubType == END_ENTIRE_DEVICE_PATH_SUBTYPE;
}

static EFI_STATUS
AeroGetDevicePathSize(
    const EFI_DEVICE_PATH_PROTOCOL* DevicePath,
    UINTN* DevicePathSize
    )
{
    const EFI_DEVICE_PATH_PROTOCOL* node = DevicePath;
    UINTN totalSize = 0;
    UINTN nodeCount = 0;

    if (DevicePath == 0 || DevicePathSize == 0) {
        return EFI_INVALID_PARAMETER;
    }

    while (nodeCount < 128) {
        const UINT16 nodeLength = AeroDevicePathNodeLength(node);

        if (nodeLength < sizeof(EFI_DEVICE_PATH_PROTOCOL) ||
            totalSize > (4096 - nodeLength)) {
            return EFI_INVALID_PARAMETER;
        }

        totalSize += nodeLength;

        if (AeroIsEndDevicePath(node)) {
            *DevicePathSize = totalSize;
            return EFI_SUCCESS;
        }

        node = (const EFI_DEVICE_PATH_PROTOCOL*)
            ((const UINT8*)node + nodeLength);
        nodeCount += 1;
    }

    return EFI_INVALID_PARAMETER;
}

static VOID
AeroSetDevicePathLength(
    EFI_DEVICE_PATH_PROTOCOL* Node,
    UINT16 Length
    )
{
    Node->Length[0] = (UINT8)(Length & 0xFF);
    Node->Length[1] = (UINT8)((Length >> 8) & 0xFF);
}

static EFI_STATUS
AeroCreateWindowsBootPath(
    const EFI_DEVICE_PATH_PROTOCOL* BaseDevicePath,
    UINT8* Buffer,
    UINTN BufferSize,
    EFI_DEVICE_PATH_PROTOCOL** DevicePath
    )
{
    static const CHAR16 path[] = L"\\EFI\\Microsoft\\Boot\\bootmgfw.efi";
    const UINTN pathCharacters = AeroStringLength(path) + 1;
    const UINTN fileNodeLength =
        sizeof(EFI_DEVICE_PATH_PROTOCOL) +
        (pathCharacters * sizeof(CHAR16));
    UINTN baseDevicePathSize = 0;
    UINTN baseWithoutEnd;
    UINTN totalLength;
    EFI_DEVICE_PATH_PROTOCOL* fileNode;
    EFI_DEVICE_PATH_PROTOCOL* endNode;
    volatile CHAR16* destination;
    UINTN index;
    EFI_STATUS status;

    if (BaseDevicePath == 0 || Buffer == 0 || DevicePath == 0 ||
        fileNodeLength > 0xFFFF) {
        return EFI_INVALID_PARAMETER;
    }

    status = AeroGetDevicePathSize(BaseDevicePath, &baseDevicePathSize);
    if (EFI_ERROR(status) ||
        baseDevicePathSize < sizeof(EFI_DEVICE_PATH_PROTOCOL)) {
        return EFI_INVALID_PARAMETER;
    }

    baseWithoutEnd = baseDevicePathSize - sizeof(EFI_DEVICE_PATH_PROTOCOL);
    totalLength = baseWithoutEnd +
        fileNodeLength +
        sizeof(EFI_DEVICE_PATH_PROTOCOL);

    if (totalLength > BufferSize) {
        return EFI_INVALID_PARAMETER;
    }

    AeroCopyBytes(Buffer, (const UINT8*)BaseDevicePath, baseWithoutEnd);

    fileNode = (EFI_DEVICE_PATH_PROTOCOL*)(Buffer + baseWithoutEnd);
    fileNode->Type = MEDIA_DEVICE_PATH;
    fileNode->SubType = MEDIA_FILEPATH_DP;
    AeroSetDevicePathLength(fileNode, (UINT16)fileNodeLength);

    destination = (volatile CHAR16*)(
        Buffer + baseWithoutEnd + sizeof(EFI_DEVICE_PATH_PROTOCOL));
    for (index = 0; index < pathCharacters; index += 1) {
        destination[index] = path[index];
    }

    endNode = (EFI_DEVICE_PATH_PROTOCOL*)(
        Buffer + baseWithoutEnd + fileNodeLength);
    endNode->Type = END_DEVICE_PATH_TYPE;
    endNode->SubType = END_ENTIRE_DEVICE_PATH_SUBTYPE;
    AeroSetDevicePathLength(endNode, (UINT16)sizeof(EFI_DEVICE_PATH_PROTOCOL));

    *DevicePath = (EFI_DEVICE_PATH_PROTOCOL*)Buffer;
    return EFI_SUCCESS;
}

static VOID
AeroShowStatus(
    EFI_SYSTEM_TABLE* SystemTable,
    CHAR16* Text,
    UINTN Attribute,
    UINTN RowOffset
    )
{
    EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL* output;
    UINTN columns = 80;
    UINTN rows = 25;
    UINTN textLength;
    UINTN column;
    UINTN row;

    if (SystemTable == 0 || SystemTable->ConOut == 0 || Text == 0) {
        return;
    }

    output = SystemTable->ConOut;

    if (output->Mode != 0 && output->QueryMode != 0) {
        if (EFI_ERROR(output->QueryMode(
                output,
                (UINTN)output->Mode->Mode,
                &columns,
                &rows))) {
            columns = 80;
            rows = 25;
        }
    }

    textLength = AeroStringLength(Text);
    column = columns > (textLength + 2) ? columns - textLength - 2 : 0;
    row = rows > (RowOffset + 1) ? rows - RowOffset - 1 : 0;

    if (output->EnableCursor != 0) {
        (VOID)output->EnableCursor(output, FALSE);
    }

    if (output->SetAttribute != 0) {
        (VOID)output->SetAttribute(output, Attribute);
    }

    if (output->SetCursorPosition != 0) {
        (VOID)output->SetCursorPosition(output, column, row);
    }

    if (output->OutputString != 0) {
        (VOID)output->OutputString(output, Text);
    }
}

static VOID
AeroFormatFailure(
    CHAR16* Buffer,
    UINTN BufferCharacters,
    UINTN Stage,
    EFI_STATUS Status
    )
{
    static const CHAR16 prefix[] = L"Aero preloader failed S";
    static const CHAR16 middle[] = L" E0x";
    static const CHAR16 hex[] = L"0123456789ABCDEF";
    UINTN output = 0;
    UINTN index;

    if (Buffer == 0 || BufferCharacters < 32) {
        return;
    }

    for (index = 0; prefix[index] != L'\0'; index += 1) {
        Buffer[output++] = prefix[index];
    }

    Buffer[output++] = (CHAR16)(L'0' + (Stage % 10));

    for (index = 0; middle[index] != L'\0'; index += 1) {
        Buffer[output++] = middle[index];
    }

    for (index = 0; index < 16; index += 1) {
        const UINTN shift = (15 - index) * 4;
        Buffer[output++] = hex[(UINTN)((Status >> shift) & 0xFULL)];
    }

    Buffer[output] = L'\0';
}

static EFI_STATUS
AeroStartWindowsBootManager(
    EFI_HANDLE ImageHandle,
    EFI_SYSTEM_TABLE* SystemTable,
    UINTN* FailureStage
    )
{
    EFI_LOADED_IMAGE_PROTOCOL* loadedImage = 0;
    EFI_DEVICE_PATH_PROTOCOL* baseDevicePath = 0;
    EFI_DEVICE_PATH_PROTOCOL* bootPath = 0;
    EFI_HANDLE windowsImage = 0;
    UINT8 pathBuffer[1024];
    EFI_STATUS status;

    if (FailureStage != 0) {
        *FailureStage = AeroBootStageNone;
    }

    if (SystemTable == 0 || SystemTable->BootServices == 0) {
        return EFI_INVALID_PARAMETER;
    }

    if (FailureStage != 0) {
        *FailureStage = AeroBootStageLoadedImage;
    }

    status = SystemTable->BootServices->HandleProtocol(
        ImageHandle,
        (EFI_GUID*)&kLoadedImageProtocolGuid,
        (VOID**)&loadedImage);

    if (EFI_ERROR(status) || loadedImage == 0 || loadedImage->DeviceHandle == 0) {
        return EFI_ERROR(status) ? status : EFI_NOT_FOUND;
    }

    if (FailureStage != 0) {
        *FailureStage = AeroBootStageDevicePath;
    }

    status = SystemTable->BootServices->HandleProtocol(
        loadedImage->DeviceHandle,
        (EFI_GUID*)&kDevicePathProtocolGuid,
        (VOID**)&baseDevicePath);

    if (EFI_ERROR(status) || baseDevicePath == 0) {
        return EFI_ERROR(status) ? status : EFI_NOT_FOUND;
    }

    if (FailureStage != 0) {
        *FailureStage = AeroBootStageBuildPath;
    }

    status = AeroCreateWindowsBootPath(
        baseDevicePath,
        pathBuffer,
        sizeof(pathBuffer),
        &bootPath);

    if (EFI_ERROR(status)) {
        return status;
    }

    if (FailureStage != 0) {
        *FailureStage = AeroBootStageLoadImage;
    }

    status = SystemTable->BootServices->LoadImage(
        FALSE,
        ImageHandle,
        bootPath,
        0,
        0,
        &windowsImage);

    if (EFI_ERROR(status)) {
        return status;
    }

    if (FailureStage != 0) {
        *FailureStage = AeroBootStageStartImage;
    }

    return SystemTable->BootServices->StartImage(
        windowsImage,
        0,
        0);
}

extern "C"
EFI_STATUS
EFIAPI
EfiMain(
    EFI_HANDLE ImageHandle,
    EFI_SYSTEM_TABLE* SystemTable
    )
{
    EFI_STATUS status;
    UINTN failureStage = AeroBootStageNone;
    CHAR16 failure[64];
    static CHAR16 title[] = L"AERO SECURITY";
    static CHAR16 loading[] = L"AeroDriver Loading...";

    if (gRelocationAnchor == 0) {
        return EFI_INVALID_PARAMETER;
    }

    AeroShowStatus(
        SystemTable,
        title,
        EFI_LIGHTCYAN | EFI_BACKGROUND_BLACK,
        3);
    AeroShowStatus(
        SystemTable,
        loading,
        EFI_WHITE | EFI_BACKGROUND_BLACK,
        2);

    if (SystemTable != 0 &&
        SystemTable->BootServices != 0 &&
        SystemTable->BootServices->Stall != 0) {
        (VOID)SystemTable->BootServices->Stall(900000);
    }

    status = AeroStartWindowsBootManager(
        ImageHandle,
        SystemTable,
        &failureStage);

    if (EFI_ERROR(status)) {
        AeroFormatFailure(
            failure,
            sizeof(failure) / sizeof(failure[0]),
            failureStage,
            status);

        AeroShowStatus(
            SystemTable,
            failure,
            EFI_LIGHTRED | EFI_BACKGROUND_BLACK,
            1);

        if (SystemTable != 0 &&
            SystemTable->BootServices != 0 &&
            SystemTable->BootServices->Stall != 0) {
            (VOID)SystemTable->BootServices->Stall(5000000);
        }
    }

    return status;
}
