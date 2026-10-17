/* Kernel-mode operator new backed by ExAllocatePool2. The matching
 * operator delete overloads are provided by the WDK headers. */
#include "common.h"

PVOID operator new(size_t size, POOL_FLAGS poolFlags, ULONG tag)
{
    return ExAllocatePool2(poolFlags, size, tag);  /* ExAllocatePool2 zero-initializes */
}

PVOID operator new(size_t size, POOL_FLAGS poolFlags)
{
    return ExAllocatePool2(poolFlags, size, DROIDMIC_POOLTAG);
}
