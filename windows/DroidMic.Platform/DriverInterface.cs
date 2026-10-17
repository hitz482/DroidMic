using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DroidMic.Platform;

/// <summary>
/// Mirror of driver/VirtualAudioDevice/droidmic_ioctl.h. Keep in sync.
/// </summary>
public static class DriverInterface
{
    public static readonly Guid ControlInterfaceGuid = new("681d599c-9cf0-4111-ab7c-073e928cc33d");
    public const string HardwareId = @"Root\DroidMicVAD";
    public const string EndpointName = "DroidMic Microphone";

    public const int InterfaceVersion = 1;
    public const int SampleRate = 48000;
    public const int Channels = 1;
    public const int MaxWriteSamples = 4800;

    private const uint FileDeviceDroidMic = 0x8D1C;
    private const uint MethodBuffered = 0;
    private const uint FileReadAccess = 1;
    private const uint FileWriteAccess = 2;

    private static uint CtlCode(uint function, uint access) =>
        (FileDeviceDroidMic << 16) | (access << 14) | (function << 2) | MethodBuffered;

    public static readonly uint IoctlGetStatus = CtlCode(0x800, FileReadAccess);
    public static readonly uint IoctlWriteAudio = CtlCode(0x801, FileWriteAccess);
    public static readonly uint IoctlReset = CtlCode(0x802, FileWriteAccess);
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct DriverAudioHeader
{
    public uint Version;
    public uint SampleRate;
    public uint Channels;
    public uint SampleCount;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct DriverStatus
{
    public uint Version;
    public uint SampleRate;
    public uint Channels;
    public uint RingCapacitySamples;
    public uint RingFillSamples;
    public uint ActiveCaptureStreams;
    public ulong SamplesWritten;
    public ulong SamplesRead;
    public ulong UnderrunSamples;
    public ulong OverflowSamples;
}

/// <summary>
/// Handle to the DroidMic driver's control interface. Writes go straight into the
/// driver's ring buffer, which feeds the "DroidMic Microphone" capture endpoint.
/// </summary>
public sealed class DriverClient : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly byte[] _writeBuffer;

    private DriverClient(SafeFileHandle handle)
    {
        _handle = handle;
        _writeBuffer = new byte[Marshal.SizeOf<DriverAudioHeader>() + DriverInterface.MaxWriteSamples * 2];
    }

    /// <summary>Paths of present DroidMic control interfaces (empty if the driver isn't installed/started).</summary>
    public static IReadOnlyList<string> FindInterfaces()
    {
        var guid = DriverInterface.ControlInterfaceGuid;
        int cr = Native.CM_Get_Device_Interface_List_Size(out uint len, ref guid, null, Native.CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
        if (cr != 0 || len <= 1) return Array.Empty<string>();
        char[] buf = new char[len];
        cr = Native.CM_Get_Device_Interface_List(ref guid, null, buf, len, Native.CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
        if (cr != 0) return Array.Empty<string>();
        return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Opens the first present interface, or returns null if none.</summary>
    public static DriverClient? TryOpen(out string? error)
    {
        error = null;
        var paths = FindInterfaces();
        if (paths.Count == 0)
        {
            error = "The DroidMic virtual audio driver is not installed or not running.";
            return null;
        }
        var h = Native.CreateFile(paths[0], Native.GENERIC_READ | Native.GENERIC_WRITE, 0, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            error = $"Could not open the DroidMic driver: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
            h.Dispose();
            return null;
        }
        return new DriverClient(h);
    }

    public unsafe DriverStatus GetStatus()
    {
        DriverStatus status;
        if (!Native.DeviceIoControl(_handle, DriverInterface.IoctlGetStatus, null, 0, &status, (uint)sizeof(DriverStatus), out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return status;
    }

    /// <summary>Writes 48 kHz mono samples; returns the driver status after the write.</summary>
    public unsafe DriverStatus Write(ReadOnlySpan<short> samples)
    {
        if (samples.Length is 0 or > DriverInterface.MaxWriteSamples) throw new ArgumentOutOfRangeException(nameof(samples));
        var header = new DriverAudioHeader
        {
            Version = DriverInterface.InterfaceVersion,
            SampleRate = DriverInterface.SampleRate,
            Channels = DriverInterface.Channels,
            SampleCount = (uint)samples.Length,
        };
        int headerSize = sizeof(DriverAudioHeader);
        MemoryMarshal.Write(_writeBuffer, in header);
        MemoryMarshal.AsBytes(samples).CopyTo(_writeBuffer.AsSpan(headerSize));
        DriverStatus status;
        fixed (byte* p = _writeBuffer)
        {
            if (!Native.DeviceIoControl(_handle, DriverInterface.IoctlWriteAudio, p, (uint)(headerSize + samples.Length * 2),
                    &status, (uint)sizeof(DriverStatus), out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return status;
    }

    public unsafe void Reset()
    {
        if (!Native.DeviceIoControl(_handle, DriverInterface.IoctlReset, null, 0, null, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose() => _handle.Dispose();
}

internal static unsafe partial class Native
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint OPEN_EXISTING = 3;
    public const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle device, uint code, void* inBuf, uint inSize, void* outBuf, uint outSize, out uint returned, IntPtr overlapped);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_Interface_List_Size(out uint len, ref Guid interfaceClass, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_Interface_List(ref Guid interfaceClass, string? deviceId, [Out] char[] buffer, uint len, uint flags);
}
