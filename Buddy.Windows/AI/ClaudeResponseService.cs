using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Buddy.Windows.Configuration;
using Buddy.Windows.Diagnostics;
using Buddy.Windows.Pointing;
using Buddy.Windows.Screen;

namespace Buddy.Windows.AI;

public sealed class ClaudeResponseService : IDisposable
{
    private const int MaximumConversationHistoryExchangeCount = 10;
    private const long FastGuidedScreenCaptureJpegQuality = 62L;
    private static readonly TimeSpan StandardResponseHardTimeout = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan FastGuidedResponseHardTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Minimum interval between streaming UI update notifications (100 ms = max 10/s).</summary>
    private static readonly long StreamingNotificationMinIntervalTicks =
        TimeSpan.FromMilliseconds(100).Ticks;

    private readonly ClaudeStreamingChatClient claudeStreamingChatClient;
    private readonly WindowsScreenCaptureService windowsScreenCaptureService;
    private readonly object responseStateLock = new();
    private readonly List<ClaudeConversationExchange> conversationHistory = new();
    private CancellationTokenSource? activeResponseCancellationTokenSource;
    private bool isCapturingScreens;
    private bool isResponding;
    private int screenCaptureCount;
    private string userTranscriptText = "";
    private string responseText = "";
    private IReadOnlyList<PointingInstruction> pointingInstructions = Array.Empty<PointingInstruction>();
    private PointingInstruction? preparedNextPointingInstruction;
    private string preparedNextInstructionText = "";
    private string? screenCaptureErrorMessage;
    private string? responseErrorMessage;
    /// <summary>
    /// Tracks the number of pointing instructions reported in the last streaming notification.
    /// Guarded by <see cref="responseStateLock"/>. A new instruction always triggers an
    /// immediate notification regardless of the time throttle.
    /// </summary>
    private int lastStreamingNotificationPointCount;
    /// <summary>
    /// Timestamp of the last streaming notification, in UTC ticks.
    /// Accessed via <see cref="Interlocked"/> so it can be read outside the lock.
    /// </summary>
    private long lastStreamingNotificationTicks;
    private bool isDisposed;

    public ClaudeResponseService(
        ClaudeStreamingChatClient claudeStreamingChatClient,
        WindowsScreenCaptureService windowsScreenCaptureService)
    {
        this.claudeStreamingChatClient = claudeStreamingChatClient;
        this.windowsScreenCaptureService = windowsScreenCaptureService;
    }

    public event EventHandler<ClaudeResponseStateChangedEventArgs>? ResponseStateChanged;

    public bool IsResponding
    {
        get
        {
            lock (responseStateLock)
            {
                return isResponding;
            }
        }
    }

    public bool IsCapturingScreens
    {
        get
        {
            lock (responseStateLock)
            {
                return isCapturingScreens;
            }
        }
    }

    public int ScreenCaptureCount
    {
        get
        {
            lock (responseStateLock)
            {
                return screenCaptureCount;
            }
        }
    }

    public string? ScreenCaptureErrorMessage
    {
        get
        {
            lock (responseStateLock)
            {
                return screenCaptureErrorMessage;
            }
        }
    }

    public string? ResponseErrorMessage
    {
        get
        {
            lock (responseStateLock)
            {
                return responseErrorMessage;
            }
        }
    }

