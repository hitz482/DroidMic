using NAudio.CoreAudioApi;

namespace DroidMic.Platform;

public enum EndpointAvailability
{
    /// <summary>No DroidMic capture endpoint exists (driver not installed).</summary>
    NotInstalled,
    /// <summary>The endpoint exists but is disabled in Sound settings.</summary>
    Disabled,
    /// <summary>The endpoint exists but Windows reports it unplugged/not present.</summary>
    Unavailable,
    /// <summary>Recording apps can select it.</summary>
    Active,
}

public sealed record EndpointInfo(
    EndpointAvailability Availability,
    string? FriendlyName,
    string? Id,
    bool IsDefault,
    bool IsDefaultCommunications,
    bool DriverInterfacePresent);

/// <summary>
/// Checks, through the Windows Core Audio API, whether the "DroidMic Microphone"
/// recording endpoint really exists and is usable, rather than assuming so.
/// </summary>
public static class EndpointDetector
{
    public static EndpointInfo Detect()
    {
        bool driverPresent = DriverClient.FindInterfaces().Count > 0;
        using var enumerator = new MMDeviceEnumerator();
        MMDevice? match = null;
        try
        {
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.All))
            {
                string name;
                try { name = d.FriendlyName; }
                catch (Exception) { continue; } // endpoints being removed can throw
                if (name.Contains(DriverInterface.EndpointName, StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("DroidMic Virtual Audio", StringComparison.OrdinalIgnoreCase))
                {
                    // Prefer an active endpoint if there are stale ones.
                    if (match is null || d.State == DeviceState.Active) match = d;
                }
            }
            if (match is null) return new EndpointInfo(EndpointAvailability.NotInstalled, null, null, false, false, driverPresent);

            var availability = match.State switch
            {
                DeviceState.Active => EndpointAvailability.Active,
                DeviceState.Disabled => EndpointAvailability.Disabled,
                _ => EndpointAvailability.Unavailable,
            };
            bool isDefault = IsDefault(enumerator, match.ID, Role.Console);
            bool isDefaultComms = IsDefault(enumerator, match.ID, Role.Communications);
            return new EndpointInfo(availability, match.FriendlyName, match.ID, isDefault, isDefaultComms, driverPresent);
        }
        catch (Exception)
        {
            return new EndpointInfo(EndpointAvailability.NotInstalled, null, null, false, false, driverPresent);
        }
    }

    private static bool IsDefault(MMDeviceEnumerator e, string id, Role role)
    {
        try
        {
            return e.HasDefaultAudioEndpoint(DataFlow.Capture, role) && e.GetDefaultAudioEndpoint(DataFlow.Capture, role).ID == id;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
