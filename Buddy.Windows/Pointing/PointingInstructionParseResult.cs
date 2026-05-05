using System.Collections.Generic;

namespace Buddy.Windows.Pointing;

public sealed class PointingInstructionParseResult
{
    public PointingInstructionParseResult(
        string visibleResponseText,
        IReadOnlyList<PointingInstruction> pointingInstructions,
        PointingInstruction? preparedNextPointingInstruction,
        string preparedNextInstructionText)
    {
        VisibleResponseText = visibleResponseText;
        PointingInstructions = pointingInstructions;
        PreparedNextPointingInstruction = preparedNextPointingInstruction;
        PreparedNextInstructionText = preparedNextInstructionText;
    }

    public string VisibleResponseText { get; }

    public IReadOnlyList<PointingInstruction> PointingInstructions { get; }

    public PointingInstruction? PreparedNextPointingInstruction { get; }

    public string PreparedNextInstructionText { get; }
}
