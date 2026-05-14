using System;
using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Buddy.Windows.AI;
using Buddy.Windows.ComputerUse;
using Buddy.Windows.Configuration;
using Buddy.Windows.Voice;
using Forms = System.Windows.Forms;

namespace Buddy.Windows.Tray;

public sealed class TrayIconManager : IDisposable
{
    private const double PanelScreenMargin = 12;

    private readonly PushToTalkHotkeyMonitor pushToTalkHotkeyMonitor;
    private readonly MicrophoneCaptureService microphoneCaptureService;
    private readonly AssemblyAIStreamingTranscriptionService streamingTranscriptionService;
    private readonly ClaudeResponseService claudeResponseService;
    private readonly ElevenLabsTextToSpeechPlaybackService textToSpeechPlaybackService;
    private readonly ComputerUseAgentCoordinator computerUseAgentCoordinator;
    private readonly Forms.NotifyIcon trayIcon;
    private FloatingPanelWindow? floatingPanelWindow;
    private bool isDisposed;

    public TrayIconManager(
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
        trayIcon = new Forms.NotifyIcon
        {
            Text = "Buddy",
            Icon = SystemIcons.Application,
            Visible = false,
            ContextMenuStrip = CreateTrayContextMenu()
        };

        trayIcon.MouseUp += HandleTrayIconMouseUp;
        pushToTalkHotkeyMonitor.PushToTalkHotkeyChanged += HandlePushToTalkHotkeyChanged;
        microphoneCaptureService.CaptureStateChanged += HandleMicrophoneCaptureStateChanged;
        streamingTranscriptionService.TranscriptionStateChanged += HandleStreamingTranscriptionStateChanged;
        claudeResponseService.ResponseStateChanged += HandleClaudeResponseStateChanged;
        textToSpeechPlaybackService.PlaybackStateChanged += HandleTextToSpeechPlaybackStateChanged;
        computerUseAgentCoordinator.StateChanged += HandleComputerUseAgentStateChanged;
        BuddyRuntimeModelSelection.ModelSelectionChanged += HandleModelSelectionChanged;
    }

    public void Initialize()
    {
        UpdateTrayIconText();
        trayIcon.Visible = true;
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        trayIcon.Visible = false;
        trayIcon.MouseUp -= HandleTrayIconMouseUp;
        pushToTalkHotkeyMonitor.PushToTalkHotkeyChanged -= HandlePushToTalkHotkeyChanged;
        microphoneCaptureService.CaptureStateChanged -= HandleMicrophoneCaptureStateChanged;
        streamingTranscriptionService.TranscriptionStateChanged -= HandleStreamingTranscriptionStateChanged;
        claudeResponseService.ResponseStateChanged -= HandleClaudeResponseStateChanged;
        textToSpeechPlaybackService.PlaybackStateChanged -= HandleTextToSpeechPlaybackStateChanged;
        computerUseAgentCoordinator.StateChanged -= HandleComputerUseAgentStateChanged;
        BuddyRuntimeModelSelection.ModelSelectionChanged -= HandleModelSelectionChanged;
        trayIcon.Dispose();

        floatingPanelWindow?.Close();
        floatingPanelWindow = null;
    }

    private Forms.ContextMenuStrip CreateTrayContextMenu()
    {
        Forms.ContextMenuStrip trayContextMenu = new();

        Forms.ToolStripMenuItem openBuddyMenuItem = new("Open Buddy");
        openBuddyMenuItem.Click += (_, _) => ShowFloatingPanel();

        Forms.ToolStripMenuItem cycleAskModelMenuItem = new("Cycle Ask Model (Ctrl+Alt+M)");
        cycleAskModelMenuItem.Click += (_, _) => BuddyRuntimeModelSelection.CycleChatModel();

        Forms.ToolStripMenuItem quitMenuItem = new("Quit");
        quitMenuItem.Click += (_, _) => System.Windows.Application.Current.Shutdown();

        trayContextMenu.Items.Add(openBuddyMenuItem);
        trayContextMenu.Items.Add(cycleAskModelMenuItem);
        trayContextMenu.Items.Add(new Forms.ToolStripSeparator());
        trayContextMenu.Items.Add(quitMenuItem);

        return trayContextMenu;
    }

