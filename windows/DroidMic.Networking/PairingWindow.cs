using System.Security.Cryptography;
using DroidMic.Protocol;

namespace DroidMic.Networking;

public sealed record PairingInfo(string Pin, DateTimeOffset ExpiresAt);

/// <summary>
/// Tracks the user-opened pairing window and its one-time PIN (protocol §3.4).
/// PIN pairing is only possible while the window is open.
/// </summary>
public sealed class PairingWindow
{
    private readonly object _lock = new();
    private readonly TimeProvider _time;
    private PairingInfo? _current;
    private int _failures;

    public PairingWindow(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public event Action<PairingInfo?>? Changed;

    public PairingInfo? Current
    {
        get
        {
            lock (_lock)
            {
                if (_current is not null && _time.GetUtcNow() >= _current.ExpiresAt) _current = null;
                return _current;
            }
        }
    }

    public PairingInfo Open() => OpenWith(RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6"));

    /// <summary>Opens pairing with a caller-chosen PIN. Only for automated tests and the headless receiver.</summary>
    public PairingInfo OpenWith(string pin)
    {
        if (pin.Length != ProtocolConstants.PinLength || !pin.All(char.IsAsciiDigit))
            throw new ArgumentException("PIN must be 6 digits", nameof(pin));
        PairingInfo info;
        lock (_lock)
        {
            info = new PairingInfo(pin, _time.GetUtcNow() + Lifetime);
            _current = info;
            _failures = 0;
        }
        Changed?.Invoke(info);
        return info;
    }

    public void Close()
    {
        lock (_lock) _current = null;
        Changed?.Invoke(null);
    }

    /// <summary>Returns the PIN to authenticate against, or null if pairing is closed.</summary>
    public string? CurrentPin => Current?.Pin;

    /// <summary>Records a failed attempt. Returns true if the window was locked out and closed.</summary>
    public bool RecordFailure()
    {
        bool locked;
        lock (_lock)
        {
            _failures++;
            locked = _failures >= ProtocolConstants.MaxPinAttempts;
            if (locked) _current = null;
        }
        if (locked) Changed?.Invoke(null);
        return locked;
    }

    /// <summary>Called after a successful pairing: the PIN is single-use.</summary>
    public void Consume() => Close();
}
