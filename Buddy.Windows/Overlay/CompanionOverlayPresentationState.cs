namespace Buddy.Windows.Overlay;

public sealed class CompanionOverlayPresentationState
{
    public CompanionOverlayPresentationState(
        string statusText,
        string detailText,
        string transcriptText,
        string responseText,
        double audioLevel,
        bool shouldShowAudioLevel,
        bool shouldShowTranscript,
        bool shouldShowResponse,
        bool shouldUseCopyPasteLayout = false,
        bool shouldShowThinkingAnimation = false)
    {
        StatusText = statusText;
        DetailText = detailText;
        TranscriptText = transcriptText;
        ResponseText = responseText;
        AudioLevel = audioLevel;
        ShouldShowAudioLevel = shouldShowAudioLevel;
        ShouldShowTranscript = shouldShowTranscript;
        ShouldShowResponse = shouldShowResponse;
        ShouldUseCopyPasteLayout = shouldUseCopyPasteLayout;
        ShouldShowThinkingAnimation = shouldShowThinkingAnimation;
    }

    public string StatusText { get; }

    public string DetailText { get; }

    public string TranscriptText { get; }

    public string ResponseText { get; }

    public double AudioLevel { get; }

    public bool ShouldShowAudioLevel { get; }

    public bool ShouldShowTranscript { get; }

    public bool ShouldShowResponse { get; }

    public bool ShouldUseCopyPasteLayout { get; }

    public bool ShouldShowThinkingAnimation { get; }
}
