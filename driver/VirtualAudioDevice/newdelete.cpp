/* Kernel-mode operator new/delete backed by ExAllocatePool2. */
#include "common.h"

PVOID operator new(size_t size, POOL_FLAGS poolFlags, ULONG tag)
{
    return ExAllocatePool2(poolFlags, size, tag);  /* ExAllocatePool2 zero-initializes */
}

PVOID operator new(size_t size, POOL_FLAGS poolFlags)
{
    return ExAllocatePool2(poolFlags, size, DROIDMIC_POOLTAG);
}

void __cdecl operator delete(PVOID p)
{
    if (p) ExFreePoolWithTag(p, DROIDMIC_POOLTAG);
}

void __cdecl operator delete(PVOID p, size_t)
{
    if (p) ExFreePoolWithTag(p, DROIDMIC_POOLTAG);
}

void __cdecl operator delete[](PVOID p)
{
    if (p) ExFreePoolWithTag(p, DROIDMIC_POOLTAG);
}
