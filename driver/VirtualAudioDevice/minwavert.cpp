/*
 * WaveRT miniport: the capture filter that recording clients (via the Windows
 * audio engine) stream from. One pin format: 48 kHz, 16-bit, mono PCM. The
 * audio engine converts for clients that ask for other formats in shared mode.
 */
#include "minwavert.h"
#include "minwavertstream.h"

/* ---- Filter description -------------------------------------------------- */

static KSDATARANGE_AUDIO WaveHostPinDataRanges[] =
{
    {
        {
            sizeof(KSDATARANGE_AUDIO),
            0, 0, 0,
            STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO),
            STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM),
            STATICGUIDOF(KSDATAFORMAT_SPECIFIER_WAVEFORMATEX)
        },
        DROIDMIC_CHANNELS,          /* MaximumChannels */
        DROIDMIC_BITS_PER_SAMPLE,   /* MinimumBitsPerSample */
        DROIDMIC_BITS_PER_SAMPLE,   /* MaximumBitsPerSample */
        DROIDMIC_SAMPLE_RATE,       /* MinimumSampleFrequency */
        DROIDMIC_SAMPLE_RATE        /* MaximumSampleFrequency */
    },
};

static PKSDATARANGE WaveHostPinDataRangePointers[] =
{
    PKSDATARANGE(&WaveHostPinDataRanges[0]),
};

static KSDATARANGE WaveBridgePinDataRanges[] =
{
    {
        sizeof(KSDATARANGE),
        0, 0, 0,
        STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO),
        STATICGUIDOF(KSDATAFORMAT_SUBTYPE_ANALOG),
        STATICGUIDOF(KSDATAFORMAT_SPECIFIER_NONE)
    },
};

static PKSDATARANGE WaveBridgePinDataRangePointers[] =
{
    &WaveBridgePinDataRanges[0],
};

static PCPIN_DESCRIPTOR WaveMiniportPins[] =
{
    /* KSPIN_WAVE_HOST: capture streaming pin */
    {
        DROIDMIC_MAX_STREAMS, DROIDMIC_MAX_STREAMS, 0,
        NULL,
        {
            0, NULL,
            0, NULL,
            SIZEOF_ARRAY(WaveHostPinDataRangePointers),
            WaveHostPinDataRangePointers,
            KSPIN_DATAFLOW_OUT,
            KSPIN_COMMUNICATION_SINK,
            &KSCATEGORY_AUDIO,
            &KSAUDFNAME_RECORDING_CONTROL,
            0
        }
    },
    /* KSPIN_WAVE_BRIDGE: from the topology filter */
    {
        0, 0, 0,
        NULL,
        {
            0, NULL,
            0, NULL,
            SIZEOF_ARRAY(WaveBridgePinDataRangePointers),
            WaveBridgePinDataRangePointers,
            KSPIN_DATAFLOW_IN,
            KSPIN_COMMUNICATION_NONE,
            &KSCATEGORY_AUDIO,
            NULL,
            0
        }
    },
};

static PCNODE_DESCRIPTOR WaveMiniportNodes[] =
{
    /* KSNODE_WAVE_ADC */
    { 0, NULL, &KSNODETYPE_ADC, NULL },
};

static PCCONNECTION_DESCRIPTOR WaveMiniportConnections[] =
{
    { PCFILTER_NODE,   KSPIN_WAVE_BRIDGE, KSNODE_WAVE_ADC, KSNODEPIN_STANDARD_IN },
    { KSNODE_WAVE_ADC, KSNODEPIN_STANDARD_OUT, PCFILTER_NODE, KSPIN_WAVE_HOST },
};

static PCFILTER_DESCRIPTOR WaveMiniportFilterDescriptor =
{
    0,                                      /* Version */
    NULL,                                   /* AutomationTable */
    sizeof(PCPIN_DESCRIPTOR),
    SIZEOF_ARRAY(WaveMiniportPins),
    WaveMiniportPins,
    sizeof(PCNODE_DESCRIPTOR),
    SIZEOF_ARRAY(WaveMiniportNodes),
    WaveMiniportNodes,
    SIZEOF_ARRAY(WaveMiniportConnections),
    WaveMiniportConnections,
    0,                                      /* CategoryCount: use defaults */
    NULL
};

/* ---- Object lifetime ------------------------------------------------------ */

