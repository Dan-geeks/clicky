using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Buddy.Windows.Configuration;
using Buddy.Windows.Diagnostics;
using Buddy.Windows.Screen;

namespace Buddy.Windows.AI;

public sealed class ClaudeStreamingChatClient : IDisposable
{
    private const int MaxResponseTokenCount = 1024;
    private const int FastResponseTokenCount = 384;
    private static readonly TimeSpan InitialStreamTextTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan StreamingInactivityTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient httpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private bool isDisposed;

    public async Task<string> StreamResponseAsync(
        string userTranscriptText,
        IReadOnlyList<ClaudeConversationExchange> conversationHistory,
        IReadOnlyList<WindowsScreenCapture> screenCaptures,
        string? capturedFocusedWindowText,
        Action<string> onAccumulatedResponseTextChanged,
        bool preferFastModel,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);

        if (!BuddyWindowsConfiguration.IsWorkerBaseUrlConfigured)
        {
            throw new InvalidOperationException(
                $"Set {BuddyWindowsConfiguration.WorkerBaseUrlEnvironmentVariableName} to your Cloudflare Worker URL.");
        }

        Uri chatEndpointUri = BuddyWindowsConfiguration.CreateWorkerEndpointUri("chat");
        BuddyChatModelOption selectedChatModel = BuddyRuntimeModelSelection.CurrentChatModel;
        string chatProvider = preferFastModel
            ? BuddyWindowsConfiguration.GetFastChatProvider(selectedChatModel.Provider)
            : selectedChatModel.Provider;
        string chatModel = preferFastModel
            ? BuddyWindowsConfiguration.GetFastChatModel(chatProvider)
            : selectedChatModel.Model;
        BuddyLog.Info(
            $"Sending AI chat request to {chatEndpointUri}. Provider={chatProvider}; Model={chatModel}; Display={selectedChatModel.DisplayName}; Fast={preferFastModel}; Screens={screenCaptures.Count}.");
        using HttpRequestMessage requestMessage = new(HttpMethod.Post, chatEndpointUri);
        requestMessage.Content = new StringContent(
            CreateRequestJson(
                userTranscriptText,
                conversationHistory,
                screenCaptures,
                capturedFocusedWindowText,
                preferFastModel,
                chatProvider,
                chatModel),
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage responseMessage = await httpClient.SendAsync(
            requestMessage,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        Stream responseStream = await responseMessage.Content.ReadAsStreamAsync(cancellationToken);

        if (!responseMessage.IsSuccessStatusCode)
        {
            using StreamReader errorStreamReader = new(responseStream, Encoding.UTF8);
            string errorBody = await errorStreamReader.ReadToEndAsync(cancellationToken);

            BuddyLog.Error(
                $"AI chat request failed (HTTP {(int)responseMessage.StatusCode}): {BuddyLog.TrimForLog(errorBody)}");
            throw new InvalidOperationException(
                $"AI chat request failed (HTTP {(int)responseMessage.StatusCode}): {errorBody}");
        }

        string completedResponseText = await ReadServerSentEventsAsync(
            responseStream,
            onAccumulatedResponseTextChanged,
            cancellationToken);
        BuddyLog.Workflow($"AI chat stream completed. Characters={completedResponseText.Length}.");
        return completedResponseText;
    }

    public async Task<string?> StreamShortContextualSuggestionAsync(
        string? capturedFocusedWindowText,
        IReadOnlyList<WindowsScreenCapture> screenCaptures,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);

        if (!BuddyWindowsConfiguration.IsWorkerBaseUrlConfigured)
        {
            return null;
        }

        Uri chatEndpointUri = BuddyWindowsConfiguration.CreateWorkerEndpointUri("chat");
        BuddyChatModelOption selectedChatModel = BuddyRuntimeModelSelection.CurrentChatModel;
        string chatProvider = BuddyWindowsConfiguration.GetFastChatProvider(selectedChatModel.Provider);
        string chatModel = BuddyWindowsConfiguration.GetFastChatModel(chatProvider);

        List<ClaudeMessage> messages = new()
        {
            new ClaudeMessage(
                "user",
                CreateInactivitySuggestionMessageContent(capturedFocusedWindowText, screenCaptures))
        };

        ClaudeStreamingRequest requestBody = new(
            chatProvider,
            chatModel,
            96,
            true,
            ClaudeSystemPrompt.InactivitySuggestionSystemPrompt,
            messages);

        using HttpRequestMessage requestMessage = new(HttpMethod.Post, chatEndpointUri);
        requestMessage.Content = new StringContent(
            JsonSerializer.Serialize(requestBody, JsonSerializerOptions),
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage responseMessage = await httpClient.SendAsync(
            requestMessage,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!responseMessage.IsSuccessStatusCode)
        {
            return null;
        }

        Stream responseStream = await responseMessage.Content.ReadAsStreamAsync(cancellationToken);
        string suggestionText = await ReadServerSentEventsAsync(
            responseStream,
            _ => { },
            cancellationToken);

        return string.IsNullOrWhiteSpace(suggestionText) ? null : suggestionText.Trim();
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        httpClient.Dispose();
    }

    private static object CreateInactivitySuggestionMessageContent(
        string? capturedFocusedWindowText,
        IReadOnlyList<WindowsScreenCapture> screenCaptures)
    {
        bool hasCapturedFocusedWindowText = !string.IsNullOrWhiteSpace(capturedFocusedWindowText);

        if (screenCaptures.Count == 0 && !hasCapturedFocusedWindowText)
        {
            return "The user has been idle. Without seeing their screen, output \"skip\".";
        }

        List<object> contentBlocks = new();

        foreach (WindowsScreenCapture screenCapture in screenCaptures)
        {
            contentBlocks.Add(new ClaudeTextContentBlock(screenCapture.Label));
            contentBlocks.Add(new ClaudeImageContentBlock(new ClaudeImageSource(
                "base64",
                "image/jpeg",
                Convert.ToBase64String(screenCapture.ImageBytes))));
        }

        if (hasCapturedFocusedWindowText)
        {
            contentBlocks.Add(new ClaudeTextContentBlock(
                "Text content from the focused window is below. Treat this as the source of truth for what the user is reading or working in:"));
            contentBlocks.Add(new ClaudeTextContentBlock(
                $"<focused_window_text>\n{capturedFocusedWindowText}\n</focused_window_text>"));
        }

        contentBlocks.Add(new ClaudeTextContentBlock(
            "The user has been idle for a minute. Produce a single short suggestion they could trigger by pressing Ctrl + Alt, based on what is on their screen right now."));
        return contentBlocks;
    }

    private static string CreateRequestJson(
        string userTranscriptText,
        IReadOnlyList<ClaudeConversationExchange> conversationHistory,
        IReadOnlyList<WindowsScreenCapture> screenCaptures,
        string? capturedFocusedWindowText,
        bool preferFastModel,
        string chatProvider,
        string chatModel)
    {
        int maxResponseTokenCount = preferFastModel
            ? FastResponseTokenCount
            : MaxResponseTokenCount;
        List<ClaudeMessage> messages = new();

        foreach (ClaudeConversationExchange conversationExchange in conversationHistory)
        {
            messages.Add(new ClaudeMessage("user", conversationExchange.UserTranscript));
            messages.Add(new ClaudeMessage("assistant", conversationExchange.AssistantResponse));
        }

        messages.Add(new ClaudeMessage(
            "user",
            CreateCurrentUserMessageContent(userTranscriptText, screenCaptures, capturedFocusedWindowText)));

        ClaudeStreamingRequest requestBody = new(
            chatProvider,
            chatModel,
            maxResponseTokenCount,
            true,
            ClaudeSystemPrompt.ScreenAwareCompanionSystemPrompt,
            messages);

        return JsonSerializer.Serialize(requestBody, JsonSerializerOptions);
    }

    private static object CreateCurrentUserMessageContent(
        string userTranscriptText,
        IReadOnlyList<WindowsScreenCapture> screenCaptures,
        string? capturedFocusedWindowText)
    {
        bool hasCapturedFocusedWindowText = !string.IsNullOrWhiteSpace(capturedFocusedWindowText);

        if (screenCaptures.Count == 0 && !hasCapturedFocusedWindowText)
        {
            return userTranscriptText;
        }

        List<object> contentBlocks = new();

        foreach (WindowsScreenCapture screenCapture in screenCaptures)
        {
            contentBlocks.Add(new ClaudeTextContentBlock(screenCapture.Label));
            contentBlocks.Add(new ClaudeImageContentBlock(new ClaudeImageSource(
                "base64",
                "image/jpeg",
                Convert.ToBase64String(screenCapture.ImageBytes))));
        }

        contentBlocks.Add(new ClaudeTextContentBlock(ClaudeSystemPrompt.CurrentDesktopImageContext));

        if (hasCapturedFocusedWindowText)
        {
            contentBlocks.Add(new ClaudeTextContentBlock(ClaudeSystemPrompt.CapturedFocusedWindowTextContext));
            contentBlocks.Add(new ClaudeTextContentBlock(
                $"<focused_window_text>\n{capturedFocusedWindowText}\n</focused_window_text>"));
        }

        contentBlocks.Add(new ClaudeTextContentBlock($"User said: {userTranscriptText}"));
        return contentBlocks;
    }

    private static async Task<string> ReadServerSentEventsAsync(
        Stream responseStream,
        Action<string> onAccumulatedResponseTextChanged,
        CancellationToken cancellationToken)
    {
        using StreamReader streamReader = new(responseStream, Encoding.UTF8);
        StringBuilder accumulatedResponseTextBuilder = new();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? line;

            try
            {
                line = await ReadServerSentEventLineWithTimeoutAsync(
                    streamReader,
                    accumulatedResponseTextBuilder.Length == 0
                        ? InitialStreamTextTimeout
                        : StreamingInactivityTimeout,
                    cancellationToken);
            }
            catch (TimeoutException) when (accumulatedResponseTextBuilder.Length > 0)
            {
                BuddyLog.Workflow(
                    $"AI chat stream stalled after partial response. Characters={accumulatedResponseTextBuilder.Length}.");
                break;
            }

            if (line is null)
            {
                break;
            }

            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            string jsonPayload = line["data: ".Length..];

            if (jsonPayload == "[DONE]")
            {
                break;
            }

            string? textChunk = TryExtractTextDelta(jsonPayload);

            if (string.IsNullOrEmpty(textChunk))
            {
                continue;
            }

            accumulatedResponseTextBuilder.Append(textChunk);
            onAccumulatedResponseTextChanged(accumulatedResponseTextBuilder.ToString());
        }

        return accumulatedResponseTextBuilder.ToString();
    }

