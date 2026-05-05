using System;

namespace Buddy.Windows.AI;

public sealed class TextToSpeechPlaybackStateChangedEventArgs : EventArgs
{
    public TextToSpeechPlaybackStateChangedEventArgs(
        bool isFetchingAudio,
        bool isPlayingAudio,
        string spokenText,
        string? playbackErrorMessage)
    {
        IsFetchingAudio = isFetchingAudio;
        IsPlayingAudio = isPlayingAudio;
        SpokenText = spokenText;
        PlaybackErrorMessage = playbackErrorMessage;
    }

    public bool IsFetchingAudio { get; }

    public bool IsPlayingAudio { get; }

    public string SpokenText { get; }

    public string? PlaybackErrorMessage { get; }
}
