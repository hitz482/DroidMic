/*
 * Topology miniport. Describes a microphone bridge pin connected straight to the
 * wave filter. The mic pin's Name GUID maps (via the INF's MediaCategories
 * entry) to "DroidMic Microphone", which becomes the endpoint name. Volume and
 * mute are left to the Windows audio engine's software controls.
 */
#include "common.h"

class CMiniportTopology : public IMiniportTopology, public CUnknown
{
public:
    DECLARE_STD_UNKNOWN();
    DEFINE_STD_CONSTRUCTOR(CMiniportTopology);
    ~CMiniportTopology();

    IMP_IMiniportTopology;

private:
    PPORTTOPOLOGY m_Port = nullptr;
};

static KSDATARANGE TopoBridgeDataRanges[] =
{
    {
        sizeof(KSDATARANGE),
        0, 0, 0,
        STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO),
        STATICGUIDOF(KSDATAFORMAT_SUBTYPE_ANALOG),
        STATICGUIDOF(KSDATAFORMAT_SPECIFIER_NONE)
    },
};

static PKSDATARANGE TopoBridgeDataRangePointers[] =
{
    &TopoBridgeDataRanges[0],
};

static PCPIN_DESCRIPTOR TopoMiniportPins[] =
{
    /* KSPIN_TOPO_MIC: the (virtual) microphone */
    {
        0, 0, 0,
        NULL,
        {
            0, NULL,
            0, NULL,
            SIZEOF_ARRAY(TopoBridgeDataRangePointers),
            TopoBridgeDataRangePointers,
            KSPIN_DATAFLOW_IN,
            KSPIN_COMMUNICATION_NONE,
            &KSNODETYPE_MICROPHONE,
            &DROIDMIC_PIN_NAME_GUID,
            0
        }
    },
    /* KSPIN_TOPO_BRIDGE: to the wave filter */
    {
        0, 0, 0,
        NULL,
        {
            0, NULL,
            0, NULL,
            SIZEOF_ARRAY(TopoBridgeDataRangePointers),
            TopoBridgeDataRangePointers,
            KSPIN_DATAFLOW_OUT,
            KSPIN_COMMUNICATION_NONE,
            &KSCATEGORY_AUDIO,
            NULL,
            0
        }
    },
};

static PCCONNECTION_DESCRIPTOR TopoMiniportConnections[] =
{
    { PCFILTER_NODE, KSPIN_TOPO_MIC, PCFILTER_NODE, KSPIN_TOPO_BRIDGE },
};

static PCFILTER_DESCRIPTOR TopoMiniportFilterDescriptor =
{
    0,
    NULL,
    sizeof(PCPIN_DESCRIPTOR),
    SIZEOF_ARRAY(TopoMiniportPins),
    TopoMiniportPins,
    sizeof(PCNODE_DESCRIPTOR),
    0,
    NULL,
    SIZEOF_ARRAY(TopoMiniportConnections),
    TopoMiniportConnections,
    0,
    NULL
};

NTSTATUS CreateMiniportTopology(_Out_ PUNKNOWN* Unknown, _In_ REFCLSID, _In_opt_ PUNKNOWN UnknownOuter, _In_ POOL_FLAGS PoolFlags)
{
    PAGED_CODE();
    *Unknown = NULL;
    CMiniportTopology* obj = new (PoolFlags, DROIDMIC_POOLTAG) CMiniportTopology(UnknownOuter);
    if (!obj) return STATUS_INSUFFICIENT_RESOURCES;
    *Unknown = PUNKNOWN(PMINIPORTTOPOLOGY(obj));
    (*Unknown)->AddRef();
    return STATUS_SUCCESS;
}

CMiniportTopology::~CMiniportTopology()
{
    PAGED_CODE();
    if (m_Port) m_Port->Release();
}

STDMETHODIMP_(NTSTATUS) CMiniportTopology::NonDelegatingQueryInterface(_In_ REFIID Interface, _COM_Outptr_ PVOID* Object)
{
    PAGED_CODE();
    if (IsEqualGUIDAligned(Interface, IID_IUnknown))
        *Object = PVOID(PUNKNOWN(PMINIPORTTOPOLOGY(this)));
    else if (IsEqualGUIDAligned(Interface, IID_IMiniport))
        *Object = PVOID(PMINIPORT(this));
    else if (IsEqualGUIDAligned(Interface, IID_IMiniportTopology))
        *Object = PVOID(PMINIPORTTOPOLOGY(this));
    else
    {
        *Object = NULL;
        return STATUS_INVALID_PARAMETER;
    }
    PUNKNOWN(*Object)->AddRef();
    return STATUS_SUCCESS;
}

STDMETHODIMP_(NTSTATUS) CMiniportTopology::GetDescription(_Out_ PPCFILTER_DESCRIPTOR* Description)
{
    PAGED_CODE();
    *Description = &TopoMiniportFilterDescriptor;
    return STATUS_SUCCESS;
}

STDMETHODIMP_(NTSTATUS) CMiniportTopology::DataRangeIntersection(
    _In_ ULONG, _In_ PKSDATARANGE, _In_ PKSDATARANGE, _In_ ULONG, _Out_writes_bytes_to_opt_(OutputBufferLength, *ResultantFormatLength) PVOID,
    _Out_ PULONG ResultantFormatLength)
{
    PAGED_CODE();
    *ResultantFormatLength = 0;
    return STATUS_NOT_IMPLEMENTED;
}

STDMETHODIMP_(NTSTATUS) CMiniportTopology::Init(_In_ PUNKNOWN, _In_ PRESOURCELIST, _In_ PPORTTOPOLOGY Port)
{
    PAGED_CODE();
    m_Port = Port;
    m_Port->AddRef();
    return STATUS_SUCCESS;
}
