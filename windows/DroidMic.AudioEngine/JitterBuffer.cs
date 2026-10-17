namespace DroidMic.AudioEngine;

/// <summary>
/// Bounded, sequence-ordered frame store. Frames are mono 16-bit PCM. Capacity is
/// fixed at construction and all storage is preallocated; Insert and TakeNext do
/// not allocate. Not thread-safe: <see cref="AudioPipeline"/> serializes access.
/// </summary>
public sealed class JitterBuffer
{
    private readonly short[][] _slots;
    private readonly int[] _lengths;
    private readonly uint[] _seqs;
    private readonly bool[] _filled;
    private readonly int _capacity;

    private bool _started;
    private uint _nextSeq;
    private int _count;
    private uint _highestSeq;

    public JitterBuffer(int capacityFrames, int maxFrameSamples)
    {
        if (capacityFrames < 4) throw new ArgumentOutOfRangeException(nameof(capacityFrames));
        _capacity = capacityFrames;
        _slots = new short[capacityFrames][];
        for (int i = 0; i < capacityFrames; i++) _slots[i] = new short[maxFrameSamples];
        _lengths = new int[capacityFrames];
        _seqs = new uint[capacityFrames];
        _filled = new bool[capacityFrames];
    }

    public int Capacity => _capacity;

    /// <summary>Number of frames currently stored.</summary>
    public int Count => _count;

    /// <summary>Total samples currently stored.</summary>
    public int BufferedSamples
    {
        get
        {
            int total = 0;
            for (int i = 0; i < _capacity; i++) if (_filled[i]) total += _lengths[i];
            return total;
        }
    }

    public uint NextSequence => _nextSeq;

    public long Late { get; private set; }
    public long Duplicates { get; private set; }
    public long Overflows { get; private set; }

    public enum InsertResult { Stored, Late, Duplicate, OverflowDroppedOldest }

    public InsertResult Insert(uint seq, ReadOnlySpan<short> samples)
    {
        if (samples.Length > _slots[0].Length) throw new ArgumentException("Frame larger than slot", nameof(samples));
        if (!_started)
        {
            _started = true;
            _nextSeq = seq;
            _highestSeq = seq;
        }
        else if (seq < _nextSeq)
        {
            Late++;
            return InsertResult.Late;
        }

        var result = InsertResult.Stored;
        // Frame too far ahead to fit: advance the playout point, discarding the oldest frames.
        if (seq - _nextSeq >= (uint)_capacity)
        {
            uint newNext = seq - (uint)_capacity + 1;
            while (_nextSeq != newNext)
            {
                int idx = Index(_nextSeq);
                if (_filled[idx] && _seqs[idx] == _nextSeq) { _filled[idx] = false; _count--; }
                _nextSeq++;
            }
            Overflows++;
            result = InsertResult.OverflowDroppedOldest;
        }

        int slot = Index(seq);
        if (_filled[slot] && _seqs[slot] == seq)
        {
            Duplicates++;
            return InsertResult.Duplicate;
        }
        if (_filled[slot]) _count--; // stale occupant from an older lap
        samples.CopyTo(_slots[slot]);
        _lengths[slot] = samples.Length;
        _seqs[slot] = seq;
        _filled[slot] = true;
        _count++;
        if (seq > _highestSeq) _highestSeq = seq;
        return result;
    }

    /// <summary>
    /// Removes the frame at the playout point. Returns its length, 0 if the frame
    /// is missing (lost or not yet arrived), or -1 if the buffer is empty.
    /// The playout point advances in every case except -1.
    /// </summary>
    public int TakeNext(Span<short> destination)
    {
        if (!_started || _count == 0) return -1;
        int idx = Index(_nextSeq);
        int len = 0;
        if (_filled[idx] && _seqs[idx] == _nextSeq)
        {
            len = _lengths[idx];
            _slots[idx].AsSpan(0, len).CopyTo(destination);
            _filled[idx] = false;
            _count--;
        }
        _nextSeq++;
        return len;
    }

    /// <summary>Discards the oldest frame (used for coarse latency reduction).</summary>
    public bool DropOldest()
    {
        if (!_started || _count == 0) return false;
        int idx = Index(_nextSeq);
        if (_filled[idx] && _seqs[idx] == _nextSeq)
        {
            _filled[idx] = false;
            _count--;
        }
        _nextSeq++;
        return true;
    }

    public void Reset()
    {
        Array.Clear(_filled);
        _count = 0;
        _started = false;
        _nextSeq = 0;
        _highestSeq = 0;
        Late = Duplicates = Overflows = 0;
    }

    private int Index(uint seq) => (int)(seq % (uint)_capacity);
}
