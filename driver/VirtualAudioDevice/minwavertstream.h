#pragma once
#include "common.h"

class CMiniportWaveRT;

#define DROIDMIC_MAX_NOTIFICATION_EVENTS 4

/*
 * A WaveRT capture stream. The stream has no hardware clock: a high-resolution
 * timer advances the position from the performance counter and copies samples
 * from the DroidMic ring into the cyclic DMA buffer up to that position. Event
 * notifications are signalled each time a notification boundary is crossed.
 */
class CMiniportWaveRTStream : public IMiniportWaveRTStreamNotification, public CUnknown
{
public:
    DECLARE_STD_UNKNOWN();
    DEFINE_STD_CONSTRUCTOR(CMiniportWaveRTStream);
    ~CMiniportWaveRTStream();

    IMP_IMiniportWaveRTStream;
    IMP_IMiniportWaveRTStreamNotification;

    NTSTATUS Init(_In_ CMiniportWaveRT* Miniport, _In_ PPORTWAVERTSTREAM PortStream, _In_ PKSDATAFORMAT DataFormat);

private:
    static EXT_CALLBACK TimerCallback;

    NTSTATUS AllocateBuffer(ULONG RequestedSize, PMDL* Mdl, ULONG* ActualSize, ULONG* Offset, MEMORY_CACHING_TYPE* CacheType);
    void FreeBuffer();
    void UpdatePositionLocked(LARGE_INTEGER now);
    void WriteCaptureBytes(ULONG offset, ULONG bytes);
    void StartTimer();
    void StopTimer();

    CMiniportWaveRT*  m_Miniport = nullptr;
    PPORTWAVERTSTREAM m_PortStream = nullptr;

    KSPIN_LOCK  m_Lock;
    KSSTATE     m_State = KSSTATE_STOP;
    PEX_TIMER   m_Timer = nullptr;
    bool        m_Running = false;

    PMDL  m_BufferMdl = nullptr;
    PBYTE m_Buffer = nullptr;
    ULONG m_BufferSize = 0;

    ULONG     m_BlockAlign = 2;
    ULONG     m_BytesPerSecond = DROIDMIC_SAMPLE_RATE * 2;
    ULONGLONG m_QpcFrequency = 0;
    ULONGLONG m_LastQpc = 0;
    ULONGLONG m_ByteRemainder = 0;   /* sub-byte carry, scaled by QPC frequency */
    ULONG     m_Position = 0;        /* byte offset into the cyclic buffer */
    ULONGLONG m_LinearPosition = 0;  /* total bytes since stream start */

    ULONG  m_NotificationCount = 0;
    ULONG  m_NotificationInterval = 0;   /* bytes between notifications */
    PKEVENT m_Events[DROIDMIC_MAX_NOTIFICATION_EVENTS] = {};

    SHORT m_Scratch[DROIDMIC_SAMPLE_RATE / 100];  /* 10 ms bounce buffer */
};
