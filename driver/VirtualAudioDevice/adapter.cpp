/*
 * DroidMic Virtual Audio Device - adapter.
 *
 * Root-enumerated PortCls adapter with two subdevices (WaveRT capture filter and
 * topology filter), plus a private device interface
 * (GUID_DEVINTERFACE_DROIDMIC_CONTROL) through which the DroidMic desktop app
 * delivers audio. PortCls owns the device object's dispatch table; we wrap
 * CREATE/CLEANUP/CLOSE/DEVICE_CONTROL/PNP and forward everything that is not
 * ours to PcDispatchIrp.
 */
#include "common.h"

#define MAX_MINIPORTS 2
#define MAX_CONTROL_HANDLES 8

typedef struct _DROIDMIC_EXTENSION {
    UNICODE_STRING ControlSymlink;       /* from IoRegisterDeviceInterface */
    FAST_MUTEX     HandleLock;
    PFILE_OBJECT   ControlHandles[MAX_CONTROL_HANDLES];
} DROIDMIC_EXTENSION, *PDROIDMIC_EXTENSION;

static PDRIVER_UNLOAD gPortClsUnload = NULL;

extern "C" DRIVER_INITIALIZE DriverEntry;
static DRIVER_ADD_DEVICE AddDevice;
static DRIVER_UNLOAD DriverUnload;
static DRIVER_DISPATCH DispatchCreate;
static DRIVER_DISPATCH DispatchCleanupClose;
static DRIVER_DISPATCH DispatchDeviceControl;
static DRIVER_DISPATCH DispatchPnp;

static PDROIDMIC_EXTENSION Ext(PDEVICE_OBJECT DeviceObject)
{
    return (PDROIDMIC_EXTENSION)((PCHAR)DeviceObject->DeviceExtension + PORT_CLASS_DEVICE_EXTENSION_SIZE);
}

/* ---- Subdevice creation ---------------------------------------------------- */

static NTSTATUS InstallSubdevice(
    _In_ PDEVICE_OBJECT DeviceObject, _In_ PIRP Irp, _In_ PCWSTR Name, _In_ REFGUID PortClassId,
    _In_ NTSTATUS (*CreateMiniport)(PUNKNOWN*, REFCLSID, PUNKNOWN, POOL_FLAGS),
    _In_ PRESOURCELIST ResourceList, _Out_ PUNKNOWN* OutPort)
{
    PAGED_CODE();
    PPORT port = NULL;
    PUNKNOWN miniport = NULL;
    *OutPort = NULL;

    NTSTATUS status = PcNewPort(&port, PortClassId);
    if (NT_SUCCESS(status)) status = CreateMiniport(&miniport, GUID_NULL, NULL, POOL_FLAG_NON_PAGED);
    if (NT_SUCCESS(status)) status = port->Init(DeviceObject, Irp, miniport, NULL, ResourceList);
    if (NT_SUCCESS(status)) status = PcRegisterSubdevice(DeviceObject, Name, port);

    if (miniport) miniport->Release();
    if (NT_SUCCESS(status))
    {
        *OutPort = port;  /* caller releases */
    }
    else
    {
        DMLOG(DM_ERROR, "InstallSubdevice %ls failed 0x%08X", Name, status);
        if (port) port->Release();
    }
    return status;
}

static NTSTATUS StartDevice(_In_ PDEVICE_OBJECT DeviceObject, _In_ PIRP Irp, _In_ PRESOURCELIST ResourceList)
{
    PAGED_CODE();
    PUNKNOWN topoPort = NULL, wavePort = NULL;

    NTSTATUS status = InstallSubdevice(DeviceObject, Irp, L"TopologyDroidMic", CLSID_PortTopology,
                                       CreateMiniportTopology, ResourceList, &topoPort);
    if (NT_SUCCESS(status))
        status = InstallSubdevice(DeviceObject, Irp, L"WaveDroidMic", CLSID_PortWaveRT,
                                  CreateMiniportWaveRT, ResourceList, &wavePort);
    if (NT_SUCCESS(status))
        status = PcRegisterPhysicalConnection(DeviceObject, topoPort, KSPIN_TOPO_BRIDGE, wavePort, KSPIN_WAVE_BRIDGE);

    if (topoPort) topoPort->Release();
    if (wavePort) wavePort->Release();
    if (!NT_SUCCESS(status)) return status;

    /* Expose the control interface used by the desktop app. */
    PDROIDMIC_EXTENSION ext = Ext(DeviceObject);
    if (ext->ControlSymlink.Buffer == NULL)
    {
        PDEVICE_OBJECT pdo = NULL;
        status = PcGetPhysicalDeviceObject(DeviceObject, &pdo);
        if (NT_SUCCESS(status))
        {
            UNICODE_STRING ref;
            RtlInitUnicodeString(&ref, DROIDMIC_CONTROL_REFERENCE_STRING);
            status = IoRegisterDeviceInterface(pdo, &GUID_DEVINTERFACE_DROIDMIC_CONTROL, &ref, &ext->ControlSymlink);
        }
        if (!NT_SUCCESS(status))
        {
            DMLOG(DM_ERROR, "IoRegisterDeviceInterface failed 0x%08X", status);
            return status;
        }
    }
    status = IoSetDeviceInterfaceState(&ext->ControlSymlink, TRUE);
    DMLOG(DM_INFO, "Started (control interface %wZ, 0x%08X)", &ext->ControlSymlink, status);
    return status;
}

