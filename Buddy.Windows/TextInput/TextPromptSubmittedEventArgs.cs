using System;

namespace Buddy.Windows.TextInput;

public sealed class TextPromptSubmittedEventArgs : EventArgs
{
    public TextPromptSubmittedEventArgs(string promptText)
    {
        PromptText = promptText;
    }

    public string PromptText { get; }
}
