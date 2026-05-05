using System;
using System.Threading;
using System.Threading.Tasks;
using Buddy.Windows.AI;
using Buddy.Windows.Diagnostics;

namespace Buddy.Windows.Voice;

public sealed class PushToTalkVoiceCaptureCoordinator : IDisposable
{
    private static readonly TimeSpan MicrophoneCaptureStartDelay = TimeSpan.FromMilliseconds(160);

    private readonly PushToTalkHotkeyMonitor pushToTalkHotkeyMonitor;
    private readonly MicrophoneCaptureService microphoneCaptureService;
    private readonly AssemblyAIStreamingTranscriptionService streamingTranscriptionService;
    private readonly ClaudeResponseService claudeResponseService;
    private readonly ElevenLabsTextToSpeechPlaybackService textToSpeechPlaybackService;
    private readonly object captureTransitionLock = new();
    private Task latestCaptureTransitionTask = Task.CompletedTask;
    private CancellationTokenSource? activeCaptureCancellationTokenSource;
    private string lastSubmittedFinalTranscriptText = "";
    private string lastSpokenClaudeResponseText = "";
    private bool latestRequestedShouldCaptureMicrophone;
    private bool isDisposed;

    public PushToTalkVoiceCaptureCoordinator(
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

        pushToTalkHotkeyMonitor.PushToTalkHotkeyChanged += HandlePushToTalkHotkeyChanged;
        microphoneCaptureService.AudioCaptured += HandleMicrophoneAudioCaptured;
        streamingTranscriptionService.TranscriptionStateChanged += HandleStreamingTranscriptionStateChanged;
        claudeResponseService.ResponseStateChanged += HandleClaudeResponseStateChanged;
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        lock (captureTransitionLock)
        {
            isDisposed = true;
            pushToTalkHotkeyMonitor.PushToTalkHotkeyChanged -= HandlePushToTalkHotkeyChanged;
            microphoneCaptureService.AudioCaptured -= HandleMicrophoneAudioCaptured;
            streamingTranscriptionService.TranscriptionStateChanged -= HandleStreamingTranscriptionStateChanged;
            claudeResponseService.ResponseStateChanged -= HandleClaudeResponseStateChanged;
            activeCaptureCancellationTokenSource?.Cancel();
            activeCaptureCancellationTokenSource?.Dispose();
            activeCaptureCancellationTokenSource = null;
        }

        microphoneCaptureService.StopCapture();
        streamingTranscriptionService.CancelSession();
        claudeResponseService.CancelResponse();
        textToSpeechPlaybackService.StopPlayback();
    }

