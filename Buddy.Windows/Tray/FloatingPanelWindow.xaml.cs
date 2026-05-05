using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Buddy.Windows.AI;
using Buddy.Windows.Voice;

namespace Buddy.Windows.Tray;

public partial class FloatingPanelWindow : Window
{
    private const int ExtendedWindowStyleIndex = -20;
    private const int ToolWindowExtendedStyle = 0x00000080;
    private const int NoActivateExtendedStyle = 0x08000000;
    private const uint ExcludeFromCaptureDisplayAffinity = 0x00000011;

    private static readonly System.Windows.Media.Brush ReadyStatusBrush = CreateFrozenBrush(0x36, 0xD3, 0x99);
    private static readonly System.Windows.Media.Brush ActiveStatusBrush = CreateFrozenBrush(0x2B, 0x7C, 0xFF);
    private static readonly System.Windows.Media.Brush ErrorStatusBrush = CreateFrozenBrush(0xF8, 0x71, 0x71);

    private readonly MicrophoneCaptureService microphoneCaptureService;
    private readonly PushToTalkHotkeyMonitor pushToTalkHotkeyMonitor;
    private readonly AssemblyAIStreamingTranscriptionService streamingTranscriptionService;
    private readonly ClaudeResponseService claudeResponseService;
    private readonly ElevenLabsTextToSpeechPlaybackService textToSpeechPlaybackService;
    private bool isPushToTalkPressed;
    private bool isPushToTalkMonitoring;
    private string? pushToTalkMonitoringErrorMessage;
    private bool isMicrophoneCapturing;
    private TimeSpan currentCaptureDuration;
    private long currentCapturedByteCount;
    private double currentAudioLevel;
    private string? microphoneCaptureErrorMessage;
    private bool isTranscriptionConnecting;
    private bool isTranscriptionStreaming;
    private string liveTranscriptText = "";
    private string finalTranscriptText = "";
    private string? transcriptionErrorMessage;
    private bool isCapturingScreens;
    private bool isClaudeResponding;
    private int screenCaptureCount;
    private string claudeUserTranscriptText = "";
    private string claudeResponseText = "";
    private string? screenCaptureErrorMessage;
    private string? claudeResponseErrorMessage;
    private bool isTextToSpeechFetchingAudio;
    private bool isTextToSpeechPlayingAudio;
    private string textToSpeechSpokenText = "";
    private string? textToSpeechPlaybackErrorMessage;

    public FloatingPanelWindow(
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
        InitializeComponent();

        UpdatePushToTalkState(
            pushToTalkHotkeyMonitor.IsPushToTalkPressed,
            pushToTalkHotkeyMonitor.IsMonitoring,
            pushToTalkHotkeyMonitor.MonitoringErrorMessage);

        pushToTalkHotkeyMonitor.PushToTalkHotkeyChanged += HandlePushToTalkHotkeyChanged;
        microphoneCaptureService.CaptureStateChanged += HandleMicrophoneCaptureStateChanged;
        streamingTranscriptionService.TranscriptionStateChanged += HandleStreamingTranscriptionStateChanged;
        claudeResponseService.ResponseStateChanged += HandleClaudeResponseStateChanged;
        textToSpeechPlaybackService.PlaybackStateChanged += HandleTextToSpeechPlaybackStateChanged;
    }

    protected override void OnSourceInitialized(EventArgs eventArguments)
    {
        base.OnSourceInitialized(eventArguments);

        IntPtr windowHandle = new WindowInteropHelper(this).Handle;
        IntPtr existingExtendedWindowStyle = GetExtendedWindowStyle(windowHandle);
        IntPtr updatedExtendedWindowStyle = new(existingExtendedWindowStyle.ToInt64() | ToolWindowExtendedStyle | NoActivateExtendedStyle);

        SetExtendedWindowStyle(windowHandle, updatedExtendedWindowStyle);

        // The AI should see the user's desktop, not Buddy's control panel.
        _ = SetWindowDisplayAffinity(windowHandle, ExcludeFromCaptureDisplayAffinity);
    }

