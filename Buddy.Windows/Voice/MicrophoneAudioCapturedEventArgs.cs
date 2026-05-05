using System;

namespace Buddy.Windows.Voice;

public sealed class MicrophoneAudioCapturedEventArgs : EventArgs
{
    public MicrophoneAudioCapturedEventArgs(
        byte[] pcm16AudioBytes,
        int byteCount,
        TimeSpan captureDuration,
        double audioLevel)
    {
        Pcm16AudioBytes = pcm16AudioBytes;
        ByteCount = byteCount;
        CaptureDuration = captureDuration;
        AudioLevel = audioLevel;
    }

    public byte[] Pcm16AudioBytes { get; }

    public int ByteCount { get; }

    public TimeSpan CaptureDuration { get; }

    public double AudioLevel { get; }
}
