namespace DroidMic.AudioEngine;

public enum LatencyPreset
{
    Low,
    Balanced,
    Stable,
}

/// <summary>Jitter-buffer bounds in milliseconds for a <see cref="LatencyPreset"/>.</summary>
public readonly record struct BufferBounds(double MinMs, double MaxMs)
{
    public static BufferBounds For(LatencyPreset preset) => preset switch
    {
        LatencyPreset.Low => new(20, 60),
        LatencyPreset.Balanced => new(30, 100),
        LatencyPreset.Stable => new(60, 200),
        _ => throw new ArgumentOutOfRangeException(nameof(preset)),
    };
}
