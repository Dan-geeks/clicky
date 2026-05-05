using System;
using Buddy.Windows.Diagnostics;
using NAudio.Wave;

namespace Buddy.Windows.Voice;

public sealed class MicrophoneCaptureService : IDisposable
{
    private const int CaptureSampleRate = 16000;
    private const int CaptureBitsPerSample = 16;
    private const int CaptureChannelCount = 1;
    private const int CaptureBufferMilliseconds = 50;
    private const int CaptureBufferCount = 3;

    private readonly object captureStateLock = new();
    private WaveInEvent? microphoneCaptureDevice;
    private DateTimeOffset? currentCaptureStartedAt;
    private long capturedByteCount;
    private double currentAudioLevel;
    private string? captureErrorMessage;
    private bool isDisposed;

    public event EventHandler<MicrophoneAudioCapturedEventArgs>? AudioCaptured;

    public event EventHandler<MicrophoneCaptureStateChangedEventArgs>? CaptureStateChanged;

    public bool IsCapturing
    {
        get
        {
            lock (captureStateLock)
            {
                return microphoneCaptureDevice is not null;
            }
        }
    }

    public string? CaptureErrorMessage
    {
        get
        {
            lock (captureStateLock)
            {
                return captureErrorMessage;
            }
        }
    }

    public void StartCapture()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);

        lock (captureStateLock)
        {
            if (microphoneCaptureDevice is not null)
            {
                return;
            }

            capturedByteCount = 0;
            currentAudioLevel = 0;
            captureErrorMessage = null;
            currentCaptureStartedAt = DateTimeOffset.UtcNow;
        }

        BuddyLog.Workflow("Microphone capture starting.");
        WaveInEvent? newMicrophoneCaptureDevice = null;

        try
        {
            newMicrophoneCaptureDevice = new WaveInEvent
            {
                WaveFormat = new WaveFormat(CaptureSampleRate, CaptureBitsPerSample, CaptureChannelCount),
                BufferMilliseconds = CaptureBufferMilliseconds,
                NumberOfBuffers = CaptureBufferCount
            };

            newMicrophoneCaptureDevice.DataAvailable += HandleMicrophoneDataAvailable;
            newMicrophoneCaptureDevice.RecordingStopped += HandleMicrophoneRecordingStopped;

            lock (captureStateLock)
            {
                microphoneCaptureDevice = newMicrophoneCaptureDevice;
            }

            newMicrophoneCaptureDevice.StartRecording();
            NotifyCaptureStateChanged();
            BuddyLog.Workflow("Microphone capture started.");
        }
        catch (Exception exception)
        {
            if (newMicrophoneCaptureDevice is not null)
            {
                DisposeCaptureDevice(newMicrophoneCaptureDevice);
            }

            lock (captureStateLock)
            {
                microphoneCaptureDevice = null;
                currentCaptureStartedAt = null;
                currentAudioLevel = 0;
                captureErrorMessage = $"Microphone capture failed: {exception.Message}";
            }

            BuddyLog.Error("Microphone capture failed to start", exception);
            NotifyCaptureStateChanged();
        }
    }

    public void StopCapture()
    {
        WaveInEvent? microphoneCaptureDeviceToStop;

        lock (captureStateLock)
        {
            microphoneCaptureDeviceToStop = microphoneCaptureDevice;
        }

        if (microphoneCaptureDeviceToStop is null)
        {
            return;
        }

        BuddyLog.Workflow("Microphone capture stopping.");
        try
        {
            microphoneCaptureDeviceToStop.StopRecording();
        }
        catch (Exception exception)
        {
            BuddyLog.Error("Microphone capture failed to stop", exception);
            CompleteCapture(microphoneCaptureDeviceToStop, $"Microphone stop failed: {exception.Message}");
        }
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;

        WaveInEvent? microphoneCaptureDeviceToDispose;

        lock (captureStateLock)
        {
            microphoneCaptureDeviceToDispose = microphoneCaptureDevice;
            microphoneCaptureDevice = null;
            currentCaptureStartedAt = null;
            currentAudioLevel = 0;
        }

        if (microphoneCaptureDeviceToDispose is not null)
        {
            DisposeCaptureDevice(microphoneCaptureDeviceToDispose);
        }
    }

    private void HandleMicrophoneDataAvailable(object? sender, WaveInEventArgs waveInEventArguments)
    {
        if (waveInEventArguments.BytesRecorded <= 0)
        {
            return;
        }

        byte[] pcm16AudioBytes = new byte[waveInEventArguments.BytesRecorded];
        Buffer.BlockCopy(waveInEventArguments.Buffer, 0, pcm16AudioBytes, 0, waveInEventArguments.BytesRecorded);

        double audioLevel = CalculatePcm16AudioLevel(pcm16AudioBytes, waveInEventArguments.BytesRecorded);
        TimeSpan captureDuration;

        lock (captureStateLock)
        {
            if (microphoneCaptureDevice is null)
            {
                return;
            }

            capturedByteCount += waveInEventArguments.BytesRecorded;
            currentAudioLevel = audioLevel;
            captureDuration = GetCurrentCaptureDuration();
        }

        AudioCaptured?.Invoke(
            this,
            new MicrophoneAudioCapturedEventArgs(
                pcm16AudioBytes,
                waveInEventArguments.BytesRecorded,
                captureDuration,
                audioLevel));

        NotifyCaptureStateChanged();
    }

    private void HandleMicrophoneRecordingStopped(object? sender, StoppedEventArgs stoppedEventArguments)
    {
        if (sender is not WaveInEvent stoppedMicrophoneCaptureDevice)
        {
            return;
        }

        string? stoppedErrorMessage = stoppedEventArguments.Exception is null
            ? null
            : $"Microphone capture stopped: {stoppedEventArguments.Exception.Message}";

        CompleteCapture(stoppedMicrophoneCaptureDevice, stoppedErrorMessage);
    }

    private void CompleteCapture(WaveInEvent stoppedMicrophoneCaptureDevice, string? stoppedErrorMessage)
    {
        bool shouldDisposeStoppedDevice;

        lock (captureStateLock)
        {
            shouldDisposeStoppedDevice = ReferenceEquals(microphoneCaptureDevice, stoppedMicrophoneCaptureDevice);

            if (shouldDisposeStoppedDevice)
            {
                microphoneCaptureDevice = null;
                currentCaptureStartedAt = null;
                currentAudioLevel = 0;
                captureErrorMessage = stoppedErrorMessage;
            }
        }

        if (!shouldDisposeStoppedDevice)
        {
            return;
        }

        DisposeCaptureDevice(stoppedMicrophoneCaptureDevice);
        BuddyLog.Workflow(
            string.IsNullOrWhiteSpace(stoppedErrorMessage)
                ? $"Microphone capture stopped. Captured {capturedByteCount} bytes."
                : $"Microphone capture stopped with error after {capturedByteCount} bytes: {stoppedErrorMessage}");
        NotifyCaptureStateChanged();
    }

    private MicrophoneCaptureStateChangedEventArgs CreateCaptureStateSnapshot()
    {
        lock (captureStateLock)
        {
            return new MicrophoneCaptureStateChangedEventArgs(
                microphoneCaptureDevice is not null,
                GetCurrentCaptureDuration(),
                capturedByteCount,
                currentAudioLevel,
                CaptureSampleRate,
                CaptureBitsPerSample,
                CaptureChannelCount,
                captureErrorMessage);
        }
    }

    private TimeSpan GetCurrentCaptureDuration()
    {
        return currentCaptureStartedAt is null
            ? TimeSpan.Zero
            : DateTimeOffset.UtcNow - currentCaptureStartedAt.Value;
    }

    private void NotifyCaptureStateChanged()
    {
        CaptureStateChanged?.Invoke(this, CreateCaptureStateSnapshot());
    }

    private void DisposeCaptureDevice(WaveInEvent captureDevice)
    {
        captureDevice.DataAvailable -= HandleMicrophoneDataAvailable;
        captureDevice.RecordingStopped -= HandleMicrophoneRecordingStopped;
        captureDevice.Dispose();
    }

    private static double CalculatePcm16AudioLevel(byte[] pcm16AudioBytes, int byteCount)
    {
        double peakAbsoluteSampleValue = 0;

        for (int byteOffset = 0; byteOffset + 1 < byteCount; byteOffset += 2)
        {
            short sampleValue = BitConverter.ToInt16(pcm16AudioBytes, byteOffset);
            double absoluteSampleValue = Math.Abs(sampleValue / 32768.0);
            peakAbsoluteSampleValue = Math.Max(peakAbsoluteSampleValue, absoluteSampleValue);
        }

        return Math.Clamp(peakAbsoluteSampleValue, 0, 1);
    }
}
