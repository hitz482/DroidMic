#pragma once
#include "common.h"

class CMiniportWaveRTStream;

/* WaveRT miniport for the DroidMic capture filter. */
class CMiniportWaveRT : public IMiniportWaveRT, public CUnknown
{
public:
    DECLARE_STD_UNKNOWN();
    DEFINE_STD_CONSTRUCTOR(CMiniportWaveRT);
    ~CMiniportWaveRT();

    IMP_IMiniportWaveRT;

    /* Called by streams when they are destroyed. */
    void StreamClosed(_In_ CMiniportWaveRTStream* stream);

    const KSDATAFORMAT_WAVEFORMATEXTENSIBLE* DefaultFormat() const { return &m_Format; }

private:
    PPORTWAVERT m_Port = nullptr;
    KSDATAFORMAT_WAVEFORMATEXTENSIBLE m_Format{};
    FAST_MUTEX m_StreamLock;
    ULONG m_StreamCount = 0;
};
