/*
 * Single-producer/single-consumer int16 ring buffer used between the control
 * IOCTL path (producer) and the WaveRT capture stream (consumer).
 *
 * Platform-neutral on purpose (no kernel headers) so it is unit-tested on any
 * host by driver/tests. Callers provide locking.
 */
#pragma once

#ifndef DROIDMIC_RING_INLINE
#define DROIDMIC_RING_INLINE static __inline
#endif

typedef struct _DROIDMIC_RING {
    short*             Samples;
    unsigned long      Capacity;   /* in samples */
    unsigned long      ReadIndex;
    unsigned long      Fill;
    unsigned long long Written;
    unsigned long long Read;
    unsigned long long Underruns;
    unsigned long long Overflows;
} DROIDMIC_RING;

DROIDMIC_RING_INLINE void DroidMicRingInit(DROIDMIC_RING* r, short* storage, unsigned long capacity)
{
    r->Samples = storage;
    r->Capacity = capacity;
    r->ReadIndex = 0;
    r->Fill = 0;
    r->Written = r->Read = r->Underruns = r->Overflows = 0;
}

DROIDMIC_RING_INLINE void DroidMicRingClear(DROIDMIC_RING* r)
{
    r->ReadIndex = 0;
    r->Fill = 0;
}

/* Drops the oldest samples so that at most `keep` remain. */
DROIDMIC_RING_INLINE void DroidMicRingTrim(DROIDMIC_RING* r, unsigned long keep)
{
    if (r->Fill > keep) {
        unsigned long drop = r->Fill - keep;
        r->ReadIndex = (r->ReadIndex + drop) % r->Capacity;
        r->Fill = keep;
        r->Overflows += drop;
    }
}

/* Appends samples; if the ring is full the oldest samples are overwritten
 * (latest audio wins, latency stays bounded). */
DROIDMIC_RING_INLINE void DroidMicRingWrite(DROIDMIC_RING* r, const short* src, unsigned long count)
{
    unsigned long i;
    if (count > r->Capacity) {
        r->Overflows += count - r->Capacity;
        src += count - r->Capacity;
        count = r->Capacity;
    }
    if (r->Fill + count > r->Capacity) {
        DroidMicRingTrim(r, r->Capacity - count);
    }
    {
        unsigned long writeIndex = (r->ReadIndex + r->Fill) % r->Capacity;
        unsigned long first = r->Capacity - writeIndex;
        if (first > count) first = count;
        for (i = 0; i < first; i++) r->Samples[writeIndex + i] = src[i];
        for (i = first; i < count; i++) r->Samples[i - first] = src[i];
    }
    r->Fill += count;
    r->Written += count;
}

/* Reads exactly `count` samples into dst, padding with silence on underrun.
 * Returns the number of real (non-padded) samples. */
DROIDMIC_RING_INLINE unsigned long DroidMicRingRead(DROIDMIC_RING* r, short* dst, unsigned long count, int countUnderrun)
{
    unsigned long avail = r->Fill < count ? r->Fill : count;
    unsigned long first = r->Capacity - r->ReadIndex;
    unsigned long i;
    if (first > avail) first = avail;
    for (i = 0; i < first; i++) dst[i] = r->Samples[r->ReadIndex + i];
    for (i = first; i < avail; i++) dst[i] = r->Samples[i - first];
    for (i = avail; i < count; i++) dst[i] = 0;
    r->ReadIndex = (r->ReadIndex + avail) % r->Capacity;
    r->Fill -= avail;
    r->Read += avail;
    if (countUnderrun && avail < count) r->Underruns += count - avail;
    return avail;
}
