/*
 * DroidMic virtual audio device: user-mode <-> driver control interface.
 *
 * Shared by the driver and documentation. The C# mirror lives in
 * windows/DroidMic.Platform/DriverInterface.cs; keep the two in sync.
 */
#pragma once

#ifdef _KERNEL_MODE
#include <ntddk.h>
#else
#include <windows.h>
#include <winioctl.h>
#endif

/* Device interface the desktop app opens with CreateFile.
 * {681D599C-9CF0-4111-AB7C-073E928CC33D} */
DEFINE_GUID(GUID_DEVINTERFACE_DROIDMIC_CONTROL,
    0x681d599c, 0x9cf0, 0x4111, 0xab, 0x7c, 0x07, 0x3e, 0x92, 0x8c, 0xc3, 0x3d);

/* Name GUID of the microphone pin; the INF maps it to "DroidMic Microphone"
 * under MediaCategories so the endpoint gets that name.
 * {0244DB71-043B-4997-8E78-0662A5D13280} */
DEFINE_GUID(DROIDMIC_PIN_NAME_GUID,
    0x0244db71, 0x043b, 0x4997, 0x8e, 0x78, 0x06, 0x62, 0xa5, 0xd1, 0x32, 0x80);

#define DROIDMIC_CONTROL_REFERENCE_STRING L"DroidMicControl"

#define FILE_DEVICE_DROIDMIC 0x8D1C

/* Output: DROIDMIC_STATUS */
#define IOCTL_DROIDMIC_GET_STATUS \
    CTL_CODE(FILE_DEVICE_DROIDMIC, 0x800, METHOD_BUFFERED, FILE_READ_ACCESS)

/* Input: DROIDMIC_AUDIO_HEADER followed by SampleCount int16 samples.
 * Output (optional): DROIDMIC_STATUS after the write. */
#define IOCTL_DROIDMIC_WRITE_AUDIO \
    CTL_CODE(FILE_DEVICE_DROIDMIC, 0x801, METHOD_BUFFERED, FILE_WRITE_ACCESS)

/* Discards buffered audio. No input/output. */
#define IOCTL_DROIDMIC_RESET \
    CTL_CODE(FILE_DEVICE_DROIDMIC, 0x802, METHOD_BUFFERED, FILE_WRITE_ACCESS)

#define DROIDMIC_INTERFACE_VERSION 1
#define DROIDMIC_SAMPLE_RATE 48000
#define DROIDMIC_CHANNELS 1
#define DROIDMIC_BITS_PER_SAMPLE 16

/* Ring capacity: 1 s. The desktop app keeps it ~15 ms full. */
#define DROIDMIC_RING_SAMPLES 48000
/* While no recording client is running, keep at most this much audio so a new
 * client starts with fresh audio instead of stale buffered audio. */
#define DROIDMIC_IDLE_KEEP_SAMPLES 960
/* Largest single write (100 ms). */
#define DROIDMIC_MAX_WRITE_SAMPLES 4800

#pragma pack(push, 4)
typedef struct _DROIDMIC_AUDIO_HEADER {
    unsigned long Version;      /* DROIDMIC_INTERFACE_VERSION */
    unsigned long SampleRate;   /* must be DROIDMIC_SAMPLE_RATE */
    unsigned long Channels;     /* must be DROIDMIC_CHANNELS */
    unsigned long SampleCount;  /* samples that follow, <= DROIDMIC_MAX_WRITE_SAMPLES */
} DROIDMIC_AUDIO_HEADER;

typedef struct _DROIDMIC_STATUS {
    unsigned long Version;
    unsigned long SampleRate;
    unsigned long Channels;
    unsigned long RingCapacitySamples;
    unsigned long RingFillSamples;
    unsigned long ActiveCaptureStreams;  /* recording clients currently running */
    unsigned long long SamplesWritten;   /* by the desktop app */
    unsigned long long SamplesRead;      /* by capture streams */
    unsigned long long UnderrunSamples;  /* silence inserted while a client was running */
    unsigned long long OverflowSamples;  /* dropped because the ring was full */
} DROIDMIC_STATUS;
#pragma pack(pop)
