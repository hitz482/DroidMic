/*
 * The audio source shared by all capture streams: a ring of 48 kHz mono int16
 * samples, written by the desktop app via IOCTL_DROIDMIC_WRITE_AUDIO and read
 * by WaveRT capture streams from their timer callback.
 *
 * Lives in non-paged global storage; there is one DroidMic device per system.
 */
#include "common.h"
#include "ringbuffer.h"

static SHORT          g_RingStorage[DROIDMIC_RING_SAMPLES];
static DROIDMIC_RING  g_Ring;
static KSPIN_LOCK     g_RingLock;
static volatile LONG  g_ActiveStreams;

void DroidMicSourceInitialize()
{
    KeInitializeSpinLock(&g_RingLock);
    DroidMicRingInit(&g_Ring, g_RingStorage, DROIDMIC_RING_SAMPLES);
    g_ActiveStreams = 0;
}

void DroidMicSourceWrite(_In_reads_(count) const SHORT* samples, ULONG count)
{
    KIRQL irql;
    KeAcquireSpinLock(&g_RingLock, &irql);
    DroidMicRingWrite(&g_Ring, samples, count);
    if (g_ActiveStreams == 0) {
        /* Nobody is recording: don't let stale audio accumulate. */
        DroidMicRingTrim(&g_Ring, DROIDMIC_IDLE_KEEP_SAMPLES);
    }
    KeReleaseSpinLock(&g_RingLock, irql);
}

void DroidMicSourceReset()
{
    KIRQL irql;
    KeAcquireSpinLock(&g_RingLock, &irql);
    DroidMicRingClear(&g_Ring);
    KeReleaseSpinLock(&g_RingLock, irql);
}

void DroidMicSourceGetStatus(_Out_ DROIDMIC_STATUS* status)
{
    KIRQL irql;
    RtlZeroMemory(status, sizeof(*status));
    KeAcquireSpinLock(&g_RingLock, &irql);
    status->Version = DROIDMIC_INTERFACE_VERSION;
    status->SampleRate = DROIDMIC_SAMPLE_RATE;
    status->Channels = DROIDMIC_CHANNELS;
    status->RingCapacitySamples = g_Ring.Capacity;
    status->RingFillSamples = g_Ring.Fill;
    status->ActiveCaptureStreams = (ULONG)g_ActiveStreams;
    status->SamplesWritten = g_Ring.Written;
    status->SamplesRead = g_Ring.Read;
    status->UnderrunSamples = g_Ring.Underruns;
    status->OverflowSamples = g_Ring.Overflows;
    KeReleaseSpinLock(&g_RingLock, irql);
}

void DroidMicSourceRead(_Out_writes_(count) SHORT* samples, ULONG count)
{
    KIRQL irql;
    KeAcquireSpinLock(&g_RingLock, &irql);
    /* Only count underruns while the app is actually feeding audio. */
    DroidMicRingRead(&g_Ring, samples, count, g_Ring.Written > 0);
    KeReleaseSpinLock(&g_RingLock, irql);
}

void DroidMicSourceStreamStarted()
{
    KIRQL irql;
    KeAcquireSpinLock(&g_RingLock, &irql);
    if (g_ActiveStreams++ == 0) {
        /* Start the first client with only the freshest audio. */
        DroidMicRingTrim(&g_Ring, DROIDMIC_IDLE_KEEP_SAMPLES);
    }
    KeReleaseSpinLock(&g_RingLock, irql);
}

void DroidMicSourceStreamStopped()
{
    KIRQL irql;
    KeAcquireSpinLock(&g_RingLock, &irql);
    if (g_ActiveStreams > 0) g_ActiveStreams--;
    KeReleaseSpinLock(&g_RingLock, irql);
}
