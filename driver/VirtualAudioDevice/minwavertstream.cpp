#include "minwavertstream.h"
#include "minwavert.h"

/* Position update period while running (100 ns units): 1 ms keeps the DMA
 * buffer filled well ahead of the audio engine's 10 ms reads. */
#define TIMER_PERIOD_100NS (10 * 1000)

NTSTATUS CMiniportWaveRTStream::Init(_In_ CMiniportWaveRT* Miniport, _In_ PPORTWAVERTSTREAM PortStream, _In_ PKSDATAFORMAT DataFormat)
{
    PAGED_CODE();
    KeInitializeSpinLock(&m_Lock);

    PWAVEFORMATEX wfx = (PWAVEFORMATEX)(DataFormat + 1);
    m_BlockAlign = wfx->nBlockAlign;
    m_BytesPerSecond = wfx->nAvgBytesPerSec;

    LARGE_INTEGER freq;
    KeQueryPerformanceCounter(&freq);
    m_QpcFrequency = (ULONGLONG)freq.QuadPart;

    m_Timer = ExAllocateTimer(TimerCallback, this, EX_TIMER_HIGH_RESOLUTION);
    if (!m_Timer) return STATUS_INSUFFICIENT_RESOURCES;

    m_PortStream = PortStream;
    m_PortStream->AddRef();
    /* Last: once m_Miniport is set the destructor reports the stream as closed. */
    m_Miniport = Miniport;
    m_Miniport->AddRef();
    return STATUS_SUCCESS;
}

CMiniportWaveRTStream::~CMiniportWaveRTStream()
{
    PAGED_CODE();
    if (m_Timer)
    {
        /* Cancel and wait for any running callback before freeing anything it touches. */
        ExDeleteTimer(m_Timer, TRUE, TRUE, NULL);
        m_Timer = nullptr;
    }
    if (m_Running) DroidMicSourceStreamStopped();
    FreeBuffer();
    if (m_PortStream) m_PortStream->Release();
    if (m_Miniport)
    {
        m_Miniport->StreamClosed(this);
        m_Miniport->Release();
    }
}

STDMETHODIMP_(NTSTATUS) CMiniportWaveRTStream::NonDelegatingQueryInterface(_In_ REFIID Interface, _COM_Outptr_ PVOID* Object)
{
    PAGED_CODE();
    if (IsEqualGUIDAligned(Interface, IID_IUnknown))
        *Object = PVOID(PUNKNOWN(PMINIPORTWAVERTSTREAM(this)));
    else if (IsEqualGUIDAligned(Interface, IID_IMiniportWaveRTStream))
        *Object = PVOID(PMINIPORTWAVERTSTREAM(this));
    else if (IsEqualGUIDAligned(Interface, IID_IMiniportWaveRTStreamNotification))
        *Object = PVOID(PMINIPORTWAVERTSTREAMNOTIFICATION(this));
    else
    {
        *Object = NULL;
        return STATUS_INVALID_PARAMETER;
    }
    PUNKNOWN(*Object)->AddRef();
    return STATUS_SUCCESS;
}

/* ---- Buffer management ---------------------------------------------------- */