NTSTATUS CreateMiniportWaveRT(_Out_ PUNKNOWN* Unknown, _In_ REFCLSID, _In_opt_ PUNKNOWN UnknownOuter, _In_ POOL_FLAGS PoolFlags)
{
    PAGED_CODE();
    *Unknown = NULL;
    CMiniportWaveRT* obj = new (PoolFlags, DROIDMIC_POOLTAG) CMiniportWaveRT(UnknownOuter);
    if (!obj) return STATUS_INSUFFICIENT_RESOURCES;
    *Unknown = PUNKNOWN(PMINIPORTWAVERT(obj));
    (*Unknown)->AddRef();
    return STATUS_SUCCESS;
}

CMiniportWaveRT::~CMiniportWaveRT()
{
    PAGED_CODE();
    if (m_Port) m_Port->Release();
}

STDMETHODIMP_(NTSTATUS) CMiniportWaveRT::NonDelegatingQueryInterface(_In_ REFIID Interface, _COM_Outptr_ PVOID* Object)
{
    PAGED_CODE();
    if (IsEqualGUIDAligned(Interface, IID_IUnknown))
        *Object = PVOID(PUNKNOWN(PMINIPORTWAVERT(this)));
    else if (IsEqualGUIDAligned(Interface, IID_IMiniport))
        *Object = PVOID(PMINIPORT(this));
    else if (IsEqualGUIDAligned(Interface, IID_IMiniportWaveRT))
        *Object = PVOID(PMINIPORTWAVERT(this));
    else
    {
        *Object = NULL;
        return STATUS_INVALID_PARAMETER;
    }
    PUNKNOWN(*Object)->AddRef();
    return STATUS_SUCCESS;
}

/* ---- IMiniport ------------------------------------------------------------ */

STDMETHODIMP_(NTSTATUS) CMiniportWaveRT::GetDescription(_Out_ PPCFILTER_DESCRIPTOR* Description)
{
    PAGED_CODE();
    *Description = &WaveMiniportFilterDescriptor;
    return STATUS_SUCCESS;
}

STDMETHODIMP_(NTSTATUS) CMiniportWaveRT::DataRangeIntersection(
    _In_ ULONG, _In_ PKSDATARANGE, _In_ PKSDATARANGE, _In_ ULONG, _Out_writes_bytes_to_opt_(OutputBufferLength, *ResultantFormatLength) PVOID,
    _Out_ PULONG ResultantFormatLength)
{
    PAGED_CODE();
    /* Let PortCls use its default intersection handler. */
    *ResultantFormatLength = 0;
    return STATUS_NOT_IMPLEMENTED;
}

/* ---- IMiniportWaveRT ------------------------------------------------------ */

STDMETHODIMP_(NTSTATUS) CMiniportWaveRT::Init(_In_ PUNKNOWN, _In_ PRESOURCELIST, _In_ PPORTWAVERT Port)
{
    PAGED_CODE();
    m_Port = Port;
    m_Port->AddRef();
    ExInitializeFastMutex(&m_StreamLock);

    m_Format.DataFormat.FormatSize = sizeof(KSDATAFORMAT_WAVEFORMATEXTENSIBLE);
    m_Format.DataFormat.MajorFormat = KSDATAFORMAT_TYPE_AUDIO;
    m_Format.DataFormat.SubFormat = KSDATAFORMAT_SUBTYPE_PCM;
    m_Format.DataFormat.Specifier = KSDATAFORMAT_SPECIFIER_WAVEFORMATEX;
    WAVEFORMATEXTENSIBLE& wfx = m_Format.WaveFormatExt;
    wfx.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
    wfx.Format.nChannels = DROIDMIC_CHANNELS;
    wfx.Format.nSamplesPerSec = DROIDMIC_SAMPLE_RATE;
    wfx.Format.wBitsPerSample = DROIDMIC_BITS_PER_SAMPLE;
    wfx.Format.nBlockAlign = DROIDMIC_CHANNELS * DROIDMIC_BITS_PER_SAMPLE / 8;
    wfx.Format.nAvgBytesPerSec = DROIDMIC_SAMPLE_RATE * wfx.Format.nBlockAlign;
    wfx.Format.cbSize = sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX);
    wfx.Samples.wValidBitsPerSample = DROIDMIC_BITS_PER_SAMPLE;
    wfx.dwChannelMask = KSAUDIO_SPEAKER_MONO;
    wfx.SubFormat = KSDATAFORMAT_SUBTYPE_PCM;
    m_Format.DataFormat.SampleSize = wfx.Format.nBlockAlign;
    return STATUS_SUCCESS;
}

