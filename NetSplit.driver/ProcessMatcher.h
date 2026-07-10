#pragma once

#include <ntddk.h>

#define NDIS630
#include <ndis.h>
#include <fwpsk.h>

#include "Public.h"

// Extracts just the executable filename (e.g. "chrome.exe") from the full
// NT device path WFP hands us in classify metadata - no hardcoded names,
// no heap allocation. Safe to call from classify (PASSIVE_LEVEL, no locks
// taken, no memory owned beyond the caller's stack buffer).
//
// Returns FALSE if the process could not be identified at all (metadata
// absent) - callers must permit-and-return in that case.
BOOLEAN
NetSplitProcessMatcher_GetExecutableName(
    _In_ const FWPS_INCOMING_METADATA_VALUES0* InMetaValues,
    _Out_writes_z_(NETSPLIT_MAX_PROCESS_NAME_CHARS + 1) PWCH ExecutableNameBuffer,
    _Out_ PUSHORT ExecutableNameLenChars
);
