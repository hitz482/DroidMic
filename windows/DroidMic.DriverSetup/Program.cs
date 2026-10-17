using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// Installs/removes the DroidMic virtual audio device (a root-enumerated device node
// with hardware id Root\DroidMicVAD) using documented SetupAPI/newdev calls.
// Equivalent to `devcon install DroidMicVAD.inf Root\DroidMicVAD`.
//
//   DroidMic.DriverSetup install <path\to\DroidMicVAD.inf>
//   DroidMic.DriverSetup uninstall
//   DroidMic.DriverSetup status
//
// Exit codes: 0 ok, 1 error, 2 usage, 3 ok but reboot required.

const string HardwareId = @"Root\DroidMicVAD";
var mediaClass = new Guid("4d36e96c-e325-11ce-bfc1-08002be10318");

try
{
    return args switch
    {
        ["install", var inf] => Install(Path.GetFullPath(inf)),
        ["uninstall"] => Uninstall(),
        ["status"] => Status(),
        _ => Usage(),
    };
}
catch (Win32Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message} (0x{ex.NativeErrorCode:X8})");
    if (ex.NativeErrorCode == unchecked((int)0xE0000247)) // ERROR_DRIVER_STORE_ADD_FAILED / unsigned
        Console.Error.WriteLine("The driver package signature was rejected. See docs/DRIVER_INSTALLATION.md (test signing vs. release signing).");
    return 1;
}

int Usage()
{
    Console.Error.WriteLine("usage: DroidMic.DriverSetup install <DroidMicVAD.inf> | uninstall | status");
    return 2;
}

int Install(string infPath)
{
    if (!File.Exists(infPath)) throw new FileNotFoundException("INF not found", infPath);

    if (FindDevices().Count == 0)
    {
        Console.WriteLine("Creating device node Root\\DroidMicVAD ...");
        CreateRootDevice();
    }
    else
    {
        Console.WriteLine("Device node already exists; updating driver ...");
    }

    if (!SetupApi.UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, HardwareId, infPath, SetupApi.INSTALLFLAG_FORCE, out bool reboot))
        throw new Win32Exception(Marshal.GetLastWin32Error());

    Console.WriteLine("DroidMic virtual audio device installed.");
    if (reboot)
    {
        Console.WriteLine("Windows reports that a restart is required to finish.");
        return 3;
    }
    return 0;
}