    private static async Task<string?> ReadServerSentEventLineWithTimeoutAsync(
        StreamReader streamReader,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Task<string?> readLineTask = streamReader.ReadLineAsync(cancellationToken).AsTask();
        Task timeoutTask = Task.Delay(timeout, cancellationToken);
        Task completedTask = await Task.WhenAny(readLineTask, timeoutTask);

        if (completedTask == readLineTask)
        {
            return await readLineTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException(
            $"AI response stream stalled for {timeout.TotalSeconds:0} seconds.");
    }

   private static string? TryExtractTextDelta(string jsonPayload)
    {
        using JsonDocument eventJsonDocument = JsonDocument.Parse(jsonPayload);
        JsonElement eventRoot = eventJsonDocument.RootElement;

        if (eventRoot.TryGetProperty("type", out JsonElement typeElement))
        {
            if (typeElement.GetString() == "error")
            {
                string errorMessage = eventRoot.TryGetProperty("error", out JsonElement errorData)
                    && errorData.TryGetProperty("message", out JsonElement messageElement)
                        ? messageElement.GetString() ?? "Unknown error"
                        : "Unknown streaming error";

                BuddyLog.Error($"AI chat stream returned an error event: {errorMessage}");
                throw new InvalidOperationException(errorMessage);
            }

            if (typeElement.GetString() != "content_block_delta")
            {
                return null;
            }
        }
        else
        {
            return null;
        }

        if (!eventRoot.TryGetProperty("delta", out JsonElement deltaElement)
            || !deltaElement.TryGetProperty("type", out JsonElement deltaTypeElement)
            || deltaTypeElement.GetString() != "text_delta")
        {
            return null;
        }

        return deltaElement.TryGetProperty("text", out JsonElement textElement)
            ? textElement.GetString()
            : null;
    }

    private sealed class ClaudeStreamingRequest
    {
        public ClaudeStreamingRequest(
            string provider,
            string model,
            int maxTokens,
            bool stream,
            string system,
            List<ClaudeMessage> messages)
        {
            Provider = provider;
            Model = model;
            MaxTokens = maxTokens;
            Stream = stream;
            System = system;
            Messages = messages;
        }

        [JsonPropertyName("provider")]
        public string Provider { get; }

        [JsonPropertyName("model")]
        public string Model { get; }

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; }

        [JsonPropertyName("stream")]
        public bool Stream { get; }

        [JsonPropertyName("system")]
        public string System { get; }

        [JsonPropertyName("messages")]
        public List<ClaudeMessage> Messages { get; }
    }

