namespace DroidMic.Protocol;

/// <summary>
/// 64-entry sliding replay window over packet sequence numbers (RFC 4303 style).
/// Call <see cref="Check"/> before decrypting and <see cref="Commit"/> only after
/// the packet authenticated, so forged packets cannot advance the window.
/// </summary>
public sealed class ReplayWindow
{
    public const int Size = 64;

    private ulong _bitmap;
    private uint _highest;
    private bool _any;

    public uint Highest => _highest;

    public bool Check(uint seq)
    {
        if (!_any || seq > _highest) return true;
        uint diff = _highest - seq;
        if (diff >= Size) return false;
        return (_bitmap & (1UL << (int)diff)) == 0;
    }

    public void Commit(uint seq)
    {
        if (!_any)
        {
            _any = true;
            _highest = seq;
            _bitmap = 1;
            return;
        }
        if (seq > _highest)
        {
            uint shift = seq - _highest;
            _bitmap = shift >= Size ? 1 : (_bitmap << (int)shift) | 1;
            _highest = seq;
        }
        else
        {
            _bitmap |= 1UL << (int)(_highest - seq);
        }
    }

    public void Reset()
    {
        _bitmap = 0;
        _highest = 0;
        _any = false;
    }
}