    protected override void OnClosed(EventArgs eventArguments)
    {
        pushToTalkHotkeyMonitor.PushToTalkHotkeyChanged -= HandlePushToTalkHotkeyChanged;
        microphoneCaptureService.CaptureStateChanged -= HandleMicrophoneCaptureStateChanged;
        streamingTranscriptionService.TranscriptionStateChanged -= HandleStreamingTranscriptionStateChanged;
        claudeResponseService.ResponseStateChanged -= HandleClaudeResponseStateChanged;
        textToSpeechPlaybackService.PlaybackStateChanged -= HandleTextToSpeechPlaybackStateChanged;
        base.OnClosed(eventArguments);
    }

    public void UpdatePushToTalkState(
        bool isPushToTalkPressed,
        bool isMonitoring,
        string? monitoringErrorMessage)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdatePushToTalkState(isPushToTalkPressed, isMonitoring, monitoringErrorMessage);
            }));
            return;
        }

        this.isPushToTalkPressed = isPushToTalkPressed;
        isPushToTalkMonitoring = isMonitoring;
        pushToTalkMonitoringErrorMessage = monitoringErrorMessage;
        RenderCurrentVoiceState();
    }

    public void UpdateTextToSpeechPlaybackState(
        bool isFetchingAudio,
        bool isPlayingAudio,
        string spokenText,
        string? playbackErrorMessage)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateTextToSpeechPlaybackState(
                    isFetchingAudio,
                    isPlayingAudio,
                    spokenText,
                    playbackErrorMessage);
            }));
            return;
        }

        isTextToSpeechFetchingAudio = isFetchingAudio;
        isTextToSpeechPlayingAudio = isPlayingAudio;
        textToSpeechSpokenText = spokenText;
        textToSpeechPlaybackErrorMessage = playbackErrorMessage;
        RenderCurrentVoiceState();
    }

    public void UpdateClaudeResponseState(
        bool isCapturingScreens,
        bool isResponding,
        int screenCaptureCount,
        string userTranscriptText,
        string responseText,
        string? screenCaptureErrorMessage,
        string? responseErrorMessage)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateClaudeResponseState(
                    isCapturingScreens,
                    isResponding,
                    screenCaptureCount,
                    userTranscriptText,
                    responseText,
                    screenCaptureErrorMessage,
                    responseErrorMessage);
            }));
            return;
        }

        this.isCapturingScreens = isCapturingScreens;
        isClaudeResponding = isResponding;
        this.screenCaptureCount = screenCaptureCount;
        claudeUserTranscriptText = userTranscriptText;
        claudeResponseText = responseText;
        this.screenCaptureErrorMessage = screenCaptureErrorMessage;
        claudeResponseErrorMessage = responseErrorMessage;
        RenderCurrentVoiceState();
    }

    public void UpdateStreamingTranscriptionState(
        bool isConnecting,
        bool isStreaming,
        string liveTranscriptText,
        string finalTranscriptText,
        string? transcriptionErrorMessage)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateStreamingTranscriptionState(
                    isConnecting,
                    isStreaming,
                    liveTranscriptText,
                    finalTranscriptText,
                    transcriptionErrorMessage);
            }));
            return;
        }

        isTranscriptionConnecting = isConnecting;
        isTranscriptionStreaming = isStreaming;
        this.liveTranscriptText = liveTranscriptText;
        this.finalTranscriptText = finalTranscriptText;
        this.transcriptionErrorMessage = transcriptionErrorMessage;
        RenderCurrentVoiceState();
    }

    public void UpdateMicrophoneCaptureState(
        bool isCapturing,
        TimeSpan captureDuration,
        long capturedByteCount,
        double audioLevel,
        string? captureErrorMessage)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateMicrophoneCaptureState(
                    isCapturing,
                    captureDuration,
                    capturedByteCount,
                    audioLevel,
                    captureErrorMessage);
            }));
            return;
        }

        isMicrophoneCapturing = isCapturing;
        currentCaptureDuration = captureDuration;
        currentCapturedByteCount = capturedByteCount;
        currentAudioLevel = audioLevel;
        microphoneCaptureErrorMessage = captureErrorMessage;
        RenderCurrentVoiceState();
    }

    private void HandleQuitButtonClick(object sender, RoutedEventArgs routedEventArguments)
    {
        System.Windows.Application.Current.Shutdown();
    }

    private void HandlePushToTalkHotkeyChanged(object? sender, PushToTalkHotkeyChangedEventArgs eventArguments)
    {
        UpdatePushToTalkState(
            eventArguments.IsPushToTalkPressed,
            eventArguments.IsMonitoring,
            eventArguments.MonitoringErrorMessage);
    }

    private void HandleMicrophoneCaptureStateChanged(object? sender, MicrophoneCaptureStateChangedEventArgs eventArguments)
    {
        UpdateMicrophoneCaptureState(
            eventArguments.IsCapturing,
            eventArguments.CaptureDuration,
            eventArguments.CapturedByteCount,
            eventArguments.AudioLevel,
            eventArguments.CaptureErrorMessage);
    }

    private void HandleStreamingTranscriptionStateChanged(
        object? sender,
        StreamingTranscriptionStateChangedEventArgs eventArguments)
    {
        UpdateStreamingTranscriptionState(
            eventArguments.IsConnecting,
            eventArguments.IsStreaming,
            eventArguments.LiveTranscriptText,
            eventArguments.FinalTranscriptText,
            eventArguments.TranscriptionErrorMessage);
    }

    private void HandleClaudeResponseStateChanged(object? sender, ClaudeResponseStateChangedEventArgs eventArguments)
    {
        UpdateClaudeResponseState(
            eventArguments.IsCapturingScreens,
            eventArguments.IsResponding,
            eventArguments.ScreenCaptureCount,
            eventArguments.UserTranscriptText,
            eventArguments.ResponseText,
            eventArguments.ScreenCaptureErrorMessage,
            eventArguments.ResponseErrorMessage);
    }

    private void HandleTextToSpeechPlaybackStateChanged(
        object? sender,
        TextToSpeechPlaybackStateChangedEventArgs eventArguments)
    {
        UpdateTextToSpeechPlaybackState(
            eventArguments.IsFetchingAudio,
            eventArguments.IsPlayingAudio,
            eventArguments.SpokenText,
            eventArguments.PlaybackErrorMessage);
    }

    private void RenderCurrentVoiceState()
    {
        RenderConversationText();

        if (!isPushToTalkMonitoring)
        {
            AppStatusTextBlock.Text = "Shortcut unavailable";
            VoiceStateTextBlock.Text = "Not monitoring";
            ShortcutStateTextBlock.Text = pushToTalkMonitoringErrorMessage ?? "Keyboard hook unavailable";
            CaptureDetailTextBlock.Text = "No audio captured";
            StatusDot.Background = ErrorStatusBrush;
            UpdateAudioLevelFill(0);
            return;
        }

        if (!string.IsNullOrWhiteSpace(microphoneCaptureErrorMessage))
        {
            AppStatusTextBlock.Text = "Mic unavailable";
            VoiceStateTextBlock.Text = "Cannot listen";
            ShortcutStateTextBlock.Text = microphoneCaptureErrorMessage;
            CaptureDetailTextBlock.Text = "Check microphone access";
            StatusDot.Background = ErrorStatusBrush;
            UpdateAudioLevelFill(0);
            return;
        }

        if (!string.IsNullOrWhiteSpace(transcriptionErrorMessage))
        {
            AppStatusTextBlock.Text = "Transcription unavailable";
            VoiceStateTextBlock.Text = "Cannot transcribe";
            ShortcutStateTextBlock.Text = transcriptionErrorMessage;
            CaptureDetailTextBlock.Text = "Check Worker and AssemblyAI setup";
            StatusDot.Background = ErrorStatusBrush;
            UpdateAudioLevelFill(0);
            return;
        }

        if (!string.IsNullOrWhiteSpace(textToSpeechPlaybackErrorMessage))
        {
            AppStatusTextBlock.Text = "Voice unavailable";
            VoiceStateTextBlock.Text = "Cannot speak";
            ShortcutStateTextBlock.Text = textToSpeechPlaybackErrorMessage;
            CaptureDetailTextBlock.Text = "Check Worker and ElevenLabs setup";
            StatusDot.Background = ErrorStatusBrush;
            UpdateAudioLevelFill(0);
            return;
        }

        if (isTextToSpeechFetchingAudio)
        {
            AppStatusTextBlock.Text = "Preparing voice";
            VoiceStateTextBlock.Text = "Generating speech";
            ShortcutStateTextBlock.Text = "ElevenLabs TTS";
            CaptureDetailTextBlock.Text = "Waiting for audio";
            StatusDot.Background = ActiveStatusBrush;
            UpdateAudioLevelFill(0);
            return;
        }

        if (isTextToSpeechPlayingAudio)
        {
            AppStatusTextBlock.Text = "Speaking";
            VoiceStateTextBlock.Text = "Playing audio";
            ShortcutStateTextBlock.Text = "Buddy is talking";
            CaptureDetailTextBlock.Text = "Press Ctrl + Alt to interrupt";
            StatusDot.Background = ActiveStatusBrush;
            UpdateAudioLevelFill(0);
            return;
        }

        if (!string.IsNullOrWhiteSpace(claudeResponseErrorMessage))
        {
            AppStatusTextBlock.Text = "AI unavailable";
            VoiceStateTextBlock.Text = "Cannot respond";
            ShortcutStateTextBlock.Text = claudeResponseErrorMessage;
            CaptureDetailTextBlock.Text = "Check Worker and AI provider setup";
            StatusDot.Background = ErrorStatusBrush;
            UpdateAudioLevelFill(0);
            return;
        }

        if (isCapturingScreens)
        {
            AppStatusTextBlock.Text = "Looking";
            VoiceStateTextBlock.Text = "Capturing screen";
            ShortcutStateTextBlock.Text = "Preparing Claude";
            CaptureDetailTextBlock.Text = "Capturing connected displays";
            StatusDot.Background = ActiveStatusBrush;
            UpdateAudioLevelFill(0);
            return;
        }

        if (isClaudeResponding)
        {
            AppStatusTextBlock.Text = "Thinking";
            VoiceStateTextBlock.Text = "Responding";
            ShortcutStateTextBlock.Text = "Streaming AI";
            CaptureDetailTextBlock.Text = CreateClaudeResponseDetailText();
            StatusDot.Background = ActiveStatusBrush;
            UpdateAudioLevelFill(0);
            return;
        }

        if (isTranscriptionConnecting)
        {
            AppStatusTextBlock.Text = "Connecting";
            VoiceStateTextBlock.Text = "Starting transcript";
            ShortcutStateTextBlock.Text = "Contacting AssemblyAI";
            CaptureDetailTextBlock.Text = "Opening stream";
            StatusDot.Background = ActiveStatusBrush;
            UpdateAudioLevelFill(0);
            return;
        }

        if (isMicrophoneCapturing)
        {
            AppStatusTextBlock.Text = "Listening";
            VoiceStateTextBlock.Text = "Recording";
            ShortcutStateTextBlock.Text = isTranscriptionStreaming
                ? "Live transcription"
                : "Release to stop";
            CaptureDetailTextBlock.Text = FormatCaptureDetails(currentCaptureDuration, currentCapturedByteCount);
            StatusDot.Background = ActiveStatusBrush;
            UpdateAudioLevelFill(currentAudioLevel);
            return;
        }

        if (isPushToTalkPressed)
        {
            AppStatusTextBlock.Text = "Shortcut active";
            VoiceStateTextBlock.Text = "Starting mic";
            ShortcutStateTextBlock.Text = "Release to stop";
            CaptureDetailTextBlock.Text = "Opening microphone";
            StatusDot.Background = ActiveStatusBrush;
            UpdateAudioLevelFill(0);
            return;
        }

        AppStatusTextBlock.Text = "Ready";
        VoiceStateTextBlock.Text = "Text prompt";
        ShortcutStateTextBlock.Text = "Ctrl + Alt + Space to type\nCtrl + Alt + Esc to quit";
        CaptureDetailTextBlock.Text = "Voice is off for now";
        StatusDot.Background = ReadyStatusBrush;
        UpdateAudioLevelFill(0);
    }

    private void UpdateAudioLevelFill(double audioLevel)
    {
        double clampedAudioLevel = Math.Clamp(audioLevel, 0, 1);
        AudioLevelFill.Width = AudioLevelTrack.Width * clampedAudioLevel;
    }

    private static string FormatCaptureDetails(TimeSpan captureDuration, long capturedByteCount)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:0.0}s / {1}",
            captureDuration.TotalSeconds,
            FormatByteCount(capturedByteCount));
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

        return "Prompt will appear here";
    }

    private string BestAvailableClaudeResponseText()
    {
        if (!string.IsNullOrWhiteSpace(claudeResponseText))
        {
            return claudeResponseText;
        }

        if (!string.IsNullOrWhiteSpace(textToSpeechSpokenText))
        {
            return textToSpeechSpokenText;
        }

        if (isCapturingScreens)
        {
            return "Looking at your screen...";
        }

        if (isClaudeResponding)
        {
            return "Thinking...";
        }

        return "Response will appear here";
    }

    private string CreateClaudeResponseDetailText()
    {
        if (!string.IsNullOrWhiteSpace(screenCaptureErrorMessage))
        {
            return "Screen capture failed; using transcript";
        }

        if (screenCaptureCount > 0 && string.IsNullOrWhiteSpace(claudeResponseText))
        {
            return FormatScreenCaptureCount(screenCaptureCount);
        }

        return string.IsNullOrWhiteSpace(claudeResponseText)
            ? "Waiting for first token"
            : "Response streaming";
    }

    private void RenderConversationText()
    {
        TranscriptTextBlock.Text = BestAvailableTranscriptText();
        ClaudeResponseTextBlock.Text = BestAvailableClaudeResponseText();
    }

    private static string FormatByteCount(long byteCount)
    {
        if (byteCount < 1024)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} B", byteCount);
        }

        double kilobytes = byteCount / 1024.0;

        if (kilobytes < 1024)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0} KB", kilobytes);
        }

        double megabytes = kilobytes / 1024.0;
        return string.Format(CultureInfo.InvariantCulture, "{0:0.0} MB", megabytes);
    }

    private static string FormatScreenCaptureCount(int screenCaptureCount)
    {
        return screenCaptureCount == 1
            ? "1 screen sent"
            : string.Format(CultureInfo.InvariantCulture, "{0} screens sent", screenCaptureCount);
    }

    private static System.Windows.Media.Brush CreateFrozenBrush(byte red, byte green, byte blue)
    {
        SolidColorBrush brush = new(System.Windows.Media.Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static IntPtr GetExtendedWindowStyle(IntPtr windowHandle)
    {
        if (IntPtr.Size == 8)
        {
            return GetWindowLongPtr64(windowHandle, ExtendedWindowStyleIndex);
        }

        return new IntPtr(GetWindowLong32(windowHandle, ExtendedWindowStyleIndex));
    }

    private static void SetExtendedWindowStyle(IntPtr windowHandle, IntPtr updatedExtendedWindowStyle)
    {
        if (IntPtr.Size == 8)
        {
            _ = SetWindowLongPtr64(windowHandle, ExtendedWindowStyleIndex, updatedExtendedWindowStyle);
            return;
        }

        _ = SetWindowLong32(windowHandle, ExtendedWindowStyleIndex, updatedExtendedWindowStyle.ToInt32());
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr windowHandle, int index, int updatedValue);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr windowHandle, int index, IntPtr updatedValue);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr windowHandle, uint displayAffinity);
}