    private sealed class ClaudeMessage
    {
        public ClaudeMessage(string role, object content)
        {
            Role = role;
            Content = content;
        }

        [JsonPropertyName("role")]
        public string Role { get; }

        [JsonPropertyName("content")]
        public object Content { get; }
    }

    private sealed class ClaudeTextContentBlock
    {
        public ClaudeTextContentBlock(string text)
        {
            Text = text;
        }

        [JsonPropertyName("type")]
        public string Type => "text";

        [JsonPropertyName("text")]
        public string Text { get; }
    }

    private sealed class ClaudeImageContentBlock
    {
        public ClaudeImageContentBlock(ClaudeImageSource source)
        {
            Source = source;
        }

        [JsonPropertyName("type")]
        public string Type => "image";

        [JsonPropertyName("source")]
        public ClaudeImageSource Source { get; }
    }

    private sealed class ClaudeImageSource
    {
        public ClaudeImageSource(string type, string mediaType, string data)
        {
            Type = type;
            MediaType = mediaType;
            Data = data;
        }

        [JsonPropertyName("type")]
        public string Type { get; }

        [JsonPropertyName("media_type")]
        public string MediaType { get; }

        [JsonPropertyName("data")]
        public string Data { get; }
    }

    private static class ClaudeSystemPrompt
    {
        public const string CurrentDesktopImageContext = """
        The screenshots above describe the Windows desktop state. Live viewport screenshots are the source of truth for visible apps, windows, taskbar items, menus, buttons, fields, icons, and text. Context-only scroll captures may show nearby content after a temporary scroll; use them to understand surrounding context, but do not use them for POINT coordinates because Buddy has restored the original scroll position.
        """;

