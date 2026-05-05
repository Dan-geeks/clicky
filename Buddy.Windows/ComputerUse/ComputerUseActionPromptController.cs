using System;
using Buddy.Windows.AI;
using Buddy.Windows.Diagnostics;
using Buddy.Windows.TextInput;
using Buddy.Windows.Voice;

namespace Buddy.Windows.ComputerUse;

/// <summary>
/// Owns the Ctrl+Alt+A flow: opens the typed prompt window in "act on my desktop" mode,
/// hands the submitted text to the Computer Use agent coordinator, and stops any active
/// voice/AI/TTS work so the agent has the foreground without competing audio.
/// </summary>
public sealed class ComputerUseActionPromptController : IDisposable
{
    private readonly PushToTalkHotkeyMonitor pushToTalkHotkeyMonitor;
    private readonly MicrophoneCaptureService microphoneCaptureService;
    private readonly AssemblyAIStreamingTranscriptionService streamingTranscriptionService;
    private readonly ClaudeResponseService claudeResponseService;
    private readonly ElevenLabsTextToSpeechPlaybackService textToSpeechPlaybackService;
    private readonly ComputerUseAgentCoordinator computerUseAgentCoordinator;
    private TextPromptWindow? actionPromptWindow;
    private bool isDisposed;

    public ComputerUseActionPromptController(
        PushToTalkHotkeyMonitor pushToTalkHotkeyMonitor,
        MicrophoneCaptureService microphoneCaptureService,
        AssemblyAIStreamingTranscriptionService streamingTranscriptionService,
        ClaudeResponseService claudeResponseService,
        ElevenLabsTextToSpeechPlaybackService textToSpeechPlaybackService,
        ComputerUseAgentCoordinator computerUseAgentCoordinator)
    {
        this.pushToTalkHotkeyMonitor = pushToTalkHotkeyMonitor;
        this.microphoneCaptureService = microphoneCaptureService;
        this.streamingTranscriptionService = streamingTranscriptionService;
        this.claudeResponseService = claudeResponseService;
        this.textToSpeechPlaybackService = textToSpeechPlaybackService;
        this.computerUseAgentCoordinator = computerUseAgentCoordinator;
    }

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);

        pushToTalkHotkeyMonitor.ActionModeHotkeyPressed += HandleActionModeHotkeyPressed;
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        pushToTalkHotkeyMonitor.ActionModeHotkeyPressed -= HandleActionModeHotkeyPressed;

        if (actionPromptWindow is not null)
        {
            actionPromptWindow.PromptSubmitted -= HandleActionPromptSubmitted;
            actionPromptWindow.Close();
            actionPromptWindow = null;
        }
    }

    private void HandleActionModeHotkeyPressed(object? sender, EventArgs eventArguments)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            BuddyLog.Workflow("Computer Use action prompt opening (Ctrl+Alt+A).");
            StopActiveBackgroundWork();
            EnsureActionPromptWindow();
            actionPromptWindow?.ApplyMode(TextPromptMode.ActOnDesktop);
            actionPromptWindow?.ShowNearCurrentCursor();
        }));
    }

    private void HandleActionPromptSubmitted(object? sender, TextPromptSubmittedEventArgs eventArguments)
    {
        BuddyLog.Workflow(
            $"Computer Use action prompt submitted: {BuddyLog.DescribeTextForLog(eventArguments.PromptText)}.");
        StopActiveBackgroundWork();
        computerUseAgentCoordinator.StartAgentRun(eventArguments.PromptText);
    }

    private void EnsureActionPromptWindow()
    {
        if (actionPromptWindow is not null)
        {
            return;
        }

        actionPromptWindow = new TextPromptWindow();
        actionPromptWindow.PromptSubmitted += HandleActionPromptSubmitted;
        actionPromptWindow.Closed += (_, _) =>
        {
            if (actionPromptWindow is not null)
            {
                actionPromptWindow.PromptSubmitted -= HandleActionPromptSubmitted;
                actionPromptWindow = null;
            }
        };
    }

    private void StopActiveBackgroundWork()
    {
        BuddyLog.Workflow("Computer Use action prompt stopping active voice/AI/TTS work.");
        microphoneCaptureService.StopCapture();
        streamingTranscriptionService.CancelSession();
        claudeResponseService.CancelResponse();
        textToSpeechPlaybackService.StopPlayback();
        computerUseAgentCoordinator.CancelAgentRun();
    }
}
