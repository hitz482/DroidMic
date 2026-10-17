using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using DroidMic.Networking;

namespace DroidMic.Platform;

/// <summary>Secrets protected with DPAPI for the current Windows user.</summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = "DroidMic secret store v1"u8.ToArray();
    private readonly FileSecretStore _files;

    public DpapiSecretStore(string directory) => _files = new FileSecretStore(directory);

    public byte[]? Load(string name)
    {
        byte[]? data = _files.Load(name);
        if (data is null) return null;
        try { return ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { return null; }
    }

    public void Save(string name, byte[] data) =>
        _files.Save(name, ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser));

    public void Delete(string name) => _files.Delete(name);
}

public sealed record LocalAddress(IPAddress Address, string InterfaceName, NetworkInterfaceType Type);

public static class NetworkInfo
{
    /// <summary>Usable IPv4 addresses, Wi-Fi/Ethernet first, virtual adapters last.</summary>
    public static IReadOnlyList<LocalAddress> GetLocalAddresses()
    {
        var list = new List<(LocalAddress addr, int rank)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            string desc = (nic.Description + " " + nic.Name).ToLowerInvariant();
            bool isVirtual = desc.Contains("virtual") || desc.Contains("hyper-v") || desc.Contains("vmware") ||
                             desc.Contains("vpn") || desc.Contains("wsl") || desc.Contains("tap-");
            bool isUsbTether = desc.Contains("rndis") || desc.Contains("remote ndis");
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address)) continue;
                if (ua.Address.ToString().StartsWith("169.254.")) continue;
                int rank = isVirtual ? 3 : isUsbTether ? 1 : nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 0;
                list.Add((new LocalAddress(ua.Address, nic.Name, isUsbTether ? NetworkInterfaceType.Ppp : nic.NetworkInterfaceType), rank));
            }
        }
        return list.OrderBy(x => x.rank).Select(x => x.addr).ToList();
    }
}

/// <summary>Windows Firewall rules for the DroidMic ports (private networks only).</summary>
public static class FirewallHelper
{
    public const string RuleName = "DroidMic";

    public static bool RuleExists()
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{RuleName}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return p.ExitCode == 0 && output.Contains(RuleName);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Adds inbound rules for this executable via an elevated netsh (shows a UAC prompt).</summary>
    public static bool AddRulesElevated(string exePath)
    {
        string cmd =
            $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow program=\"{exePath}\" protocol=TCP localport=48110 profile=private & " +
            $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow program=\"{exePath}\" protocol=UDP localport=48111-48112 profile=private";
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c netsh " + cmd)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(15000);
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // user declined UAC
        }
    }

    /// <summary>True if the active connection profile of any interface is Public (where our rules don't apply).</summary>
    public static bool AnyPublicNetwork()
    {
        try
        {
            var psi = new ProcessStartInfo("powershell", "-NoProfile -Command \"(Get-NetConnectionProfile).NetworkCategory\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output.Contains("Public");
        }
        catch (Exception)
        {
            return false;
        }
    }
}

public static class WindowsShell
{
    public static void OpenSoundSettings() => Open("ms-settings:sound");
    public static void OpenRecordingDevices() => Process.Start(new ProcessStartInfo("control.exe", "mmsys.cpl,,1") { UseShellExecute = true });
    public static void OpenMicrophonePrivacy() => Open("ms-settings:privacy-microphone");
    public static void OpenNetworkSettings() => Open("ms-settings:network-status");
    public static void Open(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
}
