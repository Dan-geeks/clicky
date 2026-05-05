using System;
using System.Collections.Generic;
using Buddy.Windows.Pointing;

namespace Buddy.Windows.AI;

public sealed class ClaudeResponseStateChangedEventArgs : EventArgs
{
    public ClaudeResponseStateChangedEventArgs(
        bool isCapturingScreens,
        bool isResponding,
        int screenCaptureCount,
        string userTranscriptText,
        string responseText,
        IReadOnlyList<PointingInstruction> pointingInstructions,
        PointingInstruction? preparedNextPointingInstruction,
        string preparedNextInstructionText,
        string? screenCaptureErrorMessage,
        string? responseErrorMessage)
    {
        IsCapturingScreens = isCapturingScreens;
        IsResponding = isResponding;
        ScreenCaptureCount = screenCaptureCount;
        UserTranscriptText = userTranscriptText;
        ResponseText = responseText;
        PointingInstructions = pointingInstructions;
        PreparedNextPointingInstruction = preparedNextPointingInstruction;
        PreparedNextInstructionText = preparedNextInstructionText;
        ScreenCaptureErrorMessage = screenCaptureErrorMessage;
        ResponseErrorMessage = responseErrorMessage;
    }

    public bool IsCapturingScreens { get; }

    public bool IsResponding { get; }

    public int ScreenCaptureCount { get; }

    public string UserTranscriptText { get; }

    public string ResponseText { get; }

    public IReadOnlyList<PointingInstruction> PointingInstructions { get; }

    public PointingInstruction? PreparedNextPointingInstruction { get; }

    public string PreparedNextInstructionText { get; }

    public string? ScreenCaptureErrorMessage { get; }

    public string? ResponseErrorMessage { get; }
}