NTSTATUS CMiniportWaveRTStream::AllocateBuffer(ULONG RequestedSize, PMDL* Mdl, ULONG* ActualSize, ULONG* Offset, MEMORY_CACHING_TYPE* CacheType)
{
    if (m_BufferMdl) return STATUS_UNSUCCESSFUL;
    if (RequestedSize == 0 || RequestedSize > m_BytesPerSecond * 2) RequestedSize = m_BytesPerSecond / 10;
    RequestedSize -= RequestedSize % m_BlockAlign;
    if (RequestedSize == 0) return STATUS_INVALID_PARAMETER;

    PHYSICAL_ADDRESS high;
    high.QuadPart = (LONGLONG)-1;
    PMDL mdl = m_PortStream->AllocatePagesForMdl(high, RequestedSize);
    if (!mdl) return STATUS_INSUFFICIENT_RESOURCES;
    PBYTE va = (PBYTE)m_PortStream->MapAllocatedPages(mdl, MmCached);
    if (!va)
    {
        m_PortStream->FreePagesFromMdl(mdl);
        return STATUS_INSUFFICIENT_RESOURCES;
    }
    RtlZeroMemory(va, RequestedSize);

    KIRQL irql;
    KeAcquireSpinLock(&m_Lock, &irql);
    m_BufferMdl = mdl;
    m_Buffer = va;
    m_BufferSize = RequestedSize;
    m_Position = 0;
    KeReleaseSpinLock(&m_Lock, irql);

    *Mdl = mdl;
    *ActualSize = RequestedSize;
    *Offset = 0;
    *CacheType = MmCached;
    return STATUS_SUCCESS;
}

void CMiniportWaveRTStream::FreeBuffer()
{
    PMDL mdl;
    PBYTE va;
    KIRQL irql;
    KeAcquireSpinLock(&m_Lock, &irql);
    mdl = m_BufferMdl;
    va = m_Buffer;
    m_BufferMdl = nullptr;
    m_Buffer = nullptr;
    m_BufferSize = 0;
    KeReleaseSpinLock(&m_Lock, irql);
    if (mdl)
    {
        m_PortStream->UnmapAllocatedPages(va, mdl);
        m_PortStream->FreePagesFromMdl(mdl);
    }
}

STDMETHODIMP_(NTSTATUS) CMiniportWaveRTStream::AllocateAudioBuffer(
    _In_ ULONG RequestedSize, _Out_ PMDL* AudioBufferMdl, _Out_ ULONG* ActualSize,
    _Out_ ULONG* OffsetFromFirstPage, _Out_ MEMORY_CACHING_TYPE* CacheType)
{
    m_NotificationCount = 0;
    m_NotificationInterval = 0;
    return AllocateBuffer(RequestedSize, AudioBufferMdl, ActualSize, OffsetFromFirstPage, CacheType);
}

STDMETHODIMP_(VOID) CMiniportWaveRTStream::FreeAudioBuffer(_In_opt_ PMDL, _In_ ULONG)
{
    FreeBuffer();
}

STDMETHODIMP_(NTSTATUS) CMiniportWaveRTStream::AllocateBufferWithNotification(
    _In_ ULONG NotificationCount, _In_ ULONG RequestedSize, _Out_ PMDL* AudioBufferMdl, _Out_ ULONG* ActualSize,
    _Out_ ULONG* OffsetFromFirstPage, _Out_ MEMORY_CACHING_TYPE* CacheType)
{
    if (NotificationCount == 0 || NotificationCount > 16) return STATUS_INVALID_PARAMETER;
    /* Make the buffer divide evenly into notification periods. */
    ULONG unit = m_BlockAlign * NotificationCount;
    RequestedSize -= RequestedSize % unit;
    NTSTATUS status = AllocateBuffer(RequestedSize, AudioBufferMdl, ActualSize, OffsetFromFirstPage, CacheType);
    if (NT_SUCCESS(status))
    {
        m_NotificationCount = NotificationCount;
        m_NotificationInterval = *ActualSize / NotificationCount;
    }
    return status;
}

STDMETHODIMP_(VOID) CMiniportWaveRTStream::FreeBufferWithNotification(_In_ PMDL, _In_ ULONG)
{
    FreeBuffer();
}

STDMETHODIMP_(NTSTATUS) CMiniportWaveRTStream::RegisterNotificationEvent(_In_ PKEVENT NotificationEvent)
{
    KIRQL irql;
    NTSTATUS status = STATUS_INSUFFICIENT_RESOURCES;
    KeAcquireSpinLock(&m_Lock, &irql);
    for (ULONG i = 0; i < DROIDMIC_MAX_NOTIFICATION_EVENTS; i++)
    {
        if (m_Events[i] == NotificationEvent) { status = STATUS_SUCCESS; break; }
        if (!m_Events[i]) { m_Events[i] = NotificationEvent; status = STATUS_SUCCESS; break; }
    }
    KeReleaseSpinLock(&m_Lock, irql);
    return status;
}