void CreateRootDevice()
{
    var g = mediaClass;
    IntPtr set = SetupApi.SetupDiCreateDeviceInfoList(ref g, IntPtr.Zero);
    if (set == SetupApi.INVALID_HANDLE_VALUE) throw new Win32Exception(Marshal.GetLastWin32Error());
    try
    {
        var info = new SetupApi.SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SetupApi.SP_DEVINFO_DATA>() };
        if (!SetupApi.SetupDiCreateDeviceInfoW(set, "MEDIA", ref g, "DroidMic Virtual Audio Device", IntPtr.Zero, SetupApi.DICD_GENERATE_ID, ref info))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        byte[] hwid = Encoding.Unicode.GetBytes(HardwareId + "\0\0");
        if (!SetupApi.SetupDiSetDeviceRegistryPropertyW(set, ref info, SetupApi.SPDRP_HARDWAREID, hwid, (uint)hwid.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        if (!SetupApi.SetupDiCallClassInstaller(SetupApi.DIF_REGISTERDEVICE, set, ref info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    finally
    {
        SetupApi.SetupDiDestroyDeviceInfoList(set);
    }
}

int Uninstall()
{
    var devices = FindDevices();
    foreach (var (set, info, instanceId) in devices)
    {
        var data = info;
        Console.WriteLine($"Removing device {instanceId} ...");
        if (!SetupApi.DiUninstallDevice(IntPtr.Zero, set, ref data, 0, out _))
            Console.Error.WriteLine($"  failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
    }
    foreach (var (set, _, _) in devices.DistinctBy(d => d.Set)) SetupApi.SetupDiDestroyDeviceInfoList(set);

    // Remove the package from the driver store so nothing is left behind.
    foreach (string oem in FindDriverStorePackages())
    {
        Console.WriteLine($"Deleting driver package {oem} ...");
        RunPnputil($"/delete-driver {oem} /uninstall /force");
    }
    Console.WriteLine(devices.Count == 0 ? "No DroidMic device was installed." : "DroidMic virtual audio device removed.");
    return 0;
}

int Status()
{
    var devices = FindDevices();
    if (devices.Count == 0)
    {
        Console.WriteLine("NOT_INSTALLED");
        return 1;
    }
    foreach (var (set, info, instanceId) in devices)
    {
        int cr = SetupApi.CM_Get_DevNode_Status(out uint status, out uint problem, info.DevInst, 0);
        bool started = cr == 0 && (status & SetupApi.DN_STARTED) != 0;
        Console.WriteLine($"{(started ? "RUNNING" : "PROBLEM")} {instanceId}{(started ? "" : $" (problem code {problem})")}");
        if (problem == 52) Console.WriteLine("Code 52: Windows cannot verify the driver signature. See docs/DRIVER_INSTALLATION.md.");
        SetupApi.SetupDiDestroyDeviceInfoList(set);
    }
    return 0;
}

List<(IntPtr Set, SetupApi.SP_DEVINFO_DATA Info, string InstanceId)> FindDevices()
{
    var result = new List<(IntPtr, SetupApi.SP_DEVINFO_DATA, string)>();
    var g = mediaClass;
    IntPtr set = SetupApi.SetupDiGetClassDevsW(ref g, null, IntPtr.Zero, 0);
    if (set == SetupApi.INVALID_HANDLE_VALUE) throw new Win32Exception(Marshal.GetLastWin32Error());
    bool keep = false;
    var buf = new byte[2048];
    for (uint i = 0; ; i++)
    {
        var info = new SetupApi.SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SetupApi.SP_DEVINFO_DATA>() };
        if (!SetupApi.SetupDiEnumDeviceInfo(set, i, ref info)) break;
        if (!SetupApi.SetupDiGetDeviceRegistryPropertyW(set, ref info, SetupApi.SPDRP_HARDWAREID, out _, buf, (uint)buf.Length, out uint size)) continue;
        var ids = Encoding.Unicode.GetString(buf, 0, (int)size).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (!ids.Any(id => id.Equals(HardwareId, StringComparison.OrdinalIgnoreCase))) continue;
        var idBuf = new StringBuilder(512);
        SetupApi.SetupDiGetDeviceInstanceIdW(set, ref info, idBuf, idBuf.Capacity, out _);
        result.Add((set, info, idBuf.ToString()));
        keep = true;
    }
    if (!keep) SetupApi.SetupDiDestroyDeviceInfoList(set);
    return result;
}

IEnumerable<string> FindDriverStorePackages()
{
    // `pnputil /enum-drivers` lists "Published Name: oemNN.inf" followed by "Original Name: droidmicvad.inf".
    string output = RunPnputil("/enum-drivers");
    string? published = null;
    foreach (var raw in output.Split('\n'))
    {
        var line = raw.Trim();
        int colon = line.IndexOf(':');
        if (colon < 0) continue;
        string value = line[(colon + 1)..].Trim();
        if (value.StartsWith("oem", StringComparison.OrdinalIgnoreCase) && value.EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
            published = value;
        else if (value.Equals("droidmicvad.inf", StringComparison.OrdinalIgnoreCase) && published is not null)
            yield return published;
    }
}

static string RunPnputil(string arguments)
{
    var psi = new ProcessStartInfo("pnputil.exe", arguments) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
    using var p = Process.Start(psi)!;
    string output = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return output;
}

internal static class SetupApi
{
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    public const uint DICD_GENERATE_ID = 1;
    public const uint DIF_REGISTERDEVICE = 0x19;
    public const uint SPDRP_HARDWAREID = 1;
    public const uint INSTALLFLAG_FORCE = 1;
    public const uint DN_STARTED = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiCreateDeviceInfoW(IntPtr set, string deviceName, ref Guid classGuid, string? description,
        IntPtr hwndParent, uint flags, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, uint property, byte[] buffer, uint size);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, uint property,
        out uint regType, byte[] buffer, uint size, out uint required);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder id, int size, out int required);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiCallClassInstaller(uint function, IntPtr set, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwnd, string hardwareId, string infPath, uint flags, out bool rebootRequired);

    [DllImport("newdev.dll", SetLastError = true)]
    public static extern bool DiUninstallDevice(IntPtr hwnd, IntPtr set, ref SP_DEVINFO_DATA data, uint flags, out bool rebootRequired);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
}
