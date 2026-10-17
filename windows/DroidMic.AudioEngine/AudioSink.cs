namespace DroidMic.AudioEngine;

/// <summary>
/// Destination for the PC-side audio stream (the virtual microphone driver, a
/// monitor output, a WAV file...). The sink's consumption rate is the master
/// clock of the pipeline.
/// </summary>
public interface IAudioSink : IDisposable
{
    /// <summary>Human-readable name shown in the UI and sent in stats.</summary>
    string Name { get; }

    /// <summary>Opens the sink for mono 16-bit PCM at <paramref name="sampleRate"/>.</summary>
    void Open(int sampleRate);

    /// <summary>Queues samples; returns the number accepted.</summary>
    int Write(ReadOnlySpan<short> samples);

    /// <summary>Samples written but not yet consumed by the sink's clock.</summary>
    int QueuedSamples { get; }

    void Close();
}