/* Accepts only the single format we advertise (PCM or EXTENSIBLE-PCM 48k/16/mono). */
static bool IsSupportedFormat(_In_ PKSDATAFORMAT DataFormat)
{
    if (!IsEqualGUIDAligned(DataFormat->MajorFormat, KSDATAFORMAT_TYPE_AUDIO) ||
        !IsEqualGUIDAligned(DataFormat->Specifier, KSDATAFORMAT_SPECIFIER_WAVEFORMATEX) ||
        DataFormat->FormatSize < sizeof(KSDATAFORMAT) + sizeof(WAVEFORMATEX))
        return false;

    PWAVEFORMATEX wfx = (PWAVEFORMATEX)(DataFormat + 1);
    if (wfx->wFormatTag == WAVE_FORMAT_EXTENSIBLE)
    {
        if (DataFormat->FormatSize < sizeof(KSDATAFORMAT) + sizeof(WAVEFORMATEXTENSIBLE)) return false;
        PWAVEFORMATEXTENSIBLE ext = (PWAVEFORMATEXTENSIBLE)wfx;
        if (!IsEqualGUIDAligned(ext->SubFormat, KSDATAFORMAT_SUBTYPE_PCM)) return false;
        if (ext->Samples.wValidBitsPerSample != DROIDMIC_BITS_PER_SAMPLE) return false;
    }
    else if (wfx->wFormatTag != WAVE_FORMAT_PCM)
    {
        return false;
    }
    return wfx->nChannels == DROIDMIC_CHANNELS &&
           wfx->nSamplesPerSec == DROIDMIC_SAMPLE_RATE &&
           wfx->wBitsPerSample == DROIDMIC_BITS_PER_SAMPLE &&
           wfx->nBlockAlign == DROIDMIC_CHANNELS * DROIDMIC_BITS_PER_SAMPLE / 8;
}

STDMETHODIMP_(NTSTATUS) CMiniportWaveRT::NewStream(
    _Out_ PMINIPORTWAVERTSTREAM* OutStream,
    _In_ PPORTWAVERTSTREAM PortStream,
    _In_ ULONG Pin,
    _In_ BOOLEAN Capture,
    _In_ PKSDATAFORMAT DataFormat)
{
    PAGED_CODE();
    *OutStream = NULL;
    if (Pin != KSPIN_WAVE_HOST || !Capture) return STATUS_INVALID_PARAMETER;
    if (!IsSupportedFormat(DataFormat)) return STATUS_NO_MATCH;

    ExAcquireFastMutex(&m_StreamLock);
    if (m_StreamCount >= DROIDMIC_MAX_STREAMS)
    {
        ExReleaseFastMutex(&m_StreamLock);
        return STATUS_INSUFFICIENT_RESOURCES;
    }
    m_StreamCount++;
    ExReleaseFastMutex(&m_StreamLock);

    CMiniportWaveRTStream* stream = new (POOL_FLAG_NON_PAGED, DROIDMIC_POOLTAG) CMiniportWaveRTStream(NULL);
    NTSTATUS status = stream ? stream->Init(this, PortStream, DataFormat) : STATUS_INSUFFICIENT_RESOURCES;
    if (!NT_SUCCESS(status))
    {
        if (stream) delete stream; /* never handed out; StreamClosed not called */
        ExAcquireFastMutex(&m_StreamLock);
        m_StreamCount--;
        ExReleaseFastMutex(&m_StreamLock);
        return status;
    }
    stream->AddRef();
    *OutStream = PMINIPORTWAVERTSTREAM(stream);
    return STATUS_SUCCESS;
}

STDMETHODIMP_(NTSTATUS) CMiniportWaveRT::GetDeviceDescription(_Out_ PDEVICE_DESCRIPTION Description)
{
    PAGED_CODE();
    RtlZeroMemory(Description, sizeof(DEVICE_DESCRIPTION));
    Description->Master = TRUE;
    Description->ScatterGather = TRUE;
    Description->Dma32BitAddresses = TRUE;
    Description->InterfaceType = PCIBus;
    Description->MaximumLength = 0xFFFFFFFF;
    return STATUS_SUCCESS;
}

void CMiniportWaveRT::StreamClosed(_In_ CMiniportWaveRTStream*)
{
    PAGED_CODE();
    ExAcquireFastMutex(&m_StreamLock);
    if (m_StreamCount > 0) m_StreamCount--;
    ExReleaseFastMutex(&m_StreamLock);
}