/* ---- Driver lifetime -------------------------------------------------------- */

extern "C" NTSTATUS DriverEntry(_In_ PDRIVER_OBJECT DriverObject, _In_ PUNICODE_STRING RegistryPath)
{
    DroidMicSourceInitialize();

    NTSTATUS status = PcInitializeAdapterDriver(DriverObject, RegistryPath, (PDRIVER_ADD_DEVICE)AddDevice);
    if (!NT_SUCCESS(status))
    {
        DMLOG(DM_ERROR, "PcInitializeAdapterDriver failed 0x%08X", status);
        return status;
    }

    DriverObject->MajorFunction[IRP_MJ_CREATE] = DispatchCreate;
    DriverObject->MajorFunction[IRP_MJ_CLEANUP] = DispatchCleanupClose;
    DriverObject->MajorFunction[IRP_MJ_CLOSE] = DispatchCleanupClose;
    DriverObject->MajorFunction[IRP_MJ_DEVICE_CONTROL] = DispatchDeviceControl;
    DriverObject->MajorFunction[IRP_MJ_PNP] = DispatchPnp;

    gPortClsUnload = DriverObject->DriverUnload;
    DriverObject->DriverUnload = DriverUnload;
    return STATUS_SUCCESS;
}

static void DriverUnload(_In_ PDRIVER_OBJECT DriverObject)
{
    PAGED_CODE();
    if (gPortClsUnload) gPortClsUnload(DriverObject);
}

static NTSTATUS AddDevice(_In_ PDRIVER_OBJECT DriverObject, _In_ PDEVICE_OBJECT PhysicalDeviceObject)
{
    PAGED_CODE();
    NTSTATUS status = PcAddAdapterDevice(DriverObject, PhysicalDeviceObject, PCPFNSTARTDEVICE(StartDevice),
                                         MAX_MINIPORTS, PORT_CLASS_DEVICE_EXTENSION_SIZE + sizeof(DROIDMIC_EXTENSION));
    if (!NT_SUCCESS(status)) return status;

    /* PcAddAdapterDevice attached our FDO on top of the PDO; find it to init our extension. */
    PDEVICE_OBJECT fdo = DriverObject->DeviceObject;
    while (fdo)
    {
        PDEVICE_OBJECT lower = IoGetLowerDeviceObject(fdo);  /* referenced */
        bool match = lower == PhysicalDeviceObject;
        if (lower) ObDereferenceObject(lower);
        if (match) break;
        fdo = fdo->NextDevice;
    }
    if (fdo)
    {
        PDROIDMIC_EXTENSION ext = Ext(fdo);
        RtlZeroMemory(ext, sizeof(*ext));
        ExInitializeFastMutex(&ext->HandleLock);
    }
    return STATUS_SUCCESS;
}

/* ---- Control interface -------------------------------------------------------- */

static bool IsControlHandle(PDROIDMIC_EXTENSION ext, PFILE_OBJECT fo, bool remove)
{
    bool found = false;
    if (!fo) return false;
    ExAcquireFastMutex(&ext->HandleLock);
    for (ULONG i = 0; i < MAX_CONTROL_HANDLES; i++)
    {
        if (ext->ControlHandles[i] == fo)
        {
            found = true;
            if (remove) ext->ControlHandles[i] = NULL;
            break;
        }
    }
    ExReleaseFastMutex(&ext->HandleLock);
    return found;
}

static NTSTATUS CompleteIrp(PIRP Irp, NTSTATUS status, ULONG_PTR info)
{
    Irp->IoStatus.Status = status;
    Irp->IoStatus.Information = info;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
    return status;
}

static NTSTATUS DispatchCreate(_In_ PDEVICE_OBJECT DeviceObject, _Inout_ PIRP Irp)
{
    PAGED_CODE();
    PIO_STACK_LOCATION stack = IoGetCurrentIrpStackLocation(Irp);
    PFILE_OBJECT fo = stack->FileObject;

    /* The I/O manager puts "\<reference string>" in FileName for interface opens. */
    UNICODE_STRING expected;
    RtlInitUnicodeString(&expected, L"\\" DROIDMIC_CONTROL_REFERENCE_STRING);
    if (!fo || !RtlEqualUnicodeString(&fo->FileName, &expected, TRUE))
    {
        return PcDispatchIrp(DeviceObject, Irp);
    }

    PDROIDMIC_EXTENSION ext = Ext(DeviceObject);
    NTSTATUS status = STATUS_TOO_MANY_OPENED_FILES;
    ExAcquireFastMutex(&ext->HandleLock);
    for (ULONG i = 0; i < MAX_CONTROL_HANDLES; i++)
    {
        if (!ext->ControlHandles[i])
        {
            ext->ControlHandles[i] = fo;
            status = STATUS_SUCCESS;
            break;
        }
    }
    ExReleaseFastMutex(&ext->HandleLock);
    return CompleteIrp(Irp, status, 0);
}

