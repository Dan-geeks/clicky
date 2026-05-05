using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Buddy.Windows.AI;
using Buddy.Windows.Diagnostics;
using Buddy.Windows.Pointing;
using Buddy.Windows.Voice;
using Forms = System.Windows.Forms;

namespace Buddy.Windows.Overlay;

public sealed class CompanionOverlayController : IDisposable
{
    private const int LowLevelKeyboardHookIdentifier = 13;
    private const int LowLevelMouseHookIdentifier = 14;
    private const int KeyDownMessageIdentifier = 0x0100;
    private const int SystemKeyDownMessageIdentifier = 0x0104;
    private const int LeftButtonUpMessageIdentifier = 0x0202;
    private const int RightButtonUpMessageIdentifier = 0x0205;
    private const int MiddleButtonUpMessageIdentifier = 0x0208;
    private const int EscapeVirtualKey = 0x1B;
    private const int ControlVirtualKey = 0x11;
    private const int ShiftVirtualKey = 0x10;
    private const int InsertVirtualKey = 0x2D;
    private const int VVirtualKey = 0x56;
    private static readonly TimeSpan IdleDismissDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CopyPasteOverlayPasteDismissDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan PointingActionCompletionSettleDelay = TimeSpan.FromMilliseconds(350);
    /// <summary>
    /// How long the pointing bubble stays visible before auto-fading back to the follower
    /// cursor when the user does not click. Mirrors the macOS overlay's hold duration so
    /// the workflow never gets stuck on a single instruction the way the previous
    /// click-near-target gate could. If the user clicks anywhere during this window the
    /// guided task continues to the next step.
    /// </summary>
    private static readonly TimeSpan PointingInstructionAutoFadeDelay = TimeSpan.FromSeconds(5);
    private const int MaximumPointingInstructionTextLength = 120;
    private static readonly string[] CopyPasteIntentTerms =
    {
        "copy",
        "paste",
        "code",
        "command",
        "script",
        "text",
        "message",
        "email",
        "reply",
        "terminal",
        "powershell"
    };

    private readonly PushToTalkHotkeyMonitor pushToTalkHotkeyMonitor;
    private readonly MicrophoneCaptureService microphoneCaptureService;
    private readonly AssemblyAIStreamingTranscriptionService streamingTranscriptionService;
    private readonly ClaudeResponseService claudeResponseService;
    private readonly ElevenLabsTextToSpeechPlaybackService textToSpeechPlaybackService;
    private readonly DispatcherTimer idleDismissTimer;
    private readonly DispatcherTimer copyPasteOverlayPasteDismissTimer;
    private readonly DispatcherTimer pointingActionCompletionSettleTimer;
    private readonly DispatcherTimer pointingInstructionAutoFadeTimer;
    private readonly LowLevelKeyboardProcedure keyboardProcedure;
    private readonly LowLevelMouseProcedure mouseProcedure;
    private CompanionOverlayWindow? overlayWindow;
    private IntPtr keyboardHookHandle = IntPtr.Zero;
    private IntPtr mouseHookHandle = IntPtr.Zero;
    private bool isPushToTalkPressed;
    private bool isPushToTalkMonitoring;
    private string? pushToTalkMonitoringErrorMessage;
    private bool isMicrophoneCapturing;
    private string? microphoneCaptureErrorMessage;
    private double currentAudioLevel;
    private bool isTranscriptionConnecting;
    private bool isTranscriptionStreaming;
    private string liveTranscriptText = "";
    private string finalTranscriptText = "";
    private string? transcriptionErrorMessage;
    private bool isCapturingScreens;
    private bool isClaudeResponding;
    private string claudeUserTranscriptText = "";
    private string claudeResponseText = "";
    private string? screenCaptureErrorMessage;
    private string? claudeResponseErrorMessage;
    private string persistentCopyPasteResponseText = "";
    private string persistentCopyPasteDetailText = "";
    private bool isTextToSpeechFetchingAudio;
    private bool isTextToSpeechPlayingAudio;
    private string textToSpeechSpokenText = "";
    private string? textToSpeechPlaybackErrorMessage;
    private PointingInstruction? latestPointingInstruction;
    private PointingInstruction? pointingInstructionWaitingForUserAction;
    private PointingInstruction? preparedNextPointingInstruction;
    private string preparedNextInstructionText = "";
    private string lastAnimatedPointingInstructionSignature = "";
    private string activeGuidedTaskRequestText = "";
    private string latestGuidedVisibleInstructionText = "";
    private int activeGuidedTaskStepNumber;
    private bool isPersistentCopyPasteOverlayVisible;
    private bool isCompanionPanelSuppressedForPointing;
    private bool isAdvancingGuidedPointingStep;
    private bool isDisposed;

