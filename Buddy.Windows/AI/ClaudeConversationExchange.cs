namespace Buddy.Windows.AI;

public sealed class ClaudeConversationExchange
{
    public ClaudeConversationExchange(string userTranscript, string assistantResponse)
    {
        UserTranscript = userTranscript;
        AssistantResponse = assistantResponse;
    }

    public string UserTranscript { get; }

    public string AssistantResponse { get; }
}
