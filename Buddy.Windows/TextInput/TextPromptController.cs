using System;
using System.Windows;
using Buddy.Windows.AI;
using Buddy.Windows.Diagnostics;
using Buddy.Windows.Voice;

namespace Buddy.Windows.TextInput;

public sealed class TextPromptController : IDisposable
{
    private readonly PushToTalkHotkeyMonitor pushToTalkHotkeyMonitor;
    private readonly MicrophoneCaptureService microphoneCaptureService;
    private readonly AssemblyAIStreamingTranscriptionService streamingTranscriptionService;
    private readonly ClaudeResponseService claudeResponseService;
    private readonly ElevenLabsTextToSpeechPlaybackService textToSpeechPlaybackService;
    private TextPromptWindow? textPromptWindow;
    private bool isDisposed;

    public TextPromptController(
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
    }

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);

        pushToTalkHotkeyMonitor.TextPromptHotkeyPressed += HandleTextPromptHotkeyPressed;
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        pushToTalkHotkeyMonitor.TextPromptHotkeyPressed -= HandleTextPromptHotkeyPressed;

        if (textPromptWindow is not null)
        {
            textPromptWindow.PromptSubmitted -= HandlePromptSubmitted;
            textPromptWindow.Close();
            textPromptWindow = null;
        }
    }

    private void HandleTextPromptHotkeyPressed(object? sender, EventArgs eventArguments)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            BuddyLog.Workflow("Typed prompt workflow opening prompt window.");
            StopActiveVoiceInteraction();
            EnsureTextPromptWindow();
            textPromptWindow?.ShowNearCurrentCursor();
        }));
    }

    private void HandlePromptSubmitted(object? sender, TextPromptSubmittedEventArgs eventArguments)
    {
        BuddyLog.Workflow(
            $"Typed prompt workflow submitted prompt: {BuddyLog.DescribeTextForLog(eventArguments.PromptText)}.");
        StopActiveVoiceInteraction();
        claudeResponseService.SendUserTranscript(eventArguments.PromptText);
    }

    private void EnsureTextPromptWindow()
    {
        if (textPromptWindow is not null)
        {
            return;
        }

        textPromptWindow = new TextPromptWindow();
        textPromptWindow.PromptSubmitted += HandlePromptSubmitted;
        textPromptWindow.Closed += (_, _) =>
        {
            if (textPromptWindow is not null)
            {
                textPromptWindow.PromptSubmitted -= HandlePromptSubmitted;
                textPromptWindow = null;
            }
        };
    }

    private void StopActiveVoiceInteraction()
    {
        BuddyLog.Workflow("Typed prompt workflow stopping active voice/AI/TTS work.");
        microphoneCaptureService.StopCapture();
        streamingTranscriptionService.CancelSession();
        claudeResponseService.CancelResponse();
        textToSpeechPlaybackService.StopPlayback();
    }
}
