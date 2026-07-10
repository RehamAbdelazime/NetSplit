#include "ProcessMatcher.h"

BOOLEAN
NetSplitProcessMatcher_GetExecutableName(
    _In_ const FWPS_INCOMING_METADATA_VALUES0* InMetaValues,
    _Out_writes_z_(NETSPLIT_MAX_PROCESS_NAME_CHARS + 1) PWCH ExecutableNameBuffer,
    _Out_ PUSHORT ExecutableNameLenChars
)
{
    ExecutableNameBuffer[0] = L'\0';
    *ExecutableNameLenChars = 0;

    // FWPS_METADATA_FIELD_PROCESS_PATH: the one documented, allocation-free
    // way to get the originating process's full image path at this layer.
    // Owned by the filter engine for the duration of this call only - we
    // never retain the pointer or free it.
    if (!FWPS_IS_METADATA_FIELD_PRESENT(InMetaValues, FWPS_METADATA_FIELD_PROCESS_PATH))
    {
        return FALSE;
    }

    FWP_BYTE_BLOB* processPath = InMetaValues->processPath;
    if (processPath == nullptr || processPath->data == nullptr || processPath->size < sizeof(WCHAR))
    {
        return FALSE;
    }

    PWCH path = (PWCH)processPath->data;
    USHORT pathLenChars = (USHORT)(processPath->size / sizeof(WCHAR));

    // Walk backward from the end to find the last path separator; everything
    // after it is the filename. No allocation - just an index scan.
    USHORT nameStart = 0;
    for (USHORT i = pathLenChars; i > 0; i--)
    {
        if (path[i - 1] == L'\\')
        {
            nameStart = i;
            break;
        }
    }

    USHORT nameLenChars = pathLenChars - nameStart;
    if (nameLenChars == 0 || nameLenChars > NETSPLIT_MAX_PROCESS_NAME_CHARS)
    {
        return FALSE;
    }

    RtlCopyMemory(ExecutableNameBuffer, path + nameStart, nameLenChars * sizeof(WCHAR));
    ExecutableNameBuffer[nameLenChars] = L'\0';
    *ExecutableNameLenChars = nameLenChars;
    return TRUE;
}