STDMETHODIMP_(NTSTATUS) CMiniportWaveRTStream::UnregisterNotificationEvent(_In_ PKEVENT NotificationEvent)
{
    KIRQL irql;
    NTSTATUS status = STATUS_NOT_FOUND;
    KeAcquireSpinLock(&m_Lock, &irql);
    for (ULONG i = 0; i < DROIDMIC_MAX_NOTIFICATION_EVENTS; i++)
    {
        if (m_Events[i] == NotificationEvent) { m_Events[i] = nullptr; status = STATUS_SUCCESS; }
    }
    KeReleaseSpinLock(&m_Lock, irql);
    return status;
}

/* ---- State and position ---------------------------------------------------- */

STDMETHODIMP_(NTSTATUS) CMiniportWaveRTStream::SetFormat(_In_ PKSDATAFORMAT)
{
    /* The format is fixed at stream creation. */
    return STATUS_NOT_SUPPORTED;
}

STDMETHODIMP_(NTSTATUS) CMiniportWaveRTStream::SetState(_In_ KSSTATE State)
{
    KIRQL irql;
    switch (State)
    {
    case KSSTATE_STOP:
        StopTimer();
        KeAcquireSpinLock(&m_Lock, &irql);
        m_Position = 0;
        m_LinearPosition = 0;
        m_ByteRemainder = 0;
        if (m_Buffer) RtlZeroMemory(m_Buffer, m_BufferSize);
        KeReleaseSpinLock(&m_Lock, irql);
        break;

    case KSSTATE_ACQUIRE:
        break;

    case KSSTATE_PAUSE:
        if (m_State == KSSTATE_RUN)
        {
            KeAcquireSpinLock(&m_Lock, &irql);
            LARGE_INTEGER now = KeQueryPerformanceCounter(NULL);
            UpdatePositionLocked(now);
            KeReleaseSpinLock(&m_Lock, irql);
            StopTimer();
        }
        break;

    case KSSTATE_RUN:
        KeAcquireSpinLock(&m_Lock, &irql);
        m_LastQpc = (ULONGLONG)KeQueryPerformanceCounter(NULL).QuadPart;
        KeReleaseSpinLock(&m_Lock, irql);
        StartTimer();
        break;

    default:
        return STATUS_INVALID_PARAMETER;
    }
    m_State = State;
    return STATUS_SUCCESS;
}

void CMiniportWaveRTStream::StartTimer()
{
    KIRQL irql;
    KeAcquireSpinLock(&m_Lock, &irql);
    bool wasRunning = m_Running;
    m_Running = true;
    KeReleaseSpinLock(&m_Lock, irql);
    if (wasRunning) return;
    DroidMicSourceStreamStarted();
    ExSetTimer(m_Timer, -TIMER_PERIOD_100NS, TIMER_PERIOD_100NS, NULL);
}

void CMiniportWaveRTStream::StopTimer()
{
    KIRQL irql;
    KeAcquireSpinLock(&m_Lock, &irql);
    bool wasRunning = m_Running;
    m_Running = false;   /* a callback already in flight sees this and does nothing */
    KeReleaseSpinLock(&m_Lock, irql);
    if (!wasRunning) return;
    ExCancelTimer(m_Timer, NULL);
    DroidMicSourceStreamStopped();
}

void CMiniportWaveRTStream::TimerCallback(_In_ PEX_TIMER, _In_opt_ PVOID Context)
{
    CMiniportWaveRTStream* self = (CMiniportWaveRTStream*)Context;
    KIRQL irql;
    KeAcquireSpinLock(&self->m_Lock, &irql);
    if (self->m_Running) self->UpdatePositionLocked(KeQueryPerformanceCounter(NULL));
    KeReleaseSpinLock(&self->m_Lock, irql);
}

