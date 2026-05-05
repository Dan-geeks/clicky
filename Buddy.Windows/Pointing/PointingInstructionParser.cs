using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Buddy.Windows.Pointing;

public static class PointingInstructionParser
{
    // Accepts either the full form "[POINT:x,y:label:screenN]" or the lenient form
    // "[POINT:x,y:label]" with no trailing screen suffix. The macOS prompt/parser allows
    // omitting the screen and falls back to whichever display the user's cursor is on, so
    // mirroring that here keeps Windows from silently dropping a guidance step when the
    // model omits or misformats the screen suffix.
    private static readonly Regex CompletePointingInstructionRegex = new(
        @"\[POINT:\s*(?<x>-?\d+(?:\.\d+)?)\s*,\s*(?<y>-?\d+(?:\.\d+)?)\s*:\s*(?<label>[^\]]*?)(?:\s*:\s*screen(?<screenNumber>\d+))?\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex CompleteNextPointingInstructionRegex = new(
        @"\[NEXTPOINT:\s*(?<x>-?\d+(?:\.\d+)?)\s*,\s*(?<y>-?\d+(?:\.\d+)?)\s*:\s*(?<label>[^\]]*?)(?:\s*:\s*screen(?<screenNumber>\d+))?\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex NonePointingInstructionRegex = new(
        @"\[(?:POINT|NEXTPOINT):\s*none\s*\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex CompleteNextInstructionTextRegex = new(
        @"\[NEXTTEXT:\s*(?<text>[^\]]*?)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex RepeatedHorizontalWhitespaceRegex = new(
        "[ \t]{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WhitespaceBeforePunctuationRegex = new(
        @"\s+([,.!?;:])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static PointingInstructionParseResult Parse(string responseText)
    {
        List<PointingInstruction> pointingInstructions = new();
        PointingInstruction? preparedNextPointingInstruction = null;
        string preparedNextInstructionText = "";

        string responseTextWithoutCompleteNextPointTags = CompleteNextPointingInstructionRegex.Replace(
            responseText,
            match =>
            {
                preparedNextPointingInstruction = CreatePointingInstruction(match);
                return "";
            });

        string responseTextWithoutCompleteNextTextTags = CompleteNextInstructionTextRegex.Replace(
            responseTextWithoutCompleteNextPointTags,
            match =>
            {
                preparedNextInstructionText = match.Groups["text"].Value.Trim();
                return "";
            });

        string responseTextWithoutCompletePointTags = CompletePointingInstructionRegex.Replace(
            responseTextWithoutCompleteNextTextTags,
            match =>
            {
                PointingInstruction? pointingInstruction = CreatePointingInstruction(match);

                if (pointingInstruction is not null)
                {
                    pointingInstructions.Add(pointingInstruction);
                }

                return "";
            });

        string responseTextWithoutNonePointTags = NonePointingInstructionRegex.Replace(
            responseTextWithoutCompletePointTags,
            "");
        string visibleResponseText = RemoveTrailingPartialPointTag(responseTextWithoutNonePointTags);
        visibleResponseText = WhitespaceBeforePunctuationRegex.Replace(visibleResponseText, "$1");
        visibleResponseText = RepeatedHorizontalWhitespaceRegex.Replace(visibleResponseText, " ");
        visibleResponseText = visibleResponseText.Trim();

        return new PointingInstructionParseResult(
            visibleResponseText,
            pointingInstructions,
            preparedNextPointingInstruction,
            preparedNextInstructionText);
    }

    private static PointingInstruction? CreatePointingInstruction(Match match)
    {
        if (!double.TryParse(
                match.Groups["x"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double xInScreenPixels)
            || !double.TryParse(
                match.Groups["y"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double yInScreenPixels))
        {
            return null;
        }

        // ScreenNumber == 0 is the sentinel for "auto / use the cursor's current screen";
        // the overlay resolves it at present time. The screen group is optional in the
        // regex, so a missing or unparseable suffix maps to that sentinel.
        Group screenNumberGroup = match.Groups["screenNumber"];
        int screenNumber = 0;

        if (screenNumberGroup.Success
            && int.TryParse(
                screenNumberGroup.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsedScreenNumber))
        {
            screenNumber = parsedScreenNumber;
        }

        string label = match.Groups["label"].Value.Trim();
        return new PointingInstruction(
            xInScreenPixels,
            yInScreenPixels,
            label,
            screenNumber);
    }

    private static string RemoveTrailingPartialPointTag(string responseText)
    {
        int lastPointTagStartIndex = responseText.LastIndexOf(
            "[POINT:",
            System.StringComparison.OrdinalIgnoreCase);
        int lastNextPointTagStartIndex = responseText.LastIndexOf(
            "[NEXTPOINT:",
            System.StringComparison.OrdinalIgnoreCase);
        int lastNextTextTagStartIndex = responseText.LastIndexOf(
            "[NEXTTEXT:",
            System.StringComparison.OrdinalIgnoreCase);

        int lastHiddenTagStartIndex = System.Math.Max(
            lastPointTagStartIndex,
            System.Math.Max(lastNextPointTagStartIndex, lastNextTextTagStartIndex));

        if (lastHiddenTagStartIndex < 0)
        {
            return responseText;
        }

        string trailingText = responseText[lastHiddenTagStartIndex..];

        return trailingText.Contains(']')
            ? responseText
            : responseText[..lastHiddenTagStartIndex];
    }
}
