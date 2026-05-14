using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Buddy.Windows.AI;
using Buddy.Windows.Configuration;
using Buddy.Windows.Diagnostics;
using Buddy.Windows.Pointing;
using Buddy.Windows.Screen;
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
    private const int TabVirtualKey = 0x09;
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
    private static readonly TimeSpan PointingInstructionAutoFadeDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CompletedTaskDisplayDelay = TimeSpan.FromSeconds(9);
    private static readonly TimeSpan InactivityHintDelay = TimeSpan.FromSeconds(60);
    private static readonly string[] InactivityHints =
    {
        "Press Ctrl + Alt to ask me anything",
        "Try: 'show me how to open a new tab'",
        "Press Ctrl + Alt + A to automate a task on screen",
        "Press Ctrl + Alt + M to switch the AI model",
        "Right-click the tray icon and choose Quit to exit",
    };
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
    private readonly ClaudeStreamingChatClient claudeStreamingChatClient;
    private readonly WindowsScreenCaptureService windowsScreenCaptureService;
    private readonly WindowsClipboardTextCaptureService windowsClipboardTextCaptureService;
    private readonly Random inactivityHintRandom = new();
    private CancellationTokenSource? inactivitySuggestionCancellationTokenSource;
    private bool isFetchingInactivitySuggestion;
    private readonly DispatcherTimer idleDismissTimer;
    private readonly DispatcherTimer copyPasteOverlayPasteDismissTimer;
    private readonly DispatcherTimer pointingActionCompletionSettleTimer;
    private readonly DispatcherTimer pointingInstructionAutoFadeTimer;
    private readonly DispatcherTimer completedTaskDismissTimer;
    private readonly DispatcherTimer inactivityHintTimer;
    private readonly LowLevelKeyboardProcedure keyboardProcedure;
    private readonly LowLevelMouseProcedure mouseProcedure;
    private CompanionOverlayWindow? overlayWindow;
    private CopyResponseButtonWindow? copyResponseButtonWindow;
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
    private string completedTaskResponseText = "";
    private bool isCompletedTaskResponseVisible;
    private string currentInactivityHint = "";
    private bool isInactivityHintVisible;
    private int nextInactivityHintIndex;
    private bool isDisposed;

    public CompanionOverlayController(
        PushToTalkHotkeyMonitor pushToTalkHotkeyMonitor,
        MicrophoneCaptureService microphoneCaptureService,
        AssemblyAIStreamingTranscriptionService streamingTranscriptionService,
        ClaudeResponseService claudeResponseService,
        ElevenLabsTextToSpeechPlaybackService textToSpeechPlaybackService,
        ClaudeStreamingChatClient claudeStreamingChatClient,
        WindowsScreenCaptureService windowsScreenCaptureService,
        WindowsClipboardTextCaptureService windowsClipboardTextCaptureService)
    {
        this.pushToTalkHotkeyMonitor = pushToTalkHotkeyMonitor;
        this.microphoneCaptureService = microphoneCaptureService;
        this.streamingTranscriptionService = streamingTranscriptionService;
        this.claudeResponseService = claudeResponseService;
        this.textToSpeechPlaybackService = textToSpeechPlaybackService;
        this.claudeStreamingChatClient = claudeStreamingChatClient;
        this.windowsScreenCaptureService = windowsScreenCaptureService;
        this.windowsClipboardTextCaptureService = windowsClipboardTextCaptureService;

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

        completedTaskDismissTimer = new DispatcherTimer
        {
            Interval = CompletedTaskDisplayDelay
        };
        completedTaskDismissTimer.Tick += HandleCompletedTaskDismissTimerTick;

        inactivityHintTimer = new DispatcherTimer
        {
            Interval = InactivityHintDelay
        };
        inactivityHintTimer.Tick += HandleInactivityHintTimerTick;

        keyboardProcedure = HandleKeyboardEvent;
        mouseProcedure = HandleMouseEvent;
    }

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);

        overlayWindow = new CompanionOverlayWindow();
        copyResponseButtonWindow = new CopyResponseButtonWindow();
        isPushToTalkPressed = pushToTalkHotkeyMonitor.IsPushToTalkPressed;
        isPushToTalkMonitoring = pushToTalkHotkeyMonitor.IsMonitoring;

        pushToTalkHotkeyMonitor.PushToTalkHotkeyChanged += HandlePushToTalkHotkeyChanged;
        microphoneCaptureService.CaptureStateChanged += HandleMicrophoneCaptureStateChanged;
        streamingTranscriptionService.TranscriptionStateChanged += HandleStreamingTranscriptionStateChanged;
        claudeResponseService.ResponseStateChanged += HandleClaudeResponseStateChanged;
        textToSpeechPlaybackService.PlaybackStateChanged += HandleTextToSpeechPlaybackStateChanged;
        inactivityHintTimer.Start();
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
        completedTaskDismissTimer.Stop();
        inactivityHintTimer.Stop();
        UninstallKeyboardHook();
        UninstallMouseHook();
        idleDismissTimer.Tick -= HandleIdleDismissTimerTick;
        copyPasteOverlayPasteDismissTimer.Tick -= HandleCopyPasteOverlayPasteDismissTimerTick;
        pointingActionCompletionSettleTimer.Tick -= HandlePointingActionCompletionSettleTimerTick;
        pointingInstructionAutoFadeTimer.Tick -= HandlePointingInstructionAutoFadeTimerTick;
        completedTaskDismissTimer.Tick -= HandleCompletedTaskDismissTimerTick;
        inactivityHintTimer.Tick -= HandleInactivityHintTimerTick;
        pushToTalkHotkeyMonitor.PushToTalkHotkeyChanged -= HandlePushToTalkHotkeyChanged;
        microphoneCaptureService.CaptureStateChanged -= HandleMicrophoneCaptureStateChanged;
        streamingTranscriptionService.TranscriptionStateChanged -= HandleStreamingTranscriptionStateChanged;
        claudeResponseService.ResponseStateChanged -= HandleClaudeResponseStateChanged;
        textToSpeechPlaybackService.PlaybackStateChanged -= HandleTextToSpeechPlaybackStateChanged;

        overlayWindow?.Close();
        overlayWindow = null;
        copyResponseButtonWindow?.Close();
        copyResponseButtonWindow = null;
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
                ResetInactivityTimer();
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
                isCompletedTaskResponseVisible = false;
                completedTaskResponseText = "";
                completedTaskDismissTimer.Stop();
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

            if (eventArguments.IsCapturingScreens)
            {
                ResetInactivityTimer();
                isCompletedTaskResponseVisible = false;
                completedTaskResponseText = "";
                completedTaskDismissTimer.Stop();
            }

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
                bool wasGuidedTask = activeGuidedTaskStepNumber > 0;
                string finalResponseText = eventArguments.ResponseText;

                latestPointingInstruction = null;
                preparedNextPointingInstruction = null;
                preparedNextInstructionText = "";
                isAdvancingGuidedPointingStep = false;
                activeGuidedTaskRequestText = "";
                latestGuidedVisibleInstructionText = "";
                activeGuidedTaskStepNumber = 0;

                if (ShouldPersistResponseForCopyPaste(
                    finalResponseText,
                    eventArguments.UserTranscriptText))
                {
                    ShowPersistentCopyPasteOverlay(finalResponseText);
                }
                else
                {
                    ClearPersistentCopyPasteOverlay();

                    if (wasGuidedTask && !string.IsNullOrWhiteSpace(finalResponseText))
                    {
                        completedTaskResponseText = finalResponseText;
                        isCompletedTaskResponseVisible = true;
                        completedTaskDismissTimer.Stop();
                        completedTaskDismissTimer.Start();
                    }
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

    private void HandleCompletedTaskDismissTimerTick(object? sender, EventArgs eventArguments)
    {
        completedTaskDismissTimer.Stop();
        isCompletedTaskResponseVisible = false;
        completedTaskResponseText = "";
        RenderOverlay();
    }

    private void HandleInactivityHintTimerTick(object? sender, EventArgs eventArguments)
    {
        inactivityHintTimer.Stop();

        // Roughly two out of three firings, try to generate a tip tailored to whatever
        // the user is looking at right now. The other firings rotate through the static
        // hints so the cadence still feels responsive when the AI is unreachable or the
        // focused window has no readable text. If the contextual call is already in
        // flight from a previous tick we skip starting another one.
        bool shouldAttemptContextualSuggestion =
            !isFetchingInactivitySuggestion
            && BuddyWindowsConfiguration.IsWorkerBaseUrlConfigured
            && inactivityHintRandom.Next(3) != 0;

        if (shouldAttemptContextualSuggestion)
        {
            _ = TryShowContextualInactivitySuggestionAsync();
            return;
        }

        ShowStaticInactivityHint();
    }

    private void ShowStaticInactivityHint()
    {
        currentInactivityHint = InactivityHints[nextInactivityHintIndex % InactivityHints.Length];
        nextInactivityHintIndex++;
        isInactivityHintVisible = true;
        RenderOverlay();
    }

    private async Task TryShowContextualInactivitySuggestionAsync()
    {
        CancellationTokenSource suggestionCancellationTokenSource = new();
        suggestionCancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(20));
        CancellationTokenSource? previousSuggestionCancellationTokenSource =
            Interlocked.Exchange(
                ref inactivitySuggestionCancellationTokenSource,
                suggestionCancellationTokenSource);
        previousSuggestionCancellationTokenSource?.Cancel();
        previousSuggestionCancellationTokenSource?.Dispose();
        isFetchingInactivitySuggestion = true;

        try
        {
            CancellationToken suggestionCancellationToken =
                suggestionCancellationTokenSource.Token;

            string? capturedFocusedWindowText = null;

            try
            {
                capturedFocusedWindowText = await windowsClipboardTextCaptureService
                    .CaptureFocusedWindowTextAsync(suggestionCancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception focusedWindowTextCaptureException)
            {
                BuddyLog.Workflow(
                    $"Inactivity suggestion skipped focused-window text capture: {focusedWindowTextCaptureException.Message}");
            }

            IReadOnlyList<WindowsScreenCapture> inactivityScreenCaptures =
                Array.Empty<WindowsScreenCapture>();

            try
            {
                inactivityScreenCaptures = await windowsScreenCaptureService
                    .CaptureAllScreensAsJpegAsync(
                        suggestionCancellationToken,
                        captureCursorScreenOnly: true,
                        jpegQuality: 55L);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception screenCaptureException)
            {
                BuddyLog.Workflow(
                    $"Inactivity suggestion skipped screen capture: {screenCaptureException.Message}");
            }

            if (string.IsNullOrWhiteSpace(capturedFocusedWindowText)
                && inactivityScreenCaptures.Count == 0)
            {
                ShowStaticInactivityHintOnUiThread();
                return;
            }

            string? suggestionText = await claudeStreamingChatClient
                .StreamShortContextualSuggestionAsync(
                    capturedFocusedWindowText,
                    inactivityScreenCaptures,
                    suggestionCancellationToken);

            string? normalizedSuggestionText = NormalizeInactivitySuggestion(suggestionText);

            if (string.IsNullOrWhiteSpace(normalizedSuggestionText))
            {
                ShowStaticInactivityHintOnUiThread();
                return;
            }

            BuddyLog.Workflow(
                $"Inactivity suggestion: {BuddyLog.DescribeTextForLog(normalizedSuggestionText)}");

            _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                currentInactivityHint = normalizedSuggestionText;
                isInactivityHintVisible = true;
                RenderOverlay();
            }));
        }
        catch (OperationCanceledException)
        {
            // Cancelled because the user became active again or another tick replaced us.
        }
        catch (Exception suggestionException)
        {
            BuddyLog.Error("Inactivity contextual suggestion failed", suggestionException);
            ShowStaticInactivityHintOnUiThread();
        }
        finally
        {
            isFetchingInactivitySuggestion = false;

            if (ReferenceEquals(
                Interlocked.CompareExchange(
                    ref inactivitySuggestionCancellationTokenSource,
                    null,
                    suggestionCancellationTokenSource),
                suggestionCancellationTokenSource))
            {
                suggestionCancellationTokenSource.Dispose();
            }
        }
    }

    private void ShowStaticInactivityHintOnUiThread()
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(
            new Action(ShowStaticInactivityHint));
    }

    private static string? NormalizeInactivitySuggestion(string? rawSuggestion)
    {
        if (string.IsNullOrWhiteSpace(rawSuggestion))
        {
            return null;
        }

        string trimmedSuggestion = rawSuggestion.Trim().Trim('"', '\'', '`');
        int firstNewlineIndex = trimmedSuggestion.IndexOf('\n');

        if (firstNewlineIndex >= 0)
        {
            trimmedSuggestion = trimmedSuggestion[..firstNewlineIndex].Trim();
        }

        if (trimmedSuggestion.Equals("skip", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (trimmedSuggestion.Length > 120)
        {
            int sentenceBreakIndex = trimmedSuggestion.IndexOfAny(new[] { '.', '!', '?' });
            trimmedSuggestion = sentenceBreakIndex > 0
                ? trimmedSuggestion[..(sentenceBreakIndex + 1)].Trim()
                : trimmedSuggestion[..120].Trim() + "...";
        }

        return string.IsNullOrWhiteSpace(trimmedSuggestion) ? null : trimmedSuggestion;
    }

    private void ResetInactivityTimer()
    {
        isInactivityHintVisible = false;
        currentInactivityHint = "";
        inactivityHintTimer.Stop();
        inactivityHintTimer.Start();

        CancellationTokenSource? inflightSuggestionCancellationTokenSource =
            Interlocked.Exchange(ref inactivitySuggestionCancellationTokenSource, null);
        inflightSuggestionCancellationTokenSource?.Cancel();
        inflightSuggestionCancellationTokenSource?.Dispose();
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
        UpdateCopyResponseButtonForPresentationState(presentationState);

        if (latestPointingInstruction is not null)
        {
            idleDismissTimer.Stop();
            RenderPointingInstruction();
            return;
        }

        if (isAdvancingGuidedPointingStep)
        {
            idleDismissTimer.Stop();
            string carriedOverInstructionText = !string.IsNullOrWhiteSpace(latestGuidedVisibleInstructionText)
                ? latestGuidedVisibleInstructionText
                : claudeResponseText;
            bool hasCarriedOverInstructionText =
                !string.IsNullOrWhiteSpace(carriedOverInstructionText);
            overlayWindow.Present(new CompanionOverlayPresentationState(
                "",
                "",
                claudeUserTranscriptText,
                carriedOverInstructionText,
                0,
                false,
                !string.IsNullOrWhiteSpace(claudeUserTranscriptText),
                hasCarriedOverInstructionText,
                shouldShowThinkingAnimation: true));
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

    private void UpdateCopyResponseButtonForPresentationState(
        CompanionOverlayPresentationState? presentationState)
    {
        if (copyResponseButtonWindow is null)
        {
            return;
        }

        if (presentationState is null
            || !presentationState.ShouldShowResponse
            || string.IsNullOrWhiteSpace(presentationState.ResponseText))
        {
            copyResponseButtonWindow.HideButton();
            return;
        }

        copyResponseButtonWindow.ShowForResponse(presentationState.ResponseText);
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

        if (isCompletedTaskResponseVisible && !string.IsNullOrWhiteSpace(completedTaskResponseText))
        {
            return new CompanionOverlayPresentationState(
                "Done",
                "Press Ctrl + Alt if you need help with the next step",
                "",
                completedTaskResponseText,
                0,
                false,
                false,
                true);
        }

        if (isInactivityHintVisible && !string.IsNullOrWhiteSpace(currentInactivityHint))
        {
            return new CompanionOverlayPresentationState(
                "Tip",
                currentInactivityHint,
                "",
                "",
                0,
                false,
                false,
                false);
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
        InstallKeyboardHook();
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
            UninstallKeyboardHook();
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
        if (keyboardEventCode >= 0)
        {
            int keyboardMessageIdentifier = messageIdentifier.ToInt32();

            if (isPersistentCopyPasteOverlayVisible)
            {
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

            if (pointingInstructionWaitingForUserAction is not null
                && IsTabKeyDown(keyboardMessageIdentifier, keyboardEventData))
            {
                BuddyLog.Workflow("Tab pressed while pointing — requesting step-by-step help.");
                System.Windows.Application.Current.Dispatcher.BeginInvoke(
                    new Action(RequestStepByStepHelpForActivePointingStep));
                return new IntPtr(1);
            }
        }

        return CallNextHookEx(keyboardHookHandle, keyboardEventCode, messageIdentifier, keyboardEventData);
    }

    /// <summary>
    /// Called when the user clicks anywhere while a pointing instruction is on screen.
    /// Stops the auto-fade hold, releases the mouse hook, and starts the short settle
    /// timer so Buddy can verify the new screen state and advance the guided task. If
    /// the response asks the user to type or paste content, the click is treated as
    /// "user is placing their cursor to start typing" rather than "step done" — we keep
    /// the instruction visible so the user can read what to type, and switch to the
    /// persistent copy/paste overlay so the response stays anchored until they press
    /// Esc or trigger the next request with Ctrl+Alt.
    /// </summary>
    private void AdvanceFromPointingStepAfterUserClick()
    {
        if (pointingInstructionWaitingForUserAction is null)
        {
            return;
        }

        string currentResponseTextSnapshot = string.IsNullOrWhiteSpace(claudeResponseText)
            ? latestGuidedVisibleInstructionText
            : claudeResponseText;

        if (ShouldKeepResponseVisibleForUserTyping(
            currentResponseTextSnapshot,
            claudeUserTranscriptText))
        {
            BuddyLog.Workflow(
                "Pointing click did not advance: response asks the user to type or paste, keeping it visible.");
            pointingInstructionAutoFadeTimer.Stop();
            UninstallMouseHook();
            pointingInstructionWaitingForUserAction = null;
            latestPointingInstruction = null;
            preparedNextPointingInstruction = null;
            preparedNextInstructionText = "";
            isCompanionPanelSuppressedForPointing = false;
            isAdvancingGuidedPointingStep = false;
            // Animate the cursor back to the user's real mouse, then switch to the
            // persistent copy/paste overlay so the response (and any code it contains)
            // stays on screen until the user paste it or dismisses with Escape.
            overlayWindow?.ClearPointingCursor(animateReturnToCursor: true, onCompleted: () =>
            {
                ShowPersistentCopyPasteOverlay(currentResponseTextSnapshot);
                RenderOverlay();
            });
            return;
        }

        pointingInstructionAutoFadeTimer.Stop();
        UninstallMouseHook();
        overlayWindow?.UpdatePointingInstructionText("checking the next step...");
        pointingActionCompletionSettleTimer.Stop();
        pointingActionCompletionSettleTimer.Start();
    }

    private static bool ShouldKeepResponseVisibleForUserTyping(
        string responseText,
        string userTranscriptText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return false;
        }

        if (ShouldPersistResponseForCopyPaste(responseText, userTranscriptText))
        {
            return true;
        }

        string normalizedResponseText = responseText.ToLowerInvariant();
        return normalizedResponseText.Contains("type ", StringComparison.Ordinal)
            || normalizedResponseText.Contains("then type", StringComparison.Ordinal)
            || normalizedResponseText.Contains("enter the ", StringComparison.Ordinal)
            || normalizedResponseText.Contains("enter:", StringComparison.Ordinal)
            || normalizedResponseText.Contains("write the ", StringComparison.Ordinal)
            || normalizedResponseText.Contains("write:", StringComparison.Ordinal)
            || normalizedResponseText.Contains("paste ", StringComparison.Ordinal);
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

    private static bool IsTabKeyDown(int messageIdentifier, IntPtr keyboardEventData)
    {
        if (messageIdentifier is not KeyDownMessageIdentifier and not SystemKeyDownMessageIdentifier)
        {
            return false;
        }

        if (Marshal.ReadInt32(keyboardEventData) != TabVirtualKey)
        {
            return false;
        }

        // Don't intercept Ctrl+Tab or Alt+Tab — those are used for window/tab switching and
        // belong to the user's app, not Buddy. Plain Tab while a Buddy bubble is on screen
        // is the dedicated step-by-step trigger.
        return !IsVirtualKeyPressed(ControlVirtualKey)
            && !IsVirtualKeyPressed(0x12); // Alt
    }

    private void RequestStepByStepHelpForActivePointingStep()
    {
        PointingInstruction? activePointingInstruction = pointingInstructionWaitingForUserAction
            ?? latestPointingInstruction;

        if (activePointingInstruction is null)
        {
            return;
        }

        string targetLabel = string.IsNullOrWhiteSpace(activePointingInstruction.Label)
            ? "the highlighted target"
            : activePointingInstruction.Label;
        string visibleInstructionContext = string.IsNullOrWhiteSpace(latestGuidedVisibleInstructionText)
            ? claudeResponseText
            : latestGuidedVisibleInstructionText;
        string normalizedVisibleInstructionContext = string.IsNullOrWhiteSpace(visibleInstructionContext)
            ? "no previous visible instruction was captured"
            : visibleInstructionContext;
        string stepByStepFollowUpPromptText = string.Format(
            CultureInfo.InvariantCulture,
            "the user pressed tab on the previous step because they want a more detailed step-by-step explanation. previous instruction shown: \"{0}\". current target: \"{1}\". reply with three to six numbered micro-steps in plain language explaining exactly how to perform this single action (where to look on screen, what to click, what happens after), then append one fresh [POINT:x,y:label:screenN] tag for the immediate action so buddy keeps pointing.",
            normalizedVisibleInstructionContext,
            targetLabel);

        // Cancel auto-fade so the help arrives before the bubble disappears, and keep the
        // bubble label informative while we wait.
        pointingInstructionAutoFadeTimer.Stop();
        overlayWindow?.UpdatePointingInstructionText("getting a step-by-step for you...");
        textToSpeechPlaybackService.StopPlayback();
        claudeResponseService.SendUserTranscript(stepByStepFollowUpPromptText, preferFastModel: false);
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