    public void SendUserTranscript(string completedUserTranscriptText, bool preferFastModel = false)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);

        string trimmedUserTranscriptText = completedUserTranscriptText.Trim();

        if (string.IsNullOrWhiteSpace(trimmedUserTranscriptText))
        {
            return;
        }

        BuddyLog.Workflow(
            $"AI workflow request queued. FastModel={preferFastModel}; UserText={BuddyLog.DescribeTextForLog(trimmedUserTranscriptText)}.");
        CancellationTokenSource responseCancellationTokenSource;
        List<ClaudeConversationExchange> conversationHistorySnapshot;

        lock (responseStateLock)
        {
            activeResponseCancellationTokenSource?.Cancel();
            activeResponseCancellationTokenSource = new CancellationTokenSource();
            responseCancellationTokenSource = activeResponseCancellationTokenSource;
            responseCancellationTokenSource.CancelAfter(preferFastModel
                ? FastGuidedResponseHardTimeout
                : StandardResponseHardTimeout);
            conversationHistorySnapshot = new List<ClaudeConversationExchange>(conversationHistory);

            isCapturingScreens = true;
            isResponding = false;
            screenCaptureCount = 0;
            userTranscriptText = trimmedUserTranscriptText;
            responseText = "";
            pointingInstructions = Array.Empty<PointingInstruction>();
            preparedNextPointingInstruction = null;
            preparedNextInstructionText = "";
            screenCaptureErrorMessage = null;
            responseErrorMessage = null;
            lastStreamingNotificationPointCount = 0;
            Interlocked.Exchange(ref lastStreamingNotificationTicks, 0);
        }

        NotifyResponseStateChanged();

        _ = Task.Run(async () =>
        {
            await StreamClaudeResponseAsync(
                trimmedUserTranscriptText,
                conversationHistorySnapshot,
                responseCancellationTokenSource,
                preferFastModel);
        });
    }

    public void CancelResponse()
    {
        BuddyLog.Workflow("AI workflow cancellation requested.");

        lock (responseStateLock)
        {
            activeResponseCancellationTokenSource?.Cancel();
            activeResponseCancellationTokenSource = null;
            isCapturingScreens = false;
            isResponding = false;
            screenCaptureCount = 0;
            userTranscriptText = "";
            responseText = "";
            pointingInstructions = Array.Empty<PointingInstruction>();
            preparedNextPointingInstruction = null;
            preparedNextInstructionText = "";
            screenCaptureErrorMessage = null;
            responseErrorMessage = null;
        }

        NotifyResponseStateChanged();
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        CancelResponse();
    }

    private async Task StreamClaudeResponseAsync(
        string completedUserTranscriptText,
        IReadOnlyList<ClaudeConversationExchange> conversationHistorySnapshot,
        CancellationTokenSource responseCancellationTokenSource,
        bool preferFastModel)
    {
        try
        {
            BuddyLog.Workflow($"AI workflow started. FastModel={preferFastModel}.");
            IReadOnlyList<WindowsScreenCapture> screenCaptures =
                await CaptureScreensForCurrentResponseAsync(
                    responseCancellationTokenSource,
                    preferFastModel);

            if (!ShouldContinueResponse(responseCancellationTokenSource))
            {
                return;
            }

            string completedRawResponseText = await claudeStreamingChatClient.StreamResponseAsync(
                completedUserTranscriptText,
                conversationHistorySnapshot,
                screenCaptures,
                updatedRawResponseText => UpdateStreamingResponseText(
                    responseCancellationTokenSource,
                    updatedRawResponseText),
                preferFastModel,
                responseCancellationTokenSource.Token);

            if (responseCancellationTokenSource.IsCancellationRequested)
            {
                return;
            }

            PointingInstructionParseResult completedParseResult =
                PointingInstructionParser.Parse(completedRawResponseText);
            BuddyLog.Workflow(
                $"AI workflow completed. Response={BuddyLog.DescribeTextForLog(completedParseResult.VisibleResponseText)}; Points={completedParseResult.PointingInstructions.Count}; HasPreparedNextPoint={completedParseResult.PreparedNextPointingInstruction is not null}.");

            lock (responseStateLock)
            {
                if (!ReferenceEquals(activeResponseCancellationTokenSource, responseCancellationTokenSource))
                {
                    return;
                }

                responseText = completedParseResult.VisibleResponseText;
                pointingInstructions = completedParseResult.PointingInstructions;
                preparedNextPointingInstruction = completedParseResult.PreparedNextPointingInstruction;
                preparedNextInstructionText = completedParseResult.PreparedNextInstructionText;
                responseErrorMessage = null;
                isCapturingScreens = false;
                isResponding = false;
                activeResponseCancellationTokenSource = null;

                conversationHistory.Add(new ClaudeConversationExchange(
                    completedUserTranscriptText,
                    completedParseResult.VisibleResponseText));

                if (conversationHistory.Count > MaximumConversationHistoryExchangeCount)
                {
                    conversationHistory.RemoveRange(
                        0,
                        conversationHistory.Count - MaximumConversationHistoryExchangeCount);
                }
            }

            NotifyResponseStateChanged();
        }
        catch (OperationCanceledException)
        {
            HandleActiveResponseCancellation(responseCancellationTokenSource);
        }
        catch (Exception exception)
        {
            BuddyLog.Error("AI response failed", exception);

            lock (responseStateLock)
            {
                if (!ReferenceEquals(activeResponseCancellationTokenSource, responseCancellationTokenSource))
                {
                    return;
                }

                responseErrorMessage = exception.Message;
                isCapturingScreens = false;
                isResponding = false;
                pointingInstructions = Array.Empty<PointingInstruction>();
                preparedNextPointingInstruction = null;
                preparedNextInstructionText = "";
                activeResponseCancellationTokenSource = null;
            }

            NotifyResponseStateChanged();
        }
        finally
        {
            responseCancellationTokenSource.Dispose();
        }
    }

    private void HandleActiveResponseCancellation(CancellationTokenSource responseCancellationTokenSource)
    {
        bool shouldNotifyResponseStateChanged = false;

        lock (responseStateLock)
        {
            if (!ReferenceEquals(activeResponseCancellationTokenSource, responseCancellationTokenSource))
            {
                return;
            }

            bool hasPartialResponseText = !string.IsNullOrWhiteSpace(responseText);

            responseErrorMessage = hasPartialResponseText
                ? "AI response stopped early; showing the partial response."
                : "AI response timed out before it sent text.";
            BuddyLog.Workflow(
                $"AI workflow cancelled or timed out. HasPartialResponse={hasPartialResponseText}.");
            isCapturingScreens = false;
            isResponding = false;
            activeResponseCancellationTokenSource = null;

            if (!hasPartialResponseText)
            {
                pointingInstructions = Array.Empty<PointingInstruction>();
                preparedNextPointingInstruction = null;
                preparedNextInstructionText = "";
            }

            shouldNotifyResponseStateChanged = true;
        }

        if (shouldNotifyResponseStateChanged)
        {
            NotifyResponseStateChanged();
        }
    }

    private void UpdateStreamingResponseText(
        CancellationTokenSource responseCancellationTokenSource,
        string updatedRawResponseText)
    {
        PointingInstructionParseResult updatedParseResult =
            PointingInstructionParser.Parse(updatedRawResponseText);

        bool shouldNotify;

        lock (responseStateLock)
        {
            if (!ReferenceEquals(activeResponseCancellationTokenSource, responseCancellationTokenSource)
                || responseCancellationTokenSource.IsCancellationRequested)
            {
                return;
            }

            // A new pointing instruction must be surfaced immediately so the overlay
            // can start animating the cursor toward the target without waiting for the
            // next throttle window.
            bool hasNewPointingInstruction =
                updatedParseResult.PointingInstructions.Count > lastStreamingNotificationPointCount;

            long currentTicks = DateTime.UtcNow.Ticks;
            long lastTicks = Interlocked.Read(ref lastStreamingNotificationTicks);
            bool isThrottleWindowElapsed =
                currentTicks - lastTicks >= StreamingNotificationMinIntervalTicks;

            shouldNotify = hasNewPointingInstruction || isThrottleWindowElapsed;

            responseText = updatedParseResult.VisibleResponseText;
            pointingInstructions = updatedParseResult.PointingInstructions;
            preparedNextPointingInstruction = updatedParseResult.PreparedNextPointingInstruction;
            preparedNextInstructionText = updatedParseResult.PreparedNextInstructionText;

            if (shouldNotify)
            {
                lastStreamingNotificationPointCount = updatedParseResult.PointingInstructions.Count;
                Interlocked.Exchange(ref lastStreamingNotificationTicks, currentTicks);
            }
        }

        if (shouldNotify)
        {
            NotifyResponseStateChanged();
        }
    }

    private async Task<IReadOnlyList<WindowsScreenCapture>> CaptureScreensForCurrentResponseAsync(
        CancellationTokenSource responseCancellationTokenSource,
        bool preferFastModel)
    {
        IReadOnlyList<WindowsScreenCapture> screenCaptures = Array.Empty<WindowsScreenCapture>();
        string? screenCaptureFailureMessage = null;

        try
        {
            bool shouldCaptureScrollContext =
                !preferFastModel
                && BuddyWindowsConfiguration.IsScrollContextEnabled;

            screenCaptures = await windowsScreenCaptureService.CaptureAllScreensWithScrollContextAsJpegAsync(
                responseCancellationTokenSource.Token,
                captureCursorScreenOnly: preferFastModel,
                jpegQuality: preferFastModel
                    ? FastGuidedScreenCaptureJpegQuality
                    : WindowsScreenCaptureService.DefaultJpegQuality,
                includeScrollContext: shouldCaptureScrollContext);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            screenCaptureFailureMessage = exception.Message;
            BuddyLog.Error("Screen capture failed before AI response", exception);
        }

        lock (responseStateLock)
        {
            if (!ReferenceEquals(activeResponseCancellationTokenSource, responseCancellationTokenSource)
                || responseCancellationTokenSource.IsCancellationRequested)
            {
                return Array.Empty<WindowsScreenCapture>();
            }

            isCapturingScreens = false;
            isResponding = true;
            screenCaptureCount = screenCaptures.Count;
            screenCaptureErrorMessage = screenCaptureFailureMessage;
        }

        BuddyLog.Workflow(
            string.IsNullOrWhiteSpace(screenCaptureFailureMessage)
                ? $"AI workflow screen capture ready. CapturedScreens={screenCaptures.Count}."
                : $"AI workflow continuing without full screen capture: {screenCaptureFailureMessage}");
        NotifyResponseStateChanged();
        return screenCaptures;
    }

    private bool ShouldContinueResponse(CancellationTokenSource responseCancellationTokenSource)
    {
        lock (responseStateLock)
        {
            return ReferenceEquals(activeResponseCancellationTokenSource, responseCancellationTokenSource)
                && !responseCancellationTokenSource.IsCancellationRequested;
        }
    }

    private ClaudeResponseStateChangedEventArgs CreateResponseStateSnapshot()
    {
        lock (responseStateLock)
        {
            return new ClaudeResponseStateChangedEventArgs(
                isCapturingScreens,
                isResponding,
                screenCaptureCount,
                userTranscriptText,
                responseText,
                pointingInstructions,
                preparedNextPointingInstruction,
                preparedNextInstructionText,
                screenCaptureErrorMessage,
                responseErrorMessage);
        }
    }

    private void NotifyResponseStateChanged()
    {
        ResponseStateChanged?.Invoke(this, CreateResponseStateSnapshot());
    }
}