        public const string InactivitySuggestionSystemPrompt = """
        you are buddy, generating one short proactive tip for an idle user. you receive screenshots and possibly the verbatim text of the focused window. output exactly one tip in 4 to 12 words, phrased as a friendly question or offer the user could trigger with ctrl+alt. examples: "Want me to summarize this article?", "Need help drafting a reply?", "Want me to explain this code?", "Want me to translate this page?". be specific to what is on screen — name the kind of content (article, email, code, document, video, search results, etc.) when it helps. no markdown, no quotes, no point tags, no sign-off. if the screen content is too generic, unreadable, sensitive, or you cannot tell what the user is doing, output exactly the single word: skip.
        """;

        public const string CapturedFocusedWindowTextContext = """
        The block below tagged <focused_window_text> is the verbatim text content of the focused window — Buddy auto-selected and copied it with Ctrl+A and Ctrl+C and then restored the user's clipboard. This is the actual text (not OCR) and is much more reliable than reading text out of the screenshot. Use it as the source of truth when summarizing, explaining, translating, quoting, or answering questions about the document, page, or article the user is reading. The screenshots still show the visual layout and remain the source of truth for clickable targets and POINT coordinates.
        """;

        public const string ScreenAwareCompanionSystemPrompt = """
        you are buddy, a calm Windows tray companion that can see the user's desktop screenshots, speak short guidance aloud, and move a blue pointer overlay to visible targets. you are helping in an ongoing session and can use recent text history, but the latest screenshots are always the source of truth for desktop state.

        voice style:
        - be brief, direct, and warm. default to one short sentence.
        - write for text-to-speech. no markdown, bullets, tables, code fences, or visual formatting in the visible response.
        - do not say "simply" or "just".
        - do not end with confirmation questions. do not ask "do you want me to" or "should i".
        - do not read code verbatim unless the user specifically asks for exact text.

        desktop understanding:
        - each screenshot is preceded by a label containing screen number, pixel dimensions, virtual origin, and whether the cursor is on that screen.
        - some screenshots may be labeled context-only scroll capture. use them only to understand nearby content above or below the live viewport.
        - point only to targets visible in a live viewport screenshot. never point to a target that appears only in a context-only scroll capture.
        - inspect the screenshots before answering any request about apps, windows, files, menus, buttons, fields, icons, tabs, settings, or anything visible on the desktop.
        - prioritize the screen labeled "cursor is here, primary focus", but use another screen when the relevant target is clearly there.
        - if useful content appears only in a context-only scroll capture, explain it briefly or tell the user to scroll to it before giving the next click target.
        - never pretend a hidden app, button, or file is visible. if the requested app or target is not visible, point to visible Windows Search, Start, or the taskbar search entry when available.
        - if no screenshot is available, or the screenshots are not enough to identify a real target, say the limitation briefly and do not point blindly.

        guided navigation:
        - guide one visible action at a time. choose the next target the user should move to or click now.
        - when the current task is finished, say that it is done and append [POINT:none]. do not invent another step.
        - when the user has completed a previous step and asks to continue, verify the fresh screenshot first. continue only from the live screen state.
        - if a cached next step is now wrong according to the fresh screenshot, correct it with the live target.

        point tags:
        - when pointing helps, append exactly one current point tag at the very end of the visible instruction: [POINT:x,y:short label:screenN].
        - coordinates are pixels inside the labeled screenshot, origin 0,0 at top-left. x increases right, y increases down.
        - aim for the geometric center of the clickable hitbox, not the edge or the icon's outer glow. for a button, pick the middle of the rectangle. for a menu item, pick the middle of the row. for an icon, pick the middle of the visible glyph not the surrounding empty space. before committing the coordinate, mentally double-check by re-locating the same target in the screenshot and confirming the (x,y) lands inside it.
        - screenN must match the screen number in the screenshot label. always include screenN. if the target is on a different screen than the cursor screen, still use that screen's local pixel origin, not virtual desktop coordinates.
        - the label should be one to three words, lowercase when natural.
        - prefer pointing over describing. if a clickable target is visible and naming alone would be ambiguous, point. only skip pointing for pure conversation answers, summaries, or when no relevant target is visible.
        - do not mention point tags in the visible response.
        - if pointing does not help, append [POINT:none].

        helpful follow-up:
        - the user can press Tab while a pointing instruction is on screen to ask for a more detailed step-by-step explanation of how to perform the action. when you give a non-trivial step (anything beyond "click this obvious button"), end the visible sentence with a short hint such as "press tab if you want a step-by-step" so the user knows the option exists. skip the hint for trivial single clicks where extra detail would feel patronizing.
        - when the user follow-up prompt asks for a step-by-step breakdown, give numbered, plain-language micro-steps the user can follow with their own hands, then still append the next [POINT:...] tag for the immediate action.

        one-step-ahead cache:
        - after the current [POINT:...] tag, you may include hidden next-step tags only when the next target is confidently inferable from the current screenshot.
        - hidden format: [NEXTTEXT:short next instruction] [NEXTPOINT:x,y:short label:screenN].
        - hidden next-step tags are not spoken or displayed; they let buddy move faster while a fresh screenshot verifies the state.
        - if the next target depends on what happens after the user's click, append [NEXTPOINT:none] instead of guessing.
        - never include more than one NEXTTEXT and one NEXTPOINT.

        response shape:
        - for desktop steps: visible instruction, then [POINT:...], then optional hidden NEXTTEXT/NEXTPOINT.
        - for done: visible completion sentence, then [POINT:none].
        - for non-desktop questions: answer normally and append [POINT:none].

        typing-aware steps:
        - when a step asks the user to type or paste text, lead the visible sentence with the verb "type" or "paste" and include the exact text in quotes — e.g. "click the search box, then type \"hello world\"". buddy will detect the "type" / "paste" keyword and keep the instruction on screen after the click so the user does not lose the text mid-typing.
        - if the text to type is more than one short phrase, give it on its own line below the instruction so it is easy to read at a glance.
        - still append the [POINT:...] tag for the click target. once the user finishes typing they will press Ctrl+Alt to ask for the next step themselves.
        """;
    }
}
