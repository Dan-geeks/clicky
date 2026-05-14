using System;
using System.Threading;
using System.Windows;
using Buddy.Windows.AI;
using Buddy.Windows.ComputerUse;
using Buddy.Windows.Configuration;
using Buddy.Windows.Diagnostics;
using Buddy.Windows.Overlay;
using Buddy.Windows.Screen;
using Buddy.Windows.TextInput;
using Buddy.Windows.Tray;
using Buddy.Windows.Voice;

namespace Buddy.Windows;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = "Buddy.Windows.SingleInstance";

    private AssemblyAITemporaryTokenClient? assemblyAITemporaryTokenClient;
    private AssemblyAIStreamingTranscriptionService? assemblyAIStreamingTranscriptionService;
    private ClaudeResponseService? claudeResponseService;
    private ClaudeStreamingChatClient? claudeStreamingChatClient;
    private CompanionOverlayController? companionOverlayController;
    private ComputerUseAgentCoordinator? computerUseAgentCoordinator;
    private ComputerUseActionPromptController? computerUseActionPromptController;
    private GeminiComputerUseClient? geminiComputerUseClient;
    private ElevenLabsTextToSpeechClient? elevenLabsTextToSpeechClient;
    private ElevenLabsTextToSpeechPlaybackService? elevenLabsTextToSpeechPlaybackService;
    private MicrophoneCaptureService? microphoneCaptureService;
    private PushToTalkHotkeyMonitor? pushToTalkHotkeyMonitor;
    private PushToTalkVoiceCaptureCoordinator? pushToTalkVoiceCaptureCoordinator;
    private Mutex? singleInstanceMutex;
    private TextPromptController? textPromptController;
    private TrayIconManager? trayIconManager;
    private WindowsScreenCaptureService? windowsScreenCaptureService;
    private WindowsClipboardTextCaptureService? windowsClipboardTextCaptureService;

    protected override void OnStartup(StartupEventArgs startupEventArguments)
    {
        singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool isFirstBuddyInstance);

        if (!isFirstBuddyInstance)
        {
            BuddyLog.Info("Another Buddy.Windows instance is already running; exiting duplicate launch.");
            Shutdown();
            return;
        }

        base.OnStartup(startupEventArguments);

        BuddyLog.Info($"Buddy.Windows starting. Log file: {BuddyLog.LogFilePath}");
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        bool isVoiceEnabled = BuddyWindowsConfiguration.IsVoiceEnabled;
        BuddyRuntimeModelSelectionChangedEventArgs modelSelectionSnapshot =
            BuddyRuntimeModelSelection.CreateSnapshot();
        BuddyLog.Info(isVoiceEnabled
            ? "Voice mode enabled. AssemblyAI and ElevenLabs workflows may run."
            : "Text-only mode enabled. AssemblyAI and ElevenLabs workflows will not run.");
        BuddyLog.Info(
            $"Initial Ask Buddy model: {modelSelectionSnapshot.ChatModel.DisplayName} ({modelSelectionSnapshot.ChatModel.Provider}/{modelSelectionSnapshot.ChatModel.Model}). Computer Use model: {modelSelectionSnapshot.ComputerUseDisplayName} ({modelSelectionSnapshot.ComputerUseModel}).");
        pushToTalkHotkeyMonitor = new PushToTalkHotkeyMonitor(isVoiceEnabled);
        pushToTalkHotkeyMonitor.ShutdownHotkeyPressed += HandleShutdownHotkeyPressed;
        pushToTalkHotkeyMonitor.ChatModelCycleHotkeyPressed += HandleChatModelCycleHotkeyPressed;
        microphoneCaptureService = new MicrophoneCaptureService();
        assemblyAITemporaryTokenClient = new AssemblyAITemporaryTokenClient();
        assemblyAIStreamingTranscriptionService = new AssemblyAIStreamingTranscriptionService(
            assemblyAITemporaryTokenClient);
        windowsScreenCaptureService = new WindowsScreenCaptureService();
        windowsClipboardTextCaptureService = new WindowsClipboardTextCaptureService();
        claudeStreamingChatClient = new ClaudeStreamingChatClient();
        claudeResponseService = new ClaudeResponseService(
            claudeStreamingChatClient,
            windowsScreenCaptureService,
            windowsClipboardTextCaptureService);
        elevenLabsTextToSpeechClient = new ElevenLabsTextToSpeechClient();
        elevenLabsTextToSpeechPlaybackService = new ElevenLabsTextToSpeechPlaybackService(
            elevenLabsTextToSpeechClient);
        geminiComputerUseClient = new GeminiComputerUseClient();
        computerUseAgentCoordinator = new ComputerUseAgentCoordinator(
            geminiComputerUseClient,
            windowsScreenCaptureService);
        if (isVoiceEnabled)
        {
            pushToTalkVoiceCaptureCoordinator = new PushToTalkVoiceCaptureCoordinator(
                pushToTalkHotkeyMonitor,
                microphoneCaptureService,
                assemblyAIStreamingTranscriptionService,
                claudeResponseService,
                elevenLabsTextToSpeechPlaybackService);
        }

        trayIconManager = new TrayIconManager(
            pushToTalkHotkeyMonitor,
            microphoneCaptureService,
            assemblyAIStreamingTranscriptionService,
            claudeResponseService,
            elevenLabsTextToSpeechPlaybackService,
            computerUseAgentCoordinator);
        trayIconManager.Initialize();
        companionOverlayController = new CompanionOverlayController(
            pushToTalkHotkeyMonitor,
            microphoneCaptureService,
            assemblyAIStreamingTranscriptionService,
            claudeResponseService,
            elevenLabsTextToSpeechPlaybackService,
            claudeStreamingChatClient,
            windowsScreenCaptureService,
            windowsClipboardTextCaptureService);
        companionOverlayController.Initialize();
        textPromptController = new TextPromptController(
            pushToTalkHotkeyMonitor,
            microphoneCaptureService,
            assemblyAIStreamingTranscriptionService,
            claudeResponseService,
            elevenLabsTextToSpeechPlaybackService);
        textPromptController.Initialize();

        // Computer Use action mode (Ctrl+Alt+A): the agent coordinator drives a multi-turn
        // Gemini loop that actually clicks/types/scrolls on the user's behalf, while the
        // existing overlay/pointing flow stays untouched for "just point at it" requests.
        computerUseActionPromptController = new ComputerUseActionPromptController(
            pushToTalkHotkeyMonitor,
            microphoneCaptureService,
            assemblyAIStreamingTranscriptionService,
            claudeResponseService,
            elevenLabsTextToSpeechPlaybackService,
            computerUseAgentCoordinator);
        computerUseActionPromptController.Initialize();

        pushToTalkHotkeyMonitor.Start();
    }

    protected override void OnExit(ExitEventArgs exitEventArguments)
    {
        BuddyLog.Info("Buddy.Windows exiting.");
        textPromptController?.Dispose();
        computerUseActionPromptController?.Dispose();
        computerUseAgentCoordinator?.Dispose();
        geminiComputerUseClient?.Dispose();
        companionOverlayController?.Dispose();
        pushToTalkVoiceCaptureCoordinator?.Dispose();
        trayIconManager?.Dispose();
        elevenLabsTextToSpeechPlaybackService?.Dispose();
        elevenLabsTextToSpeechClient?.Dispose();
        claudeResponseService?.Dispose();
        claudeStreamingChatClient?.Dispose();
        assemblyAIStreamingTranscriptionService?.Dispose();
        assemblyAITemporaryTokenClient?.Dispose();
        microphoneCaptureService?.Dispose();
        if (pushToTalkHotkeyMonitor is not null)
        {
            pushToTalkHotkeyMonitor.ShutdownHotkeyPressed -= HandleShutdownHotkeyPressed;
            pushToTalkHotkeyMonitor.ChatModelCycleHotkeyPressed -= HandleChatModelCycleHotkeyPressed;
        }

        pushToTalkHotkeyMonitor?.Dispose();
        ReleaseSingleInstanceMutex();
        base.OnExit(exitEventArguments);
    }

    private void HandleShutdownHotkeyPressed(object? sender, EventArgs eventArguments)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            BuddyLog.Info("Buddy.Windows shutdown requested from Ctrl+Alt+Esc.");
            Shutdown();
        }));
    }

    private void HandleChatModelCycleHotkeyPressed(object? sender, EventArgs eventArguments)
    {
        Dispatcher.BeginInvoke(new Action(BuddyRuntimeModelSelection.CycleChatModel));
    }

    private void ReleaseSingleInstanceMutex()
    {
        if (singleInstanceMutex is null)
        {
            return;
        }

        try
        {
            singleInstanceMutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        singleInstanceMutex.Dispose();
        singleInstanceMutex = null;
    }
}
