using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Buddy.Windows.AI;
using Buddy.Windows.Configuration;
using Buddy.Windows.Diagnostics;
using Buddy.Windows.Screen;

namespace Buddy.Windows.ComputerUse;

/// <summary>
/// Drives the multi-turn Computer Use agent loop. Unlike the existing chat path which
/// gets one response and stops, an agent task keeps going until Gemini returns no more
/// FunctionCalls (or the safety turn cap is hit). On each iteration: capture screens,
/// ask Gemini for the next action, execute it, capture screens again, and feed the
/// result back as a function_response. Existing overlay logic is untouched — this lives
/// alongside the standard pointing flow.
/// </summary>
public sealed class ComputerUseAgentCoordinator : IDisposable
{
    private const string ComputerUseSystemInstruction = """
    You are Buddy's Windows Computer Use agent. You can see desktop screenshots and call
    predefined Computer Use actions (click_at, type_text_at, scroll_at, key_combination,
    drag_and_drop, wait_for, etc.) to actually operate Windows on the user's behalf.

    Operating principles:
    - Each screenshot is the live state of the user's Windows desktop. Use the latest
      screenshot as the source of truth before deciding the next action.
    - Coordinates in your action arguments are normalized 0..999 within the screenshot
      you are looking at. Always pick the center of the target.
    - When multiple displays are attached, every screenshot is labeled with its screen
      number; pass `screen` (1-based) in the action arguments to disambiguate. If you do
      not include `screen`, Buddy will use whichever display the cursor is currently on.
    - Prefer one action per turn so the next screenshot can verify the effect before you
      decide what to do next. Wait briefly with `wait_for` if a UI is animating.
    - When the user's task is complete, respond with plain text (no function call) that
      says it is done in one short sentence.
    - Refuse irreversible or destructive actions (deleting files, sending money, sending
      messages on the user's behalf) and ask the user to confirm in writing first.
    """;

    private readonly GeminiComputerUseClient geminiComputerUseClient;
    private readonly WindowsScreenCaptureService windowsScreenCaptureService;
    private CancellationTokenSource? activeRunCancellationTokenSource;
    private Task? activeRunTask;
    private bool isDisposed;

    public ComputerUseAgentCoordinator(
        GeminiComputerUseClient geminiComputerUseClient,
        WindowsScreenCaptureService windowsScreenCaptureService)
    {
        this.geminiComputerUseClient = geminiComputerUseClient;
        this.windowsScreenCaptureService = windowsScreenCaptureService;
    }

    public event EventHandler<ComputerUseAgentStateChangedEventArgs>? StateChanged;

    public bool IsAgentRunning => activeRunTask is { IsCompleted: false };