/* Advances the capture position to `now`, filling the newly "recorded" region of
 * the DMA buffer from the ring and signalling notification events. Caller holds m_Lock. */
void CMiniportWaveRTStream::UpdatePositionLocked(LARGE_INTEGER now)
{
    if (!m_Buffer || m_BufferSize == 0 || m_QpcFrequency == 0) return;
    ULONGLONG qpc = (ULONGLONG)now.QuadPart;
    if (qpc <= m_LastQpc) return;
    ULONGLONG delta = qpc - m_LastQpc;
    m_LastQpc = qpc;

    /* bytes = delta * bytesPerSecond / frequency, carrying the remainder so the
     * long-term rate is exact. Clamp huge gaps (e.g. debugger break). */
    if (delta > m_QpcFrequency) delta = m_QpcFrequency;
    ULONGLONG scaled = delta * m_BytesPerSecond + m_ByteRemainder;
    ULONGLONG bytes = scaled / m_QpcFrequency;
    m_ByteRemainder = scaled % m_QpcFrequency;
    bytes -= bytes % m_BlockAlign;
    if (bytes == 0) return;
    if (bytes > m_BufferSize) bytes = m_BufferSize;

    ULONG oldPos = m_Position;
    WriteCaptureBytes(oldPos, (ULONG)bytes);
    m_Position = (ULONG)((oldPos + bytes) % m_BufferSize);
    m_LinearPosition += bytes;

    if (m_NotificationInterval)
    {
        ULONGLONG before = (m_LinearPosition - bytes) / m_NotificationInterval;
        ULONGLONG after = m_LinearPosition / m_NotificationInterval;
        if (after != before)
        {
            for (ULONG i = 0; i < DROIDMIC_MAX_NOTIFICATION_EVENTS; i++)
                if (m_Events[i]) KeSetEvent(m_Events[i], 0, FALSE);
        }
    }
}

/* Copies `bytes` of audio from the ring into the cyclic buffer at `offset`. */
void CMiniportWaveRTStream::WriteCaptureBytes(ULONG offset, ULONG bytes)
{
    while (bytes > 0)
    {
        ULONG chunk = min(bytes, m_BufferSize - offset);
        chunk = min(chunk, (ULONG)sizeof(m_Scratch));
        ULONG samples = chunk / sizeof(SHORT);
        DroidMicSourceRead(m_Scratch, samples);
        RtlCopyMemory(m_Buffer + offset, m_Scratch, samples * sizeof(SHORT));
        offset = (offset + chunk) % m_BufferSize;
        bytes -= chunk;
    }
}

STDMETHODIMP_(NTSTATUS) CMiniportWaveRTStream::GetPosition(_Out_ PKSAUDIO_POSITION Position)
{
    KIRQL irql;
    KeAcquireSpinLock(&m_Lock, &irql);
    if (m_Running) UpdatePositionLocked(KeQueryPerformanceCounter(NULL));
    /* For capture both offsets are the point up to which valid data exists. */
    Position->PlayOffset = m_Position;
    Position->WriteOffset = m_Position;
    KeReleaseSpinLock(&m_Lock, irql);
    return STATUS_SUCCESS;
}

STDMETHODIMP_(VOID) CMiniportWaveRTStream::GetHWLatency(_Out_ PKSRTAUDIO_HWLATENCY Latency)
{
    Latency->ChipsetDelay = 0;
    Latency->CodecDelay = 0;
    Latency->FifoSize = 0;
}

STDMETHODIMP_(NTSTATUS) CMiniportWaveRTStream::GetPositionRegister(_Out_ PKSRTAUDIO_HWREGISTER)
{
    return STATUS_NOT_SUPPORTED;
}

STDMETHODIMP_(NTSTATUS) CMiniportWaveRTStream::GetClockRegister(_Out_ PKSRTAUDIO_HWREGISTER)
{
    return STATUS_NOT_SUPPORTED;
}