    public CompanionOverlayController(
        PushToTalkHotkeyMonitor pushToTalkHotkeyMonitor,
        MicrophoneCaptureService microphoneCaptureService,
        AssemblyAIStreamingTranscriptionService streamingTranscriptionService,
        ClaudeResponseService claudeResponseService,
        ElevenLabsTextToSpeechPlaybackService textToSpeechPlaybackService)
    {
        this.pushToTalkHotkeyMonitor = pushToTalkHotkeyMonitor;
        this.microphoneCaptureService = microphoneCaptureService;
        this.streamingTranscriptionService = streamingTranscriptionService;
        this.claudeResponseService = claudeResponseService;
        this.textToSpeechPlaybackService = textToSpeechPlaybackService;

        idleDismissTimer = new DispatcherTimer
        {
            Interval = IdleDismissDelay
        };
        idleDismissTimer.Tick += HandleIdleDismissTimerTick;

        copyPasteOverlayPasteDismissTimer = new DispatcherTimer
        {
            Interval = CopyPasteOverlayPasteDismissDelay
        };
        copyPasteOverlayPasteDismissTimer.Tick += HandleCopyPasteOverlayPasteDismissTimerTick;

        pointingActionCompletionSettleTimer = new DispatcherTimer
        {
            Interval = PointingActionCompletionSettleDelay
        };
        pointingActionCompletionSettleTimer.Tick += HandlePointingActionCompletionSettleTimerTick;

        pointingInstructionAutoFadeTimer = new DispatcherTimer
        {
            Interval = PointingInstructionAutoFadeDelay
        };
        pointingInstructionAutoFadeTimer.Tick += HandlePointingInstructionAutoFadeTimerTick;
        keyboardProcedure = HandleKeyboardEvent;
        mouseProcedure = HandleMouseEvent;
    }

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);

        overlayWindow = new CompanionOverlayWindow();
        isPushToTalkPressed = pushToTalkHotkeyMonitor.IsPushToTalkPressed;
        isPushToTalkMonitoring = pushToTalkHotkeyMonitor.IsMonitoring;

        pushToTalkHotkeyMonitor.PushToTalkHotkeyChanged += HandlePushToTalkHotkeyChanged;
        microphoneCaptureService.CaptureStateChanged += HandleMicrophoneCaptureStateChanged;
        streamingTranscriptionService.TranscriptionStateChanged += HandleStreamingTranscriptionStateChanged;
        claudeResponseService.ResponseStateChanged += HandleClaudeResponseStateChanged;
        textToSpeechPlaybackService.PlaybackStateChanged += HandleTextToSpeechPlaybackStateChanged;
        RenderOverlay();
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        idleDismissTimer.Stop();
        copyPasteOverlayPasteDismissTimer.Stop();
        pointingActionCompletionSettleTimer.Stop();
        pointingInstructionAutoFadeTimer.Stop();
        UninstallKeyboardHook();
        UninstallMouseHook();
        idleDismissTimer.Tick -= HandleIdleDismissTimerTick;
        copyPasteOverlayPasteDismissTimer.Tick -= HandleCopyPasteOverlayPasteDismissTimerTick;
        pointingActionCompletionSettleTimer.Tick -= HandlePointingActionCompletionSettleTimerTick;
        pointingInstructionAutoFadeTimer.Tick -= HandlePointingInstructionAutoFadeTimerTick;
        pushToTalkHotkeyMonitor.PushToTalkHotkeyChanged -= HandlePushToTalkHotkeyChanged;
        microphoneCaptureService.CaptureStateChanged -= HandleMicrophoneCaptureStateChanged;
        streamingTranscriptionService.TranscriptionStateChanged -= HandleStreamingTranscriptionStateChanged;
        claudeResponseService.ResponseStateChanged -= HandleClaudeResponseStateChanged;
        textToSpeechPlaybackService.PlaybackStateChanged -= HandleTextToSpeechPlaybackStateChanged;

        overlayWindow?.Close();
        overlayWindow = null;
    }

    private void HandlePushToTalkHotkeyChanged(object? sender, PushToTalkHotkeyChangedEventArgs eventArguments)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            isPushToTalkPressed = eventArguments.IsPushToTalkPressed;
            isPushToTalkMonitoring = eventArguments.IsMonitoring;
            pushToTalkMonitoringErrorMessage = eventArguments.MonitoringErrorMessage;

            if (eventArguments.IsPushToTalkPressed)
            {
                latestPointingInstruction = null;
                pointingInstructionWaitingForUserAction = null;
                preparedNextPointingInstruction = null;
                preparedNextInstructionText = "";
                lastAnimatedPointingInstructionSignature = "";
                activeGuidedTaskRequestText = "";
                latestGuidedVisibleInstructionText = "";
                activeGuidedTaskStepNumber = 0;
                isCompanionPanelSuppressedForPointing = false;
                isAdvancingGuidedPointingStep = false;
                ClearPersistentCopyPasteOverlay();
                StopWaitingForPointingStepCompletion();
                overlayWindow?.ClearPointingCursor();
            }

            RenderOverlay();
        }));
    }

    private void HandleMicrophoneCaptureStateChanged(
        object? sender,
        MicrophoneCaptureStateChangedEventArgs eventArguments)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            isMicrophoneCapturing = eventArguments.IsCapturing;
            microphoneCaptureErrorMessage = eventArguments.CaptureErrorMessage;
            currentAudioLevel = eventArguments.AudioLevel;
            RenderOverlay();
        }));
    }

    private void HandleStreamingTranscriptionStateChanged(
        object? sender,
        StreamingTranscriptionStateChangedEventArgs eventArguments)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            isTranscriptionConnecting = eventArguments.IsConnecting;
            isTranscriptionStreaming = eventArguments.IsStreaming;
            liveTranscriptText = eventArguments.LiveTranscriptText;
            finalTranscriptText = eventArguments.FinalTranscriptText;
            transcriptionErrorMessage = eventArguments.TranscriptionErrorMessage;
            RenderOverlay();
        }));
    }

  private void HandleClaudeResponseStateChanged(object? sender, ClaudeResponseStateChangedEventArgs eventArguments)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            PointingInstruction? responsePointingInstruction =
                LatestPointingInstruction(eventArguments.PointingInstructions);
            bool shouldKeepPreparedPointVisibleWhileVerifying =
                isAdvancingGuidedPointingStep
                && latestPointingInstruction is not null
                && responsePointingInstruction is null
                && (eventArguments.IsCapturingScreens || eventArguments.IsResponding);

            isCapturingScreens = eventArguments.IsCapturingScreens;
            isClaudeResponding = eventArguments.IsResponding;
            claudeUserTranscriptText = eventArguments.UserTranscriptText;
            screenCaptureErrorMessage = eventArguments.ScreenCaptureErrorMessage;
            claudeResponseErrorMessage = eventArguments.ResponseErrorMessage;

            if (!string.IsNullOrWhiteSpace(eventArguments.ResponseErrorMessage))
            {
                claudeResponseText = $"Error: {eventArguments.ResponseErrorMessage}";
                isCompanionPanelSuppressedForPointing = false;
            }
            else if (!shouldKeepPreparedPointVisibleWhileVerifying
                || !string.IsNullOrWhiteSpace(eventArguments.ResponseText))
            {
                claudeResponseText = eventArguments.ResponseText;
            }

            if (!shouldKeepPreparedPointVisibleWhileVerifying)
            {
                latestPointingInstruction = responsePointingInstruction;
                preparedNextPointingInstruction = eventArguments.PreparedNextPointingInstruction;
                preparedNextInstructionText = eventArguments.PreparedNextInstructionText;
            }

            if (responsePointingInstruction is not null)
            {
                RememberGuidedTaskContext(eventArguments.UserTranscriptText, eventArguments.ResponseText);
                isAdvancingGuidedPointingStep = false;
            }

            if (eventArguments.IsCapturingScreens
                && latestPointingInstruction is null
                && !isAdvancingGuidedPointingStep)
            {
                isCompanionPanelSuppressedForPointing = false;
                StopWaitingForPointingStepCompletion();
            }

            if (!eventArguments.IsCapturingScreens
                && !eventArguments.IsResponding
                && responsePointingInstruction is null)
            {
                latestPointingInstruction = null;
                preparedNextPointingInstruction = null;
                preparedNextInstructionText = "";
                isAdvancingGuidedPointingStep = false;
                activeGuidedTaskRequestText = "";
                latestGuidedVisibleInstructionText = "";
                activeGuidedTaskStepNumber = 0;

                if (ShouldPersistResponseForCopyPaste(
                    eventArguments.ResponseText,
                    eventArguments.UserTranscriptText))
                {
                    ShowPersistentCopyPasteOverlay(eventArguments.ResponseText);
                }
                else
                {
                    ClearPersistentCopyPasteOverlay();
                }
            }
            else if (eventArguments.IsCapturingScreens
                || eventArguments.IsResponding
                || responsePointingInstruction is not null)
            {
                ClearPersistentCopyPasteOverlay();
            }

            RenderOverlay();
        }));
    }

    private void HandleTextToSpeechPlaybackStateChanged(
        object? sender,
        TextToSpeechPlaybackStateChangedEventArgs eventArguments)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            isTextToSpeechFetchingAudio = eventArguments.IsFetchingAudio;
            isTextToSpeechPlayingAudio = eventArguments.IsPlayingAudio;
            textToSpeechSpokenText = eventArguments.SpokenText;
            textToSpeechPlaybackErrorMessage = eventArguments.PlaybackErrorMessage;
            RenderOverlay();
        }));
    }

    private void HandleIdleDismissTimerTick(object? sender, EventArgs eventArguments)
    {
        idleDismissTimer.Stop();
        overlayWindow?.Dismiss();
    }

    private void HandleCopyPasteOverlayPasteDismissTimerTick(object? sender, EventArgs eventArguments)
    {
        copyPasteOverlayPasteDismissTimer.Stop();
        DismissPersistentCopyPasteOverlay();
    }

    private void HandlePointingActionCompletionSettleTimerTick(object? sender, EventArgs eventArguments)
    {
        pointingActionCompletionSettleTimer.Stop();

        CompletePointingStepAndVerify(GuidedPointingFollowUpReason.UserActionCompletedAtTarget);
    }

    private void HandlePointingInstructionAutoFadeTimerTick(object? sender, EventArgs eventArguments)
    {
        pointingInstructionAutoFadeTimer.Stop();

        // The user did not click within the hold window. Drop the pointing instruction
        // and any cached follow-up so the overlay returns to follower-cursor mode like
        // the macOS bubble does after its timeout. The user can re-trigger guidance
        // with another push-to-talk.
        if (latestPointingInstruction is null
            && pointingInstructionWaitingForUserAction is null)
        {
            return;
        }

        BuddyLog.Workflow("Pointing instruction auto-faded after the hold window expired.");
        latestPointingInstruction = null;
        preparedNextPointingInstruction = null;
        preparedNextInstructionText = "";
        lastAnimatedPointingInstructionSignature = "";
        activeGuidedTaskRequestText = "";
        latestGuidedVisibleInstructionText = "";
        activeGuidedTaskStepNumber = 0;
        isAdvancingGuidedPointingStep = false;
        isCompanionPanelSuppressedForPointing = false;
        StopWaitingForPointingStepCompletion();
        // Animated return-to-cursor: the bubble fades, the cursor flies back along a
        // Bezier arc to the user's real mouse, and only then do we render the next state
        // (follower mode). Mirrors the macOS overlay's fly-back-and-resume-following feel.
        overlayWindow?.ClearPointingCursor(animateReturnToCursor: true, onCompleted: RenderOverlay);
    }

    private void CompletePointingStepAndVerify(GuidedPointingFollowUpReason followUpReason)
    {
        PointingInstruction? completedPointingInstruction =
            pointingInstructionWaitingForUserAction ?? latestPointingInstruction;

        if (completedPointingInstruction is null)
        {
            return;
        }

        PointingInstruction? cachedNextPointingInstruction = preparedNextPointingInstruction;
        string cachedNextInstructionText = preparedNextInstructionText;
        int nextGuidedTaskStepNumber = activeGuidedTaskStepNumber <= 0
            ? 2
            : activeGuidedTaskStepNumber + 1;
        string followUpPromptText = CreateGuidedPointingFollowUpPrompt(
            completedPointingInstruction,
            followUpReason,
            activeGuidedTaskRequestText,
            latestGuidedVisibleInstructionText,
            nextGuidedTaskStepNumber);

        BuddyLog.Workflow(
            $"Guided pointing workflow advancing after user action. Step={nextGuidedTaskStepNumber}; Target=\"{completedPointingInstruction.Label}\"; Screen={completedPointingInstruction.ScreenNumber}; X={completedPointingInstruction.XInScreenPixels:0}; Y={completedPointingInstruction.YInScreenPixels:0}; HasCachedNextPoint={cachedNextPointingInstruction is not null}.");
        latestPointingInstruction = null;
        pointingInstructionWaitingForUserAction = null;
        preparedNextPointingInstruction = null;
        preparedNextInstructionText = "";
        activeGuidedTaskStepNumber = nextGuidedTaskStepNumber;
        isCompanionPanelSuppressedForPointing = true;
        StopWaitingForPointingStepCompletion();
        overlayWindow?.ClearPointingCursor();

        if (cachedNextPointingInstruction is not null)
        {
            latestPointingInstruction = cachedNextPointingInstruction;
            claudeResponseText = string.IsNullOrWhiteSpace(cachedNextInstructionText)
                ? cachedNextPointingInstruction.Label
                : cachedNextInstructionText;
            latestGuidedVisibleInstructionText = claudeResponseText;
            lastAnimatedPointingInstructionSignature = "";
            isAdvancingGuidedPointingStep = true;
            RenderOverlay();
        }
        else
        {
            isAdvancingGuidedPointingStep = true;
            RenderOverlay();
        }

        textToSpeechPlaybackService.StopPlayback();
        claudeResponseService.SendUserTranscript(followUpPromptText, preferFastModel: true);
    }

    private void RenderOverlay()
    {
        if (overlayWindow is null)
        {
            return;
        }

        CompanionOverlayPresentationState? presentationState = CreateActivePresentationState();

        if (latestPointingInstruction is not null)
        {
            idleDismissTimer.Stop();
            RenderPointingInstruction();
            return;
        }

        if (isAdvancingGuidedPointingStep)
        {
            idleDismissTimer.Stop();
            overlayWindow.PresentFollowerCursor();
            return;
        }

        if (presentationState is null || isCompanionPanelSuppressedForPointing)
        {
            idleDismissTimer.Stop();
            isCompanionPanelSuppressedForPointing = false;
            overlayWindow.PresentFollowerCursor();
            return;
        }

        idleDismissTimer.Stop();
        overlayWindow.Present(presentationState);
        RenderPointingInstruction();
    }

    private void RenderPointingInstruction()
    {
        if (overlayWindow is null || latestPointingInstruction is null)
        {
            StopWaitingForPointingStepCompletion();
            overlayWindow?.ClearPointingCursor();
            return;
        }

        string pointingInstructionSignature = CreatePointingInstructionSignature(latestPointingInstruction);
        bool shouldAnimatePointingInstruction =
            pointingInstructionSignature != lastAnimatedPointingInstructionSignature;

        if (!IsPointingTargetScreenAvailable(latestPointingInstruction))
        {
            latestPointingInstruction = null;
            StopWaitingForPointingStepCompletion();
            overlayWindow.ClearPointingCursor();
            return;
        }

        if (pointingInstructionWaitingForUserAction is not null)
        {
            InstallMouseHook();
            return;
        }

        isCompanionPanelSuppressedForPointing = true;

        overlayWindow.PointToInstruction(
            latestPointingInstruction,
            CreatePointingInstructionText(latestPointingInstruction),
            shouldAnimatePointingInstruction);

        lastAnimatedPointingInstructionSignature = pointingInstructionSignature;
        if (pointingInstructionWaitingForUserAction is null)
        {
            StartWaitingForUserActionAtPointingTarget(latestPointingInstruction);
        }
    }

    private CompanionOverlayPresentationState? CreateActivePresentationState()
    {
        string bestTranscriptText = BestAvailableTranscriptText();
        string bestResponseText = BestAvailableResponseText();
        bool hasTranscript = !string.IsNullOrWhiteSpace(bestTranscriptText);
        bool hasResponse = !string.IsNullOrWhiteSpace(bestResponseText);

        if (isTextToSpeechPlayingAudio)
        {
            return new CompanionOverlayPresentationState(
                "Speaking",
                "Press Ctrl + Alt to interrupt",
                bestTranscriptText,
                bestResponseText,
                0,
                false,
                hasTranscript,
                hasResponse);
        }

        if (isTextToSpeechFetchingAudio)
        {
            return new CompanionOverlayPresentationState(
                "Preparing voice",
                "Generating speech",
                bestTranscriptText,
                bestResponseText,
                0,
                false,
                hasTranscript,
                hasResponse);
        }

        if (isCapturingScreens)
        {
            return new CompanionOverlayPresentationState(
                "Looking",
                "Capturing connected displays",
                bestTranscriptText,
                "Looking at your screen...",
                0,
                false,
                hasTranscript,
                true);
        }

        if (isClaudeResponding)
        {
            return new CompanionOverlayPresentationState(
                "",
                string.IsNullOrWhiteSpace(claudeResponseText) ? "" : "Streaming response",
                bestTranscriptText,
                bestResponseText,
                0,
                false,
                hasTranscript,
                hasResponse,
                shouldShowThinkingAnimation: true);
        }

        if (isTranscriptionConnecting)
        {
            return new CompanionOverlayPresentationState(
                "Connecting",
                "Opening transcript stream",
                bestTranscriptText,
                "",
                0,
                false,
                hasTranscript,
                false);
        }

        if (isMicrophoneCapturing || isTranscriptionStreaming || isPushToTalkPressed)
        {
            return new CompanionOverlayPresentationState(
                "Listening",
                isPushToTalkMonitoring ? "Release Ctrl + Alt to send" : "Shortcut unavailable",
                bestTranscriptText,
                "",
                currentAudioLevel,
                true,
                hasTranscript,
                false);
        }

        CompanionOverlayPresentationState? errorPresentationState =
            CreateErrorPresentationState(bestTranscriptText, bestResponseText, hasTranscript, hasResponse);

        if (errorPresentationState is not null)
        {
            return errorPresentationState;
        }

        if (isPersistentCopyPasteOverlayVisible
            && !string.IsNullOrWhiteSpace(persistentCopyPasteResponseText))
        {
            return new CompanionOverlayPresentationState(
                "Ready to paste",
                persistentCopyPasteDetailText,
                "",
                persistentCopyPasteResponseText,
                0,
                false,
                false,
                true,
                shouldUseCopyPasteLayout: true);
        }

        return null;
    }

    private CompanionOverlayPresentationState? CreateErrorPresentationState(
        string bestTranscriptText,
        string bestResponseText,
        bool hasTranscript,
        bool hasResponse)
    {
        if (!isPushToTalkMonitoring && !string.IsNullOrWhiteSpace(pushToTalkMonitoringErrorMessage))
        {
            return CreateErrorPresentationState(
                "Shortcut unavailable",
                pushToTalkMonitoringErrorMessage,
                bestTranscriptText,
                hasTranscript);
        }

        if (!string.IsNullOrWhiteSpace(microphoneCaptureErrorMessage))
        {
            return CreateErrorPresentationState(
                "Mic unavailable",
                microphoneCaptureErrorMessage,
                bestTranscriptText,
                hasTranscript);
        }

        if (!string.IsNullOrWhiteSpace(transcriptionErrorMessage))
        {
            return CreateErrorPresentationState(
                "Transcription unavailable",
                transcriptionErrorMessage,
                bestTranscriptText,
                hasTranscript);
        }

        if (!string.IsNullOrWhiteSpace(claudeResponseErrorMessage))
        {
            return new CompanionOverlayPresentationState(
                "AI unavailable",
                "See Buddy log for details",
                bestTranscriptText,
                FormatErrorResponseText(claudeResponseErrorMessage, bestResponseText),
                0,
                false,
                hasTranscript,
                true);
        }

        if (!string.IsNullOrWhiteSpace(textToSpeechPlaybackErrorMessage))
        {
            return new CompanionOverlayPresentationState(
                "Voice unavailable",
                "See Buddy log for details",
                bestTranscriptText,
                FormatErrorResponseText(textToSpeechPlaybackErrorMessage, bestResponseText),
                0,
                false,
                hasTranscript,
                true);
        }

        if (!string.IsNullOrWhiteSpace(screenCaptureErrorMessage) && !hasResponse)
        {
            return CreateErrorPresentationState(
                "Screen capture failed",
                screenCaptureErrorMessage,
                bestTranscriptText,
                hasTranscript);
        }

        return null;
    }

    private static CompanionOverlayPresentationState CreateErrorPresentationState(
        string statusText,
        string errorMessage,
        string transcriptText,
        bool shouldShowTranscript)
    {
        return new CompanionOverlayPresentationState(
            statusText,
            "See Buddy log for details",
            transcriptText,
            FormatErrorResponseText(errorMessage, ""),
            0,
            false,
            shouldShowTranscript,
            true);
    }

    private static string FormatErrorResponseText(string errorMessage, string fallbackResponseText)
    {
        if (!string.IsNullOrWhiteSpace(fallbackResponseText)
            && fallbackResponseText.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
        {
            return fallbackResponseText;
        }

        return $"Error: {errorMessage}";
    }

    private string BestAvailableTranscriptText()
    {
        if (!string.IsNullOrWhiteSpace(claudeUserTranscriptText))
        {
            return claudeUserTranscriptText;
        }

        if (!string.IsNullOrWhiteSpace(liveTranscriptText))
        {
            return liveTranscriptText;
        }

        if (!string.IsNullOrWhiteSpace(finalTranscriptText))
        {
            return finalTranscriptText;
        }

        return "";
    }

    private string BestAvailableResponseText()
    {
        if (!string.IsNullOrWhiteSpace(claudeResponseText))
        {
            return claudeResponseText;
        }

        if (!string.IsNullOrWhiteSpace(textToSpeechSpokenText))
        {
            return textToSpeechSpokenText;
        }

        return "";
    }

    private void RememberGuidedTaskContext(
        string currentUserTranscriptText,
        string currentVisibleResponseText)
    {
        if (!IsGuidedPointingFollowUpPrompt(currentUserTranscriptText))
        {
            activeGuidedTaskRequestText = currentUserTranscriptText;
            activeGuidedTaskStepNumber = 1;
        }
        else if (string.IsNullOrWhiteSpace(activeGuidedTaskRequestText))
        {
            activeGuidedTaskRequestText = "continue the current desktop task";
        }

        if (!string.IsNullOrWhiteSpace(currentVisibleResponseText))
        {
            latestGuidedVisibleInstructionText = currentVisibleResponseText;
        }
    }

    private static PointingInstruction? LatestPointingInstruction(
        IReadOnlyList<PointingInstruction> pointingInstructions)
    {
        return pointingInstructions.Count == 0
            ? null
            : pointingInstructions[^1];
    }

    private static string CreatePointingInstructionSignature(PointingInstruction pointingInstruction)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}:{1:0.###},{2:0.###}:{3}",
            pointingInstruction.ScreenNumber,
            pointingInstruction.XInScreenPixels,
            pointingInstruction.YInScreenPixels,
            pointingInstruction.Label);
    }

    private void StartWaitingForUserActionAtPointingTarget(PointingInstruction pointingInstruction)
    {
        pointingInstructionWaitingForUserAction = pointingInstruction;
        InstallMouseHook();
        // Auto-fade if the user never clicks. Mirrors the macOS overlay's bubble timeout
        // so a stale or imprecise pointing target does not freeze the workflow.
        pointingInstructionAutoFadeTimer.Stop();
        pointingInstructionAutoFadeTimer.Start();
    }

    private void StopWaitingForPointingStepCompletion()
    {
        pointingActionCompletionSettleTimer.Stop();
        pointingInstructionAutoFadeTimer.Stop();
        pointingInstructionWaitingForUserAction = null;
        if (!isPersistentCopyPasteOverlayVisible)
        {
            UninstallMouseHook();
        }
    }

    private void ShowPersistentCopyPasteOverlay(string responseText)
    {
        string trimmedResponseText = responseText.Trim();

        if (string.IsNullOrWhiteSpace(trimmedResponseText))
        {
            ClearPersistentCopyPasteOverlay();
            return;
        }

        persistentCopyPasteResponseText = trimmedResponseText;
        persistentCopyPasteDetailText = TryCopyTextToClipboard(trimmedResponseText)
            ? "Copied to clipboard. Paste it, or press Esc to close"
            : "Keep this open while you copy it, or press Esc to close";
        isPersistentCopyPasteOverlayVisible = true;
        InstallKeyboardHook();
    }

    private void ClearPersistentCopyPasteOverlay()
    {
        copyPasteOverlayPasteDismissTimer.Stop();
        isPersistentCopyPasteOverlayVisible = false;
        persistentCopyPasteResponseText = "";
        persistentCopyPasteDetailText = "";
        UninstallKeyboardHook();

        if (pointingInstructionWaitingForUserAction is null)
        {
            UninstallMouseHook();
        }
    }

    private void DismissPersistentCopyPasteOverlay()
    {
        if (!isPersistentCopyPasteOverlayVisible)
        {
            return;
        }

        ClearPersistentCopyPasteOverlay();

        if (CreateActivePresentationState() is null)
        {
            overlayWindow?.Dismiss();
            return;
        }

        RenderOverlay();
    }

    private void StartCopyPasteOverlayDismissAfterPaste()
    {
        if (!isPersistentCopyPasteOverlayVisible)
        {
            return;
        }

        copyPasteOverlayPasteDismissTimer.Stop();
        copyPasteOverlayPasteDismissTimer.Start();
    }

    private static bool TryCopyTextToClipboard(string textToCopy)
    {
        try
        {
            System.Windows.Clipboard.SetText(textToCopy);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ShouldPersistResponseForCopyPaste(
        string responseText,
        string userTranscriptText)
    {
        string trimmedResponseText = responseText.Trim();

        if (string.IsNullOrWhiteSpace(trimmedResponseText))
        {
            return false;
        }

        string normalizedUserTranscriptText = userTranscriptText.ToLowerInvariant();

        foreach (string copyPasteIntentTerm in CopyPasteIntentTerms)
        {
            if (normalizedUserTranscriptText.Contains(copyPasteIntentTerm, StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (trimmedResponseText.Length >= 120
            || trimmedResponseText.Contains('\n')
            || trimmedResponseText.Contains(';')
            || trimmedResponseText.Contains('{')
            || trimmedResponseText.Contains('}')
            || trimmedResponseText.Contains("://", StringComparison.Ordinal))
        {
            return true;
        }

        string normalizedResponseText = trimmedResponseText.ToLowerInvariant();
        return normalizedResponseText.Contains("dotnet ", StringComparison.Ordinal)
            || normalizedResponseText.Contains("npm ", StringComparison.Ordinal)
            || normalizedResponseText.Contains("git ", StringComparison.Ordinal)
            || normalizedResponseText.Contains("python ", StringComparison.Ordinal)
            || normalizedResponseText.Contains("powershell", StringComparison.Ordinal)
            || normalizedResponseText.Contains("function ", StringComparison.Ordinal)
            || normalizedResponseText.Contains("class ", StringComparison.Ordinal)
            || normalizedResponseText.Contains("const ", StringComparison.Ordinal)
            || normalizedResponseText.Contains("let ", StringComparison.Ordinal);
    }

    private string CreatePointingInstructionText(PointingInstruction pointingInstruction)
    {
        string instructionText = BestAvailableResponseText();

        if (string.IsNullOrWhiteSpace(instructionText))
        {
            instructionText = pointingInstruction.Label;
        }

        return TrimPointingInstructionText(instructionText);
    }

    private static string TrimPointingInstructionText(string instructionText)
    {
        string normalizedInstructionText = string.Join(
            " ",
            instructionText.Split(
                new[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries));

        if (normalizedInstructionText.Length <= MaximumPointingInstructionTextLength)
        {
            return normalizedInstructionText;
        }

        int preferredCutIndex = normalizedInstructionText.LastIndexOf(
            ' ',
            MaximumPointingInstructionTextLength - 4);

        if (preferredCutIndex < MaximumPointingInstructionTextLength / 2)
        {
            preferredCutIndex = MaximumPointingInstructionTextLength - 3;
        }

        return normalizedInstructionText[..preferredCutIndex].TrimEnd() + "...";
    }

    private static bool IsPointingTargetScreenAvailable(PointingInstruction pointingInstruction)
    {
        // ScreenNumber <= 0 is the "auto / use cursor's current screen" sentinel produced
        // when the model omits the trailing :screenN — always resolvable, so accept it.
        if (pointingInstruction.ScreenNumber <= 0)
        {
            return true;
        }

        int screenIndex = pointingInstruction.ScreenNumber - 1;
        return screenIndex >= 0 && screenIndex < Forms.Screen.AllScreens.Length;
    }

    private IntPtr HandleMouseEvent(int mouseEventCode, IntPtr messageIdentifier, IntPtr mouseEventData)
    {
        if (mouseEventCode >= 0)
        {
            int mouseMessageIdentifier = messageIdentifier.ToInt32();

            if (IsMouseActionCompletionMessage(mouseMessageIdentifier)
                && pointingInstructionWaitingForUserAction is not null)
            {
                // Any click anywhere advances the guided task. Mirrors the macOS overlay's
                // simple flow: don't gate on cursor-near-target proximity (which fails when
                // POINT coordinates are imprecise) and don't require a confirmation click.
                System.Windows.Application.Current.Dispatcher.BeginInvoke(
                    new Action(AdvanceFromPointingStepAfterUserClick));
            }
        }

        return CallNextHookEx(mouseHookHandle, mouseEventCode, messageIdentifier, mouseEventData);
    }

    private IntPtr HandleKeyboardEvent(int keyboardEventCode, IntPtr messageIdentifier, IntPtr keyboardEventData)
    {
        if (keyboardEventCode >= 0 && isPersistentCopyPasteOverlayVisible)
        {
            int keyboardMessageIdentifier = messageIdentifier.ToInt32();

            if (IsEscapeKeyDown(keyboardMessageIdentifier, keyboardEventData))
            {
                BuddyLog.Workflow("Persistent copy/paste overlay dismissed with Escape.");
                System.Windows.Application.Current.Dispatcher.BeginInvoke(
                    new Action(DismissPersistentCopyPasteOverlay));
                return new IntPtr(1);
            }

            if (IsPasteShortcut(keyboardMessageIdentifier, keyboardEventData))
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(
                    new Action(StartCopyPasteOverlayDismissAfterPaste));
            }
        }

        return CallNextHookEx(keyboardHookHandle, keyboardEventCode, messageIdentifier, keyboardEventData);
    }

    /// <summary>
    /// Called when the user clicks anywhere while a pointing instruction is on screen.
    /// Stops the auto-fade hold, releases the mouse hook, and starts the short settle
    /// timer so Buddy can verify the new screen state and advance the guided task.
    /// </summary>
    private void AdvanceFromPointingStepAfterUserClick()
    {
        if (pointingInstructionWaitingForUserAction is null)
        {
            return;
        }

        pointingInstructionAutoFadeTimer.Stop();
        UninstallMouseHook();
        overlayWindow?.UpdatePointingInstructionText("checking the next step...");
        pointingActionCompletionSettleTimer.Stop();
        pointingActionCompletionSettleTimer.Start();
    }

    private void InstallMouseHook()
    {
        if (mouseHookHandle != IntPtr.Zero)
        {
            return;
        }

        using Process currentProcess = Process.GetCurrentProcess();
        ProcessModule? currentProcessModule = currentProcess.MainModule;
        IntPtr currentProcessModuleHandle = currentProcessModule is null
            ? IntPtr.Zero
            : GetModuleHandle(currentProcessModule.ModuleName);

        mouseHookHandle = SetWindowsHookEx(
            LowLevelMouseHookIdentifier,
            mouseProcedure,
            currentProcessModuleHandle,
            0);
    }

    private void UninstallMouseHook()
    {
        if (mouseHookHandle == IntPtr.Zero)
        {
            return;
        }

        _ = UnhookWindowsHookEx(mouseHookHandle);
        mouseHookHandle = IntPtr.Zero;
    }

    private void InstallKeyboardHook()
    {
        if (keyboardHookHandle != IntPtr.Zero)
        {
            return;
        }

        using Process currentProcess = Process.GetCurrentProcess();
        ProcessModule? currentProcessModule = currentProcess.MainModule;
        IntPtr currentProcessModuleHandle = currentProcessModule is null
            ? IntPtr.Zero
            : GetModuleHandle(currentProcessModule.ModuleName);

        keyboardHookHandle = SetWindowsHookExForKeyboard(
            LowLevelKeyboardHookIdentifier,
            keyboardProcedure,
            currentProcessModuleHandle,
            0);
    }

    private void UninstallKeyboardHook()
    {
        if (keyboardHookHandle == IntPtr.Zero)
        {
            return;
        }

        _ = UnhookWindowsHookEx(keyboardHookHandle);
        keyboardHookHandle = IntPtr.Zero;
    }

    private static string CreateGuidedPointingFollowUpPrompt(
        PointingInstruction completedPointingInstruction,
        GuidedPointingFollowUpReason followUpReason,
        string originalTaskText,
        string previousVisibleInstructionText,
        int nextGuidedTaskStepNumber)
    {
        string targetLabel = string.IsNullOrWhiteSpace(completedPointingInstruction.Label)
            ? "the target"
            : completedPointingInstruction.Label;
        string normalizedOriginalTaskText = NormalizeGuidedPromptContextText(
            originalTaskText,
            "continue the current desktop task");
        string normalizedPreviousInstructionText = NormalizeGuidedPromptContextText(
            previousVisibleInstructionText,
            "no previous visible instruction was captured");
        string progressDescription = followUpReason switch
        {
            GuidedPointingFollowUpReason.UserActionCompletedAtTarget =>
                string.Format(
                    CultureInfo.InvariantCulture,
                    "i clicked or acted on {0}.",
                    targetLabel),
            _ =>
                string.Format(
                    CultureInfo.InvariantCulture,
                    "i clicked or acted on {0}.",
                    targetLabel)
        };

        return string.Format(
            CultureInfo.InvariantCulture,
            "continue the same guided desktop task. original user task: {0}. last buddy instruction shown to the user: {1}. progress for step {2}: {3} you are the fast follow-up model, so preserve that context and do not restart the task. use this fresh screenshot to verify the live screen before continuing. if the whole task is finished, say it is done and append [POINT:none]. otherwise give only the next visible step now and append one precise [POINT:x,y:label:screenN] tag, even if the screen did not change.",
            normalizedOriginalTaskText,
            normalizedPreviousInstructionText,
            nextGuidedTaskStepNumber,
            progressDescription);
    }

    private static bool IsGuidedPointingFollowUpPrompt(string promptText)
    {
        return promptText.StartsWith(
            "continue the same guided desktop task.",
            StringComparison.OrdinalIgnoreCase)
            || promptText.StartsWith(
                "continue the guided desktop task.",
                StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeGuidedPromptContextText(
        string contextText,
        string fallbackText)
    {
        string normalizedContextText = string.Join(
            " ",
            contextText.Split(
                new[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries));

        if (string.IsNullOrWhiteSpace(normalizedContextText))
        {
            return fallbackText;
        }

        const int maximumContextLength = 420;

        return normalizedContextText.Length <= maximumContextLength
            ? normalizedContextText
            : normalizedContextText[..maximumContextLength].TrimEnd() + "...";
    }

    private static bool IsMouseActionCompletionMessage(int messageIdentifier)
    {
        return messageIdentifier is LeftButtonUpMessageIdentifier
            or RightButtonUpMessageIdentifier
            or MiddleButtonUpMessageIdentifier;
    }

    private static bool IsPasteShortcut(int messageIdentifier, IntPtr keyboardEventData)
    {
        if (messageIdentifier is not KeyDownMessageIdentifier and not SystemKeyDownMessageIdentifier)
        {
            return false;
        }

        int virtualKeyCode = Marshal.ReadInt32(keyboardEventData);

        return (virtualKeyCode == VVirtualKey && IsVirtualKeyPressed(ControlVirtualKey))
            || (virtualKeyCode == InsertVirtualKey && IsVirtualKeyPressed(ShiftVirtualKey));
    }

    private static bool IsEscapeKeyDown(int messageIdentifier, IntPtr keyboardEventData)
    {
        if (messageIdentifier is not KeyDownMessageIdentifier and not SystemKeyDownMessageIdentifier)
        {
            return false;
        }

        return Marshal.ReadInt32(keyboardEventData) == EscapeVirtualKey;
    }

    private static bool IsVirtualKeyPressed(int virtualKeyCode)
    {
        return (GetKeyState(virtualKeyCode) & 0x8000) != 0;
    }

    private delegate IntPtr LowLevelKeyboardProcedure(
        int keyboardEventCode,
        IntPtr messageIdentifier,
        IntPtr keyboardEventData);

    private delegate IntPtr LowLevelMouseProcedure(
        int mouseEventCode,
        IntPtr messageIdentifier,
        IntPtr mouseEventData);

    private enum GuidedPointingFollowUpReason
    {
        UserActionCompletedAtTarget
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookIdentifier,
        LowLevelMouseProcedure hookProcedure,
        IntPtr moduleHandle,
        uint threadIdentifier);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookEx", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExForKeyboard(
        int hookIdentifier,
        LowLevelKeyboardProcedure hookProcedure,
        IntPtr moduleHandle,
        uint threadIdentifier);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hookHandle,
        int mouseEventCode,
        IntPtr messageIdentifier,
        IntPtr mouseEventData);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKeyCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
