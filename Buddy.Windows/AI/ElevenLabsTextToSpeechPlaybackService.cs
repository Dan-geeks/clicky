using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Buddy.Windows.Diagnostics;
using NAudio.Wave;

namespace Buddy.Windows.AI;

public sealed class ElevenLabsTextToSpeechPlaybackService : IDisposable
{
    private readonly ElevenLabsTextToSpeechClient textToSpeechClient;
    private readonly object playbackStateLock = new();
    private CancellationTokenSource? activePlaybackCancellationTokenSource;
    private WaveOutEvent? activeWaveOutDevice;
    private Mp3FileReader? activeMp3FileReader;
    private MemoryStream? activeAudioStream;
    private bool isFetchingAudio;
    private bool isPlayingAudio;
    private string spokenText = "";
    private string? playbackErrorMessage;
    private bool isDisposed;

    public ElevenLabsTextToSpeechPlaybackService(ElevenLabsTextToSpeechClient textToSpeechClient)
    {
        this.textToSpeechClient = textToSpeechClient;
    }

    public event EventHandler<TextToSpeechPlaybackStateChangedEventArgs>? PlaybackStateChanged;

    public bool IsFetchingAudio
    {
        get
        {
            lock (playbackStateLock)
            {
                return isFetchingAudio;
            }
        }
    }

    public bool IsPlayingAudio
    {
        get
        {
            lock (playbackStateLock)
            {
                return isPlayingAudio;
            }
        }
    }

    public string? PlaybackErrorMessage
    {
        get
        {
            lock (playbackStateLock)
            {
                return playbackErrorMessage;
            }
        }
    }

