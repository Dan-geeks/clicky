using System;

namespace Buddy.Windows.Voice;

public sealed class StreamingTranscriptionStateChangedEventArgs : EventArgs
{
    public StreamingTranscriptionStateChangedEventArgs(
        bool isConnecting,
        bool isStreaming,
        string liveTranscriptText,
        string finalTranscriptText,
        string? transcriptionErrorMessage)
    {
        IsConnecting = isConnecting;
        IsStreaming = isStreaming;
        LiveTranscriptText = liveTranscriptText;
        FinalTranscriptText = finalTranscriptText;
        TranscriptionErrorMessage = transcriptionErrorMessage;
    }

    public bool IsConnecting { get; }

    public bool IsStreaming { get; }

    public string LiveTranscriptText { get; }

    public string FinalTranscriptText { get; }

    public string? TranscriptionErrorMessage { get; }
}