    private void HandlePushToTalkHotkeyChanged(object? sender, PushToTalkHotkeyChangedEventArgs eventArguments)
    {
        bool shouldCaptureMicrophone = eventArguments.IsMonitoring && eventArguments.IsPushToTalkPressed;

        lock (captureTransitionLock)
        {
            if (isDisposed)
            {
                return;
            }

            latestRequestedShouldCaptureMicrophone = shouldCaptureMicrophone;

            if (!shouldCaptureMicrophone)
            {
                activeCaptureCancellationTokenSource?.Cancel();
            }

            latestCaptureTransitionTask = latestCaptureTransitionTask.ContinueWith(
                _ => ApplyMicrophoneCaptureStateAsync(shouldCaptureMicrophone),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }

    private async Task ApplyMicrophoneCaptureStateAsync(bool shouldCaptureMicrophone)
    {
        CancellationToken activeCaptureCancellationToken;

        lock (captureTransitionLock)
        {
            if (isDisposed)
            {
                return;
            }

            if (shouldCaptureMicrophone)
            {
                if (!latestRequestedShouldCaptureMicrophone)
                {
                    return;
                }

                lastSubmittedFinalTranscriptText = "";
                lastSpokenClaudeResponseText = "";
                activeCaptureCancellationTokenSource?.Dispose();
                activeCaptureCancellationTokenSource = new CancellationTokenSource();
                activeCaptureCancellationToken = activeCaptureCancellationTokenSource.Token;
            }
            else
            {
                activeCaptureCancellationTokenSource?.Cancel();
                activeCaptureCancellationTokenSource?.Dispose();
                activeCaptureCancellationTokenSource = null;
                activeCaptureCancellationToken = CancellationToken.None;
            }
        }

        if (shouldCaptureMicrophone)
        {
            BuddyLog.Workflow("Voice workflow starting: preparing microphone capture and transcription.");

            try
            {
                await Task.Delay(MicrophoneCaptureStartDelay, activeCaptureCancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!ShouldContinueMicrophoneCapture(activeCaptureCancellationToken))
            {
                return;
            }

            claudeResponseService.CancelResponse();
            textToSpeechPlaybackService.StopPlayback();

            BuddyLog.Workflow("Voice workflow opening transcription stream.");
            await streamingTranscriptionService.StartSessionAsync(activeCaptureCancellationToken);

            if (!ShouldContinueMicrophoneCapture(activeCaptureCancellationToken)
                || !streamingTranscriptionService.IsStreaming)
            {
                BuddyLog.Workflow("Voice workflow did not start microphone capture because transcription was not streaming.");
                return;
            }

            BuddyLog.Workflow("Voice workflow starting microphone capture.");
            microphoneCaptureService.StartCapture();
            return;
        }

        BuddyLog.Workflow("Voice workflow stopping capture and requesting final transcript.");
        microphoneCaptureService.StopCapture();
        streamingTranscriptionService.RequestFinalTranscript();
    }

    private void HandleMicrophoneAudioCaptured(object? sender, MicrophoneAudioCapturedEventArgs eventArguments)
    {
        streamingTranscriptionService.AppendAudio(eventArguments);
    }

    private bool ShouldContinueMicrophoneCapture(CancellationToken activeCaptureCancellationToken)
    {
        lock (captureTransitionLock)
        {
            return !isDisposed
                && latestRequestedShouldCaptureMicrophone
                && !activeCaptureCancellationToken.IsCancellationRequested;
        }
    }

    private void HandleStreamingTranscriptionStateChanged(
        object? sender,
        StreamingTranscriptionStateChangedEventArgs eventArguments)
    {
        string completedTranscriptText = eventArguments.FinalTranscriptText.Trim();

        if (string.IsNullOrWhiteSpace(completedTranscriptText))
        {
            return;
        }

        lock (captureTransitionLock)
        {
            if (isDisposed || completedTranscriptText == lastSubmittedFinalTranscriptText)
            {
                return;
            }

            lastSubmittedFinalTranscriptText = completedTranscriptText;
        }

        BuddyLog.Workflow(
            $"Voice workflow final transcript ready: {BuddyLog.DescribeTextForLog(completedTranscriptText)}.");
        claudeResponseService.SendUserTranscript(completedTranscriptText);
    }

    private void HandleClaudeResponseStateChanged(object? sender, ClaudeResponseStateChangedEventArgs eventArguments)
    {
        string completedClaudeResponseText = eventArguments.ResponseText.Trim();

        if (eventArguments.IsResponding
            || !string.IsNullOrWhiteSpace(eventArguments.ResponseErrorMessage)
            || string.IsNullOrWhiteSpace(completedClaudeResponseText))
        {
            return;
        }

        lock (captureTransitionLock)
        {
            if (isDisposed || completedClaudeResponseText == lastSpokenClaudeResponseText)
            {
                return;
            }

            lastSpokenClaudeResponseText = completedClaudeResponseText;
        }

        BuddyLog.Workflow(
            $"Voice workflow sending response to TTS: {BuddyLog.DescribeTextForLog(completedClaudeResponseText)}.");
        textToSpeechPlaybackService.SpeakText(completedClaudeResponseText);
    }
}