    public void SpeakText(string text)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);

        string trimmedText = text.Trim();

        if (string.IsNullOrWhiteSpace(trimmedText))
        {
            return;
        }

        BuddyLog.Workflow($"TTS workflow starting: {BuddyLog.DescribeTextForLog(trimmedText)}.");
        CancellationTokenSource playbackCancellationTokenSource;

        lock (playbackStateLock)
        {
            StopPlaybackLocked();
            activePlaybackCancellationTokenSource = new CancellationTokenSource();
            playbackCancellationTokenSource = activePlaybackCancellationTokenSource;
            isFetchingAudio = true;
            isPlayingAudio = false;
            spokenText = trimmedText;
            playbackErrorMessage = null;
        }

        NotifyPlaybackStateChanged();

        _ = Task.Run(async () =>
        {
            await FetchAndPlaySpeechAsync(trimmedText, playbackCancellationTokenSource);
        });
    }

    public void StopPlayback()
    {
        BuddyLog.Workflow("TTS workflow stop requested.");

        lock (playbackStateLock)
        {
            StopPlaybackLocked();
            isFetchingAudio = false;
            isPlayingAudio = false;
            spokenText = "";
            playbackErrorMessage = null;
        }

        NotifyPlaybackStateChanged();
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        StopPlayback();
    }

    private async Task FetchAndPlaySpeechAsync(
        string text,
        CancellationTokenSource playbackCancellationTokenSource)
    {
        bool didStartPlayback = false;

        try
        {
            byte[] audioBytes = await textToSpeechClient.FetchSpeechAudioAsync(
                text,
                playbackCancellationTokenSource.Token);

            if (playbackCancellationTokenSource.IsCancellationRequested)
            {
                return;
            }

            didStartPlayback = StartAudioPlayback(audioBytes, playbackCancellationTokenSource);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            BuddyLog.Error("TTS fetch or playback failed", exception);

            lock (playbackStateLock)
            {
                if (!ReferenceEquals(activePlaybackCancellationTokenSource, playbackCancellationTokenSource))
                {
                    return;
                }

                isFetchingAudio = false;
                isPlayingAudio = false;
                playbackErrorMessage = exception.Message;
                activePlaybackCancellationTokenSource = null;
            }

            NotifyPlaybackStateChanged();
        }
        finally
        {
            if (!didStartPlayback)
            {
                DisposeCancellationTokenSourceIfInactive(playbackCancellationTokenSource);
            }
        }
    }

    private bool StartAudioPlayback(
        byte[] audioBytes,
        CancellationTokenSource playbackCancellationTokenSource)
    {
        MemoryStream? audioStream = null;
        Mp3FileReader? mp3FileReader = null;
        WaveOutEvent? waveOutDevice = null;

        try
        {
            audioStream = new MemoryStream(audioBytes);
            mp3FileReader = new Mp3FileReader(audioStream);
            waveOutDevice = new WaveOutEvent();
            waveOutDevice.PlaybackStopped += HandlePlaybackStopped;
            waveOutDevice.Init(mp3FileReader);
        }
        catch
        {
            if (waveOutDevice is not null)
            {
                waveOutDevice.PlaybackStopped -= HandlePlaybackStopped;
                waveOutDevice.Dispose();
            }

            mp3FileReader?.Dispose();
            audioStream?.Dispose();
            throw;
        }

        if (audioStream is null || mp3FileReader is null || waveOutDevice is null)
        {
            throw new InvalidOperationException("Audio playback could not be initialized.");
        }

        lock (playbackStateLock)
        {
            if (!ReferenceEquals(activePlaybackCancellationTokenSource, playbackCancellationTokenSource)
                || playbackCancellationTokenSource.IsCancellationRequested)
            {
                waveOutDevice.PlaybackStopped -= HandlePlaybackStopped;
                waveOutDevice.Dispose();
                mp3FileReader.Dispose();
                audioStream.Dispose();
                return false;
            }

            DisposePlaybackResourcesLocked();
            activeAudioStream = audioStream;
            activeMp3FileReader = mp3FileReader;
            activeWaveOutDevice = waveOutDevice;
            isFetchingAudio = false;
            isPlayingAudio = true;
            playbackErrorMessage = null;
        }

        NotifyPlaybackStateChanged();
        waveOutDevice.Play();
        BuddyLog.Workflow($"TTS workflow playback started. AudioBytes={audioBytes.Length}.");
        return true;
    }

    private void HandlePlaybackStopped(object? sender, StoppedEventArgs stoppedEventArguments)
    {
        string? stoppedErrorMessage = stoppedEventArguments.Exception?.Message;

        if (stoppedEventArguments.Exception is not null)
        {
            BuddyLog.Error("TTS playback stopped with an error", stoppedEventArguments.Exception);
        }
        else
        {
            BuddyLog.Workflow("TTS workflow playback stopped.");
        }

        lock (playbackStateLock)
        {
            if (!ReferenceEquals(sender, activeWaveOutDevice))
            {
                return;
            }

            isFetchingAudio = false;
            isPlayingAudio = false;
            playbackErrorMessage = stoppedErrorMessage;
            activePlaybackCancellationTokenSource?.Dispose();
            activePlaybackCancellationTokenSource = null;
            DisposePlaybackResourcesLocked();
        }

        NotifyPlaybackStateChanged();
    }

    private void StopPlaybackLocked()
    {
        activePlaybackCancellationTokenSource?.Cancel();
        activePlaybackCancellationTokenSource = null;

        if (activeWaveOutDevice is not null)
        {
            activeWaveOutDevice.PlaybackStopped -= HandlePlaybackStopped;
            activeWaveOutDevice.Stop();
        }

        DisposePlaybackResourcesLocked();
    }

    private void DisposeCancellationTokenSourceIfInactive(CancellationTokenSource playbackCancellationTokenSource)
    {
        lock (playbackStateLock)
        {
            if (ReferenceEquals(activePlaybackCancellationTokenSource, playbackCancellationTokenSource))
            {
                return;
            }
        }

        playbackCancellationTokenSource.Dispose();
    }

    private void DisposePlaybackResourcesLocked()
    {
        if (activeWaveOutDevice is not null)
        {
            activeWaveOutDevice.PlaybackStopped -= HandlePlaybackStopped;
            activeWaveOutDevice.Dispose();
            activeWaveOutDevice = null;
        }

        activeMp3FileReader?.Dispose();
        activeMp3FileReader = null;

        activeAudioStream?.Dispose();
        activeAudioStream = null;
    }

    private TextToSpeechPlaybackStateChangedEventArgs CreatePlaybackStateSnapshot()
    {
        lock (playbackStateLock)
        {
            return new TextToSpeechPlaybackStateChangedEventArgs(
                isFetchingAudio,
                isPlayingAudio,
                spokenText,
                playbackErrorMessage);
        }
    }

    private void NotifyPlaybackStateChanged()
    {
        PlaybackStateChanged?.Invoke(this, CreatePlaybackStateSnapshot());
    }
}