    public void StartAgentRun(string userInstructionText)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);

        if (string.IsNullOrWhiteSpace(userInstructionText))
        {
            return;
        }

        if (IsAgentRunning)
        {
            BuddyLog.Workflow("Computer Use agent run requested while another run is active; cancelling the active run first.");
            CancelAgentRun();
        }

        activeRunCancellationTokenSource = new CancellationTokenSource();
        CancellationToken runCancellationToken = activeRunCancellationTokenSource.Token;
        activeRunTask = Task.Run(
            () => RunAgentLoopAsync(userInstructionText, runCancellationToken),
            runCancellationToken);
    }

    public void CancelAgentRun()
    {
        try
        {
            activeRunCancellationTokenSource?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        CancelAgentRun();
        activeRunCancellationTokenSource?.Dispose();
        activeRunCancellationTokenSource = null;
    }

    private async Task RunAgentLoopAsync(string userInstructionText, CancellationToken cancellationToken)
    {
        BuddyLog.Workflow(
            $"Computer Use agent run starting. Prompt={BuddyLog.DescribeTextForLog(userInstructionText)}.");
        RaiseStateChanged(ComputerUseAgentStatus.Capturing, "Looking at your screen...", finalAssistantText: null, errorMessage: null);

        List<ComputerUseMessage> conversationMessages = new();
        IReadOnlyList<WindowsScreenCapture> initialScreenCaptures;

        try
        {
            initialScreenCaptures = await windowsScreenCaptureService.CaptureAllScreensAsJpegAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            RaiseStateChanged(ComputerUseAgentStatus.Cancelled, "Cancelled.", null, null);
            return;
        }
        catch (Exception screenCaptureException)
        {
            RaiseStateChanged(ComputerUseAgentStatus.Failed, "Screen capture failed.", null, screenCaptureException.Message);
            return;
        }

        conversationMessages.Add(new ComputerUseMessage(
            "user",
            CreateInitialUserMessageContent(userInstructionText, initialScreenCaptures)));

        int maximumAgentTurns = BuddyWindowsConfiguration.GetComputerUseMaxTurns();
        IReadOnlyList<WindowsScreenCapture> latestScreenCaptures = initialScreenCaptures;

        for (int agentTurnNumber = 1; agentTurnNumber <= maximumAgentTurns; agentTurnNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RaiseStateChanged(
                ComputerUseAgentStatus.Thinking,
                $"Thinking (turn {agentTurnNumber}/{maximumAgentTurns})...",
                finalAssistantText: null,
                errorMessage: null);

            ComputerUseTurnResponse turnResponse;

            try
            {
                turnResponse = await geminiComputerUseClient.RequestNextTurnAsync(
                    ComputerUseSystemInstruction,
                    conversationMessages,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                RaiseStateChanged(ComputerUseAgentStatus.Cancelled, "Cancelled.", null, null);
                return;
            }
            catch (Exception apiException)
            {
                RaiseStateChanged(ComputerUseAgentStatus.Failed, "Gemini Computer Use request failed.", null, apiException.Message);
                return;
            }

            List<ComputerUseContentBlock> assistantTurnContent = new();

            if (!string.IsNullOrEmpty(turnResponse.Text))
            {
                assistantTurnContent.Add(ComputerUseContentBlock.CreateText(turnResponse.Text));
            }

            foreach (ComputerUseFunctionCall pendingFunctionCall in turnResponse.FunctionCalls)
            {
                assistantTurnContent.Add(ComputerUseContentBlock.CreateFunctionCall(
                    pendingFunctionCall.Id,
                    pendingFunctionCall.Name,
                    pendingFunctionCall.Args));
            }

            if (assistantTurnContent.Count > 0)
            {
                conversationMessages.Add(new ComputerUseMessage("assistant", assistantTurnContent));
            }

            if (turnResponse.IsComplete || turnResponse.FunctionCalls.Count == 0)
            {
                BuddyLog.Workflow(
                    $"Computer Use agent run completed in {agentTurnNumber} turn(s). FinishReason={turnResponse.RawFinishReason ?? "(none)"}.");
                RaiseStateChanged(
                    ComputerUseAgentStatus.Completed,
                    string.IsNullOrWhiteSpace(turnResponse.Text) ? "Done." : turnResponse.Text,
                    finalAssistantText: turnResponse.Text,
                    errorMessage: null);
                return;
            }

            ComputerUseActionHandler actionHandler = new(latestScreenCaptures);
            List<ComputerUseContentBlock> functionResponseContent = new();

            foreach (ComputerUseFunctionCall pendingFunctionCall in turnResponse.FunctionCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RaiseStateChanged(
                    ComputerUseAgentStatus.Acting,
                    $"Running {pendingFunctionCall.Name}...",
                    finalAssistantText: null,
                    errorMessage: null);

                string functionResultDescription = await actionHandler.ExecuteAsync(pendingFunctionCall);
                functionResponseContent.Add(ComputerUseContentBlock.CreateFunctionResponse(
                    pendingFunctionCall.Id,
                    pendingFunctionCall.Name,
                    functionResultDescription,
                    screenshotAfterAction: null));
            }

            RaiseStateChanged(ComputerUseAgentStatus.Capturing, "Looking at the new screen...", null, null);

            // Give the UI a moment to settle before the next screenshot — menu animations,
            // window opens, and field focus transitions usually take a few hundred ms.
            await Task.Delay(450, cancellationToken);

            try
            {
                latestScreenCaptures = await windowsScreenCaptureService.CaptureAllScreensAsJpegAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                RaiseStateChanged(ComputerUseAgentStatus.Cancelled, "Cancelled.", null, null);
                return;
            }
            catch (Exception screenCaptureException)
            {
                RaiseStateChanged(ComputerUseAgentStatus.Failed, "Screen capture failed.", null, screenCaptureException.Message);
                return;
            }

            // Attach the fresh screenshot to the FIRST function_response in this batch so
            // Gemini can verify what the action did. Anything else stays without an image.
            if (functionResponseContent.Count > 0 && latestScreenCaptures.Count > 0)
            {
                WindowsScreenCapture primaryScreenCapture = latestScreenCaptures[0];
                ComputerUseContentBlock firstFunctionResponse = functionResponseContent[0];
                firstFunctionResponse.Screenshot = new ComputerUseScreenshotPayload(
                    "image/jpeg",
                    Convert.ToBase64String(primaryScreenCapture.ImageBytes));
            }

            // Also append the raw screenshot blocks so multi-display setups still feed all
            // displays back. The function_response screenshot above is the primary, but
            // some Gemini revisions appreciate an explicit image part too.
            foreach (WindowsScreenCapture screenCapture in latestScreenCaptures)
            {
                functionResponseContent.Add(ComputerUseContentBlock.CreateText(screenCapture.Label));
                functionResponseContent.Add(ComputerUseContentBlock.CreateImage(
                    "image/jpeg",
                    Convert.ToBase64String(screenCapture.ImageBytes)));
            }

            conversationMessages.Add(new ComputerUseMessage("user", functionResponseContent));
        }

        BuddyLog.Workflow(
            $"Computer Use agent hit the safety turn cap of {maximumAgentTurns}; stopping the run.");
        RaiseStateChanged(
            ComputerUseAgentStatus.Failed,
            $"Stopped after {maximumAgentTurns} turns.",
            finalAssistantText: null,
            errorMessage: $"Reached the safety turn cap ({maximumAgentTurns}). Set BUDDY_COMPUTER_USE_MAX_TURNS to raise it.");
    }

    private static List<ComputerUseContentBlock> CreateInitialUserMessageContent(
        string userInstructionText,
        IReadOnlyList<WindowsScreenCapture> initialScreenCaptures)
    {
        List<ComputerUseContentBlock> contentBlocks = new();

        foreach (WindowsScreenCapture screenCapture in initialScreenCaptures)
        {
            contentBlocks.Add(ComputerUseContentBlock.CreateText(screenCapture.Label));
            contentBlocks.Add(ComputerUseContentBlock.CreateImage(
                "image/jpeg",
                Convert.ToBase64String(screenCapture.ImageBytes)));
        }

        contentBlocks.Add(ComputerUseContentBlock.CreateText(
            $"User task: {userInstructionText}\n\n"
            + "Decide the next single action. Respond with exactly one Computer Use function call when more "
            + "work is needed, or with plain text when the task is complete."));
        return contentBlocks;
    }

    private void RaiseStateChanged(
        ComputerUseAgentStatus status,
        string statusText,
        string? finalAssistantText,
        string? errorMessage)
    {
        StateChanged?.Invoke(
            this,
            new ComputerUseAgentStateChangedEventArgs(status, statusText, finalAssistantText, errorMessage));
    }
}

public enum ComputerUseAgentStatus
{
    Capturing,
    Thinking,
    Acting,
    Completed,
    Cancelled,
    Failed
}

public sealed class ComputerUseAgentStateChangedEventArgs : EventArgs
{
    public ComputerUseAgentStateChangedEventArgs(
        ComputerUseAgentStatus status,
        string statusText,
        string? finalAssistantText,
        string? errorMessage)
    {
        Status = status;
        StatusText = statusText;
        FinalAssistantText = finalAssistantText;
        ErrorMessage = errorMessage;
    }

    public ComputerUseAgentStatus Status { get; }

    public string StatusText { get; }

    public string? FinalAssistantText { get; }

    public string? ErrorMessage { get; }
}
