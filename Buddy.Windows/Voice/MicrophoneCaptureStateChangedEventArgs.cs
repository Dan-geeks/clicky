using System;

namespace Buddy.Windows.Voice;

public sealed class MicrophoneCaptureStateChangedEventArgs : EventArgs
{
    public MicrophoneCaptureStateChangedEventArgs(
        bool isCapturing,
        TimeSpan captureDuration,
        long capturedByteCount,
        double audioLevel,
        int sampleRate,
        int bitsPerSample,
        int channelCount,
        string? captureErrorMessage)
    {
        IsCapturing = isCapturing;
        CaptureDuration = captureDuration;
        CapturedByteCount = capturedByteCount;
        AudioLevel = audioLevel;
        SampleRate = sampleRate;
        BitsPerSample = bitsPerSample;
        ChannelCount = channelCount;
        CaptureErrorMessage = captureErrorMessage;
    }

    public bool IsCapturing { get; }

    public TimeSpan CaptureDuration { get; }

    public long CapturedByteCount { get; }

    public double AudioLevel { get; }

    public int SampleRate { get; }

    public int BitsPerSample { get; }

    public int ChannelCount { get; }

    public string? CaptureErrorMessage { get; }
}