static NTSTATUS DispatchCleanupClose(_In_ PDEVICE_OBJECT DeviceObject, _Inout_ PIRP Irp)
{
    PAGED_CODE();
    PIO_STACK_LOCATION stack = IoGetCurrentIrpStackLocation(Irp);
    PDROIDMIC_EXTENSION ext = Ext(DeviceObject);
    bool isClose = stack->MajorFunction == IRP_MJ_CLOSE;
    if (!IsControlHandle(ext, stack->FileObject, isClose))
    {
        return PcDispatchIrp(DeviceObject, Irp);
    }
    if (!isClose)
    {
        /* The feeding app went away (normally or by crashing): drop its buffered
         * audio so recording clients get silence, not a stale loop. */
        DroidMicSourceReset();
    }
    return CompleteIrp(Irp, STATUS_SUCCESS, 0);
}

static NTSTATUS DispatchDeviceControl(_In_ PDEVICE_OBJECT DeviceObject, _Inout_ PIRP Irp)
{
    PIO_STACK_LOCATION stack = IoGetCurrentIrpStackLocation(Irp);
    if (!IsControlHandle(Ext(DeviceObject), stack->FileObject, false))
    {
        return PcDispatchIrp(DeviceObject, Irp);
    }

    ULONG code = stack->Parameters.DeviceIoControl.IoControlCode;
    ULONG inLen = stack->Parameters.DeviceIoControl.InputBufferLength;
    ULONG outLen = stack->Parameters.DeviceIoControl.OutputBufferLength;
    PVOID buffer = Irp->AssociatedIrp.SystemBuffer;  /* METHOD_BUFFERED */

    switch (code)
    {
    case IOCTL_DROIDMIC_GET_STATUS:
        if (outLen < sizeof(DROIDMIC_STATUS) || !buffer) return CompleteIrp(Irp, STATUS_BUFFER_TOO_SMALL, 0);
        DroidMicSourceGetStatus((DROIDMIC_STATUS*)buffer);
        return CompleteIrp(Irp, STATUS_SUCCESS, sizeof(DROIDMIC_STATUS));

    case IOCTL_DROIDMIC_WRITE_AUDIO:
    {
        if (inLen < sizeof(DROIDMIC_AUDIO_HEADER) || !buffer) return CompleteIrp(Irp, STATUS_INVALID_PARAMETER, 0);
        DROIDMIC_AUDIO_HEADER header = *(DROIDMIC_AUDIO_HEADER*)buffer;  /* copy before the buffer is reused for output */
        if (header.Version != DROIDMIC_INTERFACE_VERSION ||
            header.SampleRate != DROIDMIC_SAMPLE_RATE ||
            header.Channels != DROIDMIC_CHANNELS ||
            header.SampleCount == 0 ||
            header.SampleCount > DROIDMIC_MAX_WRITE_SAMPLES ||
            inLen != sizeof(DROIDMIC_AUDIO_HEADER) + header.SampleCount * sizeof(SHORT))
        {
            return CompleteIrp(Irp, STATUS_INVALID_PARAMETER, 0);
        }
        DroidMicSourceWrite((const SHORT*)((PUCHAR)buffer + sizeof(DROIDMIC_AUDIO_HEADER)), header.SampleCount);
        if (outLen >= sizeof(DROIDMIC_STATUS))
        {
            DroidMicSourceGetStatus((DROIDMIC_STATUS*)buffer);
            return CompleteIrp(Irp, STATUS_SUCCESS, sizeof(DROIDMIC_STATUS));
        }
        return CompleteIrp(Irp, STATUS_SUCCESS, 0);
    }

    case IOCTL_DROIDMIC_RESET:
        DroidMicSourceReset();
        return CompleteIrp(Irp, STATUS_SUCCESS, 0);

    default:
        return CompleteIrp(Irp, STATUS_INVALID_DEVICE_REQUEST, 0);
    }
}

static NTSTATUS DispatchPnp(_In_ PDEVICE_OBJECT DeviceObject, _Inout_ PIRP Irp)
{
    PIO_STACK_LOCATION stack = IoGetCurrentIrpStackLocation(Irp);
    PDROIDMIC_EXTENSION ext = Ext(DeviceObject);
    UCHAR minor = stack->MinorFunction;

    if ((minor == IRP_MN_STOP_DEVICE || minor == IRP_MN_SURPRISE_REMOVAL || minor == IRP_MN_REMOVE_DEVICE) &&
        ext->ControlSymlink.Buffer)
    {
        IoSetDeviceInterfaceState(&ext->ControlSymlink, FALSE);
        DroidMicSourceReset();
    }

    if (minor == IRP_MN_REMOVE_DEVICE && ext->ControlSymlink.Buffer)
    {
        /* Free the symlink name before PortCls deletes the device object (and our extension). */
        RtlFreeUnicodeString(&ext->ControlSymlink);
        ext->ControlSymlink.Buffer = NULL;
    }
    return PcDispatchIrp(DeviceObject, Irp);
}
