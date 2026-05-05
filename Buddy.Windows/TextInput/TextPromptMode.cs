namespace Buddy.Windows.TextInput;

public enum TextPromptMode
{
    /// <summary>
    /// Ctrl+Alt+Space — typed prompt routes through the standard Claude/Gemini chat
    /// pipeline that returns text + optional [POINT] tags.
    /// </summary>
    AskBuddy,

    /// <summary>
    /// Ctrl+Alt+A — typed prompt starts a Gemini Computer Use agent run that actually
    /// clicks, types, and scrolls on the user's behalf.
    /// </summary>
    ActOnDesktop
}
