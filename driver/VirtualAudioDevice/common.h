/*
 * DroidMic Virtual Audio Device - shared driver definitions.
 *
 * Architecture follows Microsoft's SysVAD / SimpleAudioSample (PortCls + WaveRT):
 *   adapter.cpp        DriverEntry, AddDevice, StartDevice, control-interface IRPs
 *   minwavert.cpp      WaveRT miniport (capture filter)
 *   minwavertstream.cpp WaveRT capture stream, timer-driven position, fills DMA buffer
 *   mintopo.cpp        Topology miniport (microphone bridge pin)
 *   audiosource.cpp    Ring buffer shared between the control IOCTLs and capture streams
 */
#pragma once

#include <portcls.h>
#include <stdunk.h>
#include <ksdebug.h>
#include <ntstrsafe.h>
/* GUIDs from droidmic_ioctl.h are instantiated once, in guids.cpp; KS GUIDs come from ksguid.lib. */
#include "droidmic_ioctl.h"

#define DROIDMIC_POOLTAG 'cMdD'

/* MSVC drops the trailing comma when __VA_ARGS__ is empty. */
#define DMLOG(level, fmt, ...) KdPrintEx((DPFLTR_IHVAUDIO_ID, level, "DroidMic: " fmt "\n", __VA_ARGS__))
#define DM_ERROR DPFLTR_ERROR_LEVEL
#define DM_INFO  DPFLTR_INFO_LEVEL

/* Pin and node ids. */
enum {
    KSPIN_WAVE_HOST = 0,    /* capture streaming pin (filter output) */
    KSPIN_WAVE_BRIDGE = 1,  /* bridge from topology */
};
enum { KSNODE_WAVE_ADC = 0 };
enum {
    KSPIN_TOPO_MIC = 0,     /* physical microphone (bridge, input) */
    KSPIN_TOPO_BRIDGE = 1,  /* to the wave filter */
};

/* One pin instance: shared-mode clients all share the audio engine's single
 * stream, and two streams would otherwise split the ring's samples between them. */
#define DROIDMIC_MAX_STREAMS 1

/* ---- Audio source (audiosource.cpp) ------------------------------------- */
void DroidMicSourceInitialize();
/* Producer side (IRQL <= DISPATCH_LEVEL). */
void DroidMicSourceWrite(_In_reads_(count) const SHORT* samples, ULONG count);
void DroidMicSourceReset();
void DroidMicSourceGetStatus(_Out_ DROIDMIC_STATUS* status);
/* Consumer side (called from capture streams at DISPATCH_LEVEL). */
void DroidMicSourceRead(_Out_writes_(count) SHORT* samples, ULONG count);
void DroidMicSourceStreamStarted();
void DroidMicSourceStreamStopped();

/* ---- Miniport factories ------------------------------------------------- */
NTSTATUS CreateMiniportWaveRT(_Out_ PUNKNOWN* Unknown, _In_ REFCLSID, _In_opt_ PUNKNOWN UnknownOuter, _In_ POOL_FLAGS PoolFlags);
NTSTATUS CreateMiniportTopology(_Out_ PUNKNOWN* Unknown, _In_ REFCLSID, _In_opt_ PUNKNOWN UnknownOuter, _In_ POOL_FLAGS PoolFlags);

/* ---- Kernel C++ allocation ----------------------------------------------- */
PVOID operator new(size_t size, POOL_FLAGS poolFlags, ULONG tag);
PVOID operator new(size_t size, POOL_FLAGS poolFlags);
void __cdecl operator delete(PVOID p);
void __cdecl operator delete(PVOID p, size_t);
void __cdecl operator delete[](PVOID p);