    private void HandleTrayIconMouseUp(object? sender, Forms.MouseEventArgs mouseEventArguments)
    {
        if (mouseEventArguments.Button == Forms.MouseButtons.Left)
        {
            ToggleFloatingPanel();
        }
    }

    private void ToggleFloatingPanel()
    {
        if (floatingPanelWindow?.IsVisible == true)
        {
            floatingPanelWindow.Hide();
            return;
        }

        ShowFloatingPanel();
    }

    private void ShowFloatingPanel()
    {
        if (floatingPanelWindow is null)
        {
            floatingPanelWindow = new FloatingPanelWindow(
                pushToTalkHotkeyMonitor,
                microphoneCaptureService,
                streamingTranscriptionService,
                claudeResponseService,
                textToSpeechPlaybackService,
                computerUseAgentCoordinator);
            floatingPanelWindow.Closed += (_, _) => floatingPanelWindow = null;
        }

        PositionFloatingPanel(floatingPanelWindow);
        floatingPanelWindow.Show();
    }

    private void HandlePushToTalkHotkeyChanged(object? sender, PushToTalkHotkeyChangedEventArgs eventArguments)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateTrayIconText();
            floatingPanelWindow?.UpdatePushToTalkState(
                eventArguments.IsPushToTalkPressed,
                eventArguments.IsMonitoring,
                eventArguments.MonitoringErrorMessage);
        }));
    }

    private void HandleMicrophoneCaptureStateChanged(object? sender, MicrophoneCaptureStateChangedEventArgs eventArguments)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateTrayIconText();
            floatingPanelWindow?.UpdateMicrophoneCaptureState(
                eventArguments.IsCapturing,
                eventArguments.CaptureDuration,
                eventArguments.CapturedByteCount,
                eventArguments.AudioLevel,
                eventArguments.CaptureErrorMessage);
        }));
    }

    private void HandleStreamingTranscriptionStateChanged(
        object? sender,
        StreamingTranscriptionStateChangedEventArgs eventArguments)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateTrayIconText();
            floatingPanelWindow?.UpdateStreamingTranscriptionState(
                eventArguments.IsConnecting,
                eventArguments.IsStreaming,
                eventArguments.LiveTranscriptText,
                eventArguments.FinalTranscriptText,
                eventArguments.TranscriptionErrorMessage);
        }));
    }

    private void HandleClaudeResponseStateChanged(object? sender, ClaudeResponseStateChangedEventArgs eventArguments)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateTrayIconText();
            floatingPanelWindow?.UpdateClaudeResponseState(
                eventArguments.IsCapturingScreens,
                eventArguments.IsResponding,
                eventArguments.ScreenCaptureCount,
                eventArguments.UserTranscriptText,
                eventArguments.ResponseText,
                eventArguments.ScreenCaptureErrorMessage,
                eventArguments.ResponseErrorMessage);
        }));
    }

    private void HandleTextToSpeechPlaybackStateChanged(
        object? sender,
        TextToSpeechPlaybackStateChangedEventArgs eventArguments)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateTrayIconText();
            floatingPanelWindow?.UpdateTextToSpeechPlaybackState(
                eventArguments.IsFetchingAudio,
                eventArguments.IsPlayingAudio,
                eventArguments.SpokenText,
                eventArguments.PlaybackErrorMessage);
        }));
    }

    private void HandleComputerUseAgentStateChanged(
        object? sender,
        ComputerUseAgentStateChangedEventArgs eventArguments)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateTrayIconText();
            floatingPanelWindow?.UpdateComputerUseAgentState(
                eventArguments.Status,
                eventArguments.StatusText,
                eventArguments.FinalAssistantText,
                eventArguments.ErrorMessage);
        }));
    }

    private void HandleModelSelectionChanged(
        object? sender,
        BuddyRuntimeModelSelectionChangedEventArgs eventArguments)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateTrayIconText();
            floatingPanelWindow?.UpdateModelSelectionState();
        }));
    }

    private void UpdateTrayIconText()
    {
        if (!pushToTalkHotkeyMonitor.IsMonitoring)
        {
            trayIcon.Text = "Buddy - shortcut unavailable";
            return;
        }

        if (!string.IsNullOrWhiteSpace(textToSpeechPlaybackService.PlaybackErrorMessage))
        {
            trayIcon.Text = "Buddy - voice unavailable";
            return;
        }

        if (textToSpeechPlaybackService.IsFetchingAudio)
        {
            trayIcon.Text = "Buddy - preparing voice";
            return;
        }

        if (textToSpeechPlaybackService.IsPlayingAudio)
        {
            trayIcon.Text = "Buddy - speaking";
            return;
        }

        if (!string.IsNullOrWhiteSpace(claudeResponseService.ResponseErrorMessage))
        {
            trayIcon.Text = "Buddy - AI unavailable";
            return;
        }

        if (computerUseAgentCoordinator.IsAgentRunning)
        {
            trayIcon.Text = "Buddy - acting";
            return;
        }

        if (claudeResponseService.IsCapturingScreens)
        {
            trayIcon.Text = "Buddy - capturing screen";
            return;
        }

        if (claudeResponseService.IsResponding)
        {
            trayIcon.Text = "Buddy - responding";
            return;
        }

        if (!string.IsNullOrWhiteSpace(streamingTranscriptionService.TranscriptionErrorMessage))
        {
            trayIcon.Text = "Buddy - transcription unavailable";
            return;
        }

        if (streamingTranscriptionService.IsConnecting)
        {
            trayIcon.Text = "Buddy - connecting";
            return;
        }

        if (streamingTranscriptionService.IsStreaming)
        {
            trayIcon.Text = "Buddy - transcribing";
            return;
        }

        if (!string.IsNullOrWhiteSpace(microphoneCaptureService.CaptureErrorMessage))
        {
            trayIcon.Text = "Buddy - microphone unavailable";
            return;
        }

        if (microphoneCaptureService.IsCapturing)
        {
            trayIcon.Text = "Buddy - listening";
            return;
        }

        trayIcon.Text = pushToTalkHotkeyMonitor.IsPushToTalkPressed
            ? "Buddy - Ctrl+Alt held"
            : $"Buddy - {BuddyRuntimeModelSelection.CurrentChatDisplayName}";
    }

    private static void PositionFloatingPanel(FloatingPanelWindow floatingPanelWindow)
    {
        _ = new WindowInteropHelper(floatingPanelWindow).EnsureHandle();

        DpiScale panelDpiScale = VisualTreeHelper.GetDpi(floatingPanelWindow);
        Forms.Screen currentCursorScreen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        System.Drawing.Rectangle screenWorkingArea = currentCursorScreen.WorkingArea;

        double workingAreaLeft = screenWorkingArea.Left / panelDpiScale.DpiScaleX;
        double workingAreaTop = screenWorkingArea.Top / panelDpiScale.DpiScaleY;
        double workingAreaRight = screenWorkingArea.Right / panelDpiScale.DpiScaleX;
        double workingAreaBottom = screenWorkingArea.Bottom / panelDpiScale.DpiScaleY;

        double panelLeft = workingAreaRight - floatingPanelWindow.Width - PanelScreenMargin;
        double panelTop = workingAreaBottom - floatingPanelWindow.Height - PanelScreenMargin;

        floatingPanelWindow.Left = Math.Max(workingAreaLeft + PanelScreenMargin, panelLeft);
        floatingPanelWindow.Top = Math.Max(workingAreaTop + PanelScreenMargin, panelTop);
    }
}
