using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using DroidMic.Platform;
using Xunit;

namespace DroidMic.Tests;

/// <summary>Checks the C# mirror (DriverInterface.cs) against driver/VirtualAudioDevice/droidmic_ioctl.h.</summary>
public class DriverContractTests
{
    private static readonly string Header = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "droidmic_ioctl.h"));

    private static long Define(string name)
    {
        var m = Regex.Match(Header, @"#define\s+" + name + @"\s+(0x[0-9A-Fa-f]+|\d+)");
        Assert.True(m.Success, name);
        string v = m.Groups[1].Value;
        return v.StartsWith("0x") ? Convert.ToInt64(v, 16) : long.Parse(v);
    }

    private static uint CtlCode(string ioctl)
    {
        var m = Regex.Match(Header, @"#define\s+" + ioctl + @"\s*\\\s*CTL_CODE\(FILE_DEVICE_DROIDMIC,\s*(0x[0-9A-Fa-f]+),\s*METHOD_BUFFERED,\s*(FILE_READ_ACCESS|FILE_WRITE_ACCESS)\)");
        Assert.True(m.Success, ioctl);
        uint fn = Convert.ToUInt32(m.Groups[1].Value, 16);
        uint access = m.Groups[2].Value == "FILE_READ_ACCESS" ? 1u : 2u;
        return ((uint)Define("FILE_DEVICE_DROIDMIC") << 16) | (access << 14) | (fn << 2);
    }

    [Fact]
    public void IoctlCodesMatchHeader()
    {
        Assert.Equal(CtlCode("IOCTL_DROIDMIC_GET_STATUS"), DriverInterface.IoctlGetStatus);
        Assert.Equal(CtlCode("IOCTL_DROIDMIC_WRITE_AUDIO"), DriverInterface.IoctlWriteAudio);
        Assert.Equal(CtlCode("IOCTL_DROIDMIC_RESET"), DriverInterface.IoctlReset);
    }

    [Fact]
    public void ConstantsMatchHeader()
    {
        Assert.Equal(DriverInterface.InterfaceVersion, Define("DROIDMIC_INTERFACE_VERSION"));
        Assert.Equal(DriverInterface.SampleRate, Define("DROIDMIC_SAMPLE_RATE"));
        Assert.Equal(DriverInterface.Channels, Define("DROIDMIC_CHANNELS"));
        Assert.Equal(DriverInterface.MaxWriteSamples, Define("DROIDMIC_MAX_WRITE_SAMPLES"));
    }

    [Fact]
    public void InterfaceGuidMatchesHeader()
    {
        var m = Regex.Match(Header, @"DEFINE_GUID\(GUID_DEVINTERFACE_DROIDMIC_CONTROL,\s*([^)]*)\)");
        Assert.True(m.Success);
        var parts = m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Convert.ToUInt64(p, 16)).ToArray();
        var guid = new Guid((uint)parts[0], (ushort)parts[1], (ushort)parts[2],
            (byte)parts[3], (byte)parts[4], (byte)parts[5], (byte)parts[6], (byte)parts[7], (byte)parts[8], (byte)parts[9], (byte)parts[10]);
        Assert.Equal(guid, DriverInterface.ControlInterfaceGuid);
    }

    [Fact]
    public void StructLayoutsMatchPack4CLayout()
    {
        // 4 × ULONG
        Assert.Equal(16, Marshal.SizeOf<DriverAudioHeader>());
        // 6 × ULONG + 4 × ULONGLONG with #pragma pack(4)
        Assert.Equal(56, Marshal.SizeOf<DriverStatus>());
        Assert.Equal(24, Marshal.OffsetOf<DriverStatus>(nameof(DriverStatus.SamplesWritten)).ToInt32());
    }
}
