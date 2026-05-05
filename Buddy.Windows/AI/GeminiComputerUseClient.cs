using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Buddy.Windows.Configuration;
using Buddy.Windows.Diagnostics;

namespace Buddy.Windows.AI;

/// <summary>
/// Talks to the Cloudflare Worker's <c>/computer-use</c> route, which proxies to Google's
/// Gemini Computer Use API. Each turn the agent loop calls <see cref="RequestNextTurnAsync"/>
/// with the running tool history; Gemini returns either the next FunctionCall to execute
/// or final text indicating the task is done.
/// </summary>
public sealed class GeminiComputerUseClient : IDisposable
{
    private static readonly TimeSpan ComputerUseRequestTimeout = TimeSpan.FromSeconds(120);
    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient httpClient = new()
    {
        Timeout = ComputerUseRequestTimeout
    };

    private bool isDisposed;

    public async Task<ComputerUseTurnResponse> RequestNextTurnAsync(
        string systemInstructionText,
        IReadOnlyList<ComputerUseMessage> conversationMessages,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);

        if (!BuddyWindowsConfiguration.IsWorkerBaseUrlConfigured)
        {
            throw new InvalidOperationException(
                $"Set {BuddyWindowsConfiguration.WorkerBaseUrlEnvironmentVariableName} to your Cloudflare Worker URL.");
        }

        Uri computerUseEndpointUri = BuddyWindowsConfiguration.CreateWorkerEndpointUri("computer-use");
        string computerUseModel = BuddyWindowsConfiguration.GetComputerUseModel();

        BuddyLog.Info(
            $"Sending Computer Use request to {computerUseEndpointUri}. Model={computerUseModel}; Messages={conversationMessages.Count}.");

        ComputerUseRequestBody requestBody = new(
            computerUseModel,
            systemInstructionText,
            "ENVIRONMENT_DESKTOP_OS",
            new List<ComputerUseMessage>(conversationMessages));

        using HttpRequestMessage requestMessage = new(HttpMethod.Post, computerUseEndpointUri)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(requestBody, JsonSerializerOptions),
                Encoding.UTF8,
                "application/json")
        };

        using HttpResponseMessage responseMessage = await httpClient.SendAsync(
            requestMessage,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);

        string responseBodyText = await responseMessage.Content.ReadAsStringAsync(cancellationToken);

        if (!responseMessage.IsSuccessStatusCode)
        {
            BuddyLog.Error(
                $"Computer Use request failed (HTTP {(int)responseMessage.StatusCode}): {BuddyLog.TrimForLog(responseBodyText)}");
            throw new InvalidOperationException(
                $"Computer Use request failed (HTTP {(int)responseMessage.StatusCode}): {responseBodyText}");
        }

        ComputerUseTurnResponse? turnResponse = JsonSerializer.Deserialize<ComputerUseTurnResponse>(
            responseBodyText,
            JsonSerializerOptions);

        if (turnResponse is null)
        {
            throw new InvalidOperationException("Computer Use response was empty or unparseable.");
        }

        BuddyLog.Workflow(
            $"Computer Use turn returned. FunctionCalls={turnResponse.FunctionCalls.Count}; IsComplete={turnResponse.IsComplete}; FinishReason={turnResponse.RawFinishReason ?? "(none)"}.");
        return turnResponse;
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

    private sealed class ComputerUseRequestBody
    {
        public ComputerUseRequestBody(
            string model,
            string system,
            string environment,
            List<ComputerUseMessage> messages)
        {
            Model = model;
            System = system;
            Environment = environment;
            Messages = messages;
        }

        [JsonPropertyName("model")]
        public string Model { get; }

        [JsonPropertyName("system")]
        public string System { get; }

        [JsonPropertyName("environment")]
        public string Environment { get; }

        [JsonPropertyName("messages")]
        public List<ComputerUseMessage> Messages { get; }
    }
}

public sealed class ComputerUseMessage
{
    public ComputerUseMessage(string role, List<ComputerUseContentBlock> content)
    {
        Role = role;
        Content = content;
    }

    [JsonPropertyName("role")]
    public string Role { get; }

    [JsonPropertyName("content")]
    public List<ComputerUseContentBlock> Content { get; }
}

/// <summary>
/// Polymorphic block sent to / received from the Worker's Computer Use route. The
/// Worker translates these to the Gemini SDK's <c>function_call</c>/<c>function_response</c>
/// /<c>inline_data</c>/<c>text</c> parts before forwarding to Google.
/// </summary>
public sealed class ComputerUseContentBlock
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("source")]
    public ComputerUseImageSource? Source { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("args")]
    public Dictionary<string, JsonElement>? Args { get; set; }

    [JsonPropertyName("result")]
    public string? Result { get; set; }

    [JsonPropertyName("screenshot")]
    public ComputerUseScreenshotPayload? Screenshot { get; set; }

    public static ComputerUseContentBlock CreateText(string text)
    {
        return new ComputerUseContentBlock
        {
            Type = "text",
            Text = text
        };
    }

    public static ComputerUseContentBlock CreateImage(string mediaType, string base64Data)
    {
        return new ComputerUseContentBlock
        {
            Type = "image",
            Source = new ComputerUseImageSource("base64", mediaType, base64Data)
        };
    }

    public static ComputerUseContentBlock CreateFunctionCall(
        string functionCallIdentifier,
        string functionName,
        Dictionary<string, JsonElement> functionArguments)
    {
        return new ComputerUseContentBlock
        {
            Type = "function_call",
            Id = functionCallIdentifier,
            Name = functionName,
            Args = functionArguments
        };
    }

    public static ComputerUseContentBlock CreateFunctionResponse(
        string functionCallIdentifier,
        string functionName,
        string functionResultDescription,
        ComputerUseScreenshotPayload? screenshotAfterAction)
    {
        return new ComputerUseContentBlock
        {
            Type = "function_response",
            Id = functionCallIdentifier,
            Name = functionName,
            Result = functionResultDescription,
            Screenshot = screenshotAfterAction
        };
    }
}

public sealed class ComputerUseImageSource
{
    public ComputerUseImageSource(string sourceType, string mediaType, string base64Data)
    {
        Type = sourceType;
        MediaType = mediaType;
        Data = base64Data;
    }

    [JsonPropertyName("type")]
    public string Type { get; }

    [JsonPropertyName("media_type")]
    public string MediaType { get; }

    [JsonPropertyName("data")]
    public string Data { get; }
}

public sealed class ComputerUseScreenshotPayload
{
    public ComputerUseScreenshotPayload(string mediaType, string base64Data)
    {
        MediaType = mediaType;
        Data = base64Data;
    }

    [JsonPropertyName("media_type")]
    public string MediaType { get; }

    [JsonPropertyName("data")]
    public string Data { get; }
}

public sealed class ComputerUseTurnResponse
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("function_calls")]
    public List<ComputerUseFunctionCall> FunctionCalls { get; set; } = new();

    [JsonPropertyName("is_complete")]
    public bool IsComplete { get; set; }

    [JsonPropertyName("raw_finish_reason")]
    public string? RawFinishReason { get; set; }
}

public sealed class ComputerUseFunctionCall
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("args")]
    public Dictionary<string, JsonElement> Args { get; set; } = new();
}
