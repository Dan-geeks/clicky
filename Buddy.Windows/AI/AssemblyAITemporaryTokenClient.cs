using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Buddy.Windows.Configuration;
using Buddy.Windows.Diagnostics;

namespace Buddy.Windows.AI;

public sealed class AssemblyAITemporaryTokenClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient httpClient = new();
    private bool isDisposed;

    public async Task<string> FetchTemporaryTokenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);

        if (!BuddyWindowsConfiguration.IsWorkerBaseUrlConfigured)
        {
            throw new InvalidOperationException(
                $"Set {BuddyWindowsConfiguration.WorkerBaseUrlEnvironmentVariableName} to your Cloudflare Worker URL.");
        }

        Uri transcribeTokenEndpointUri = BuddyWindowsConfiguration.CreateWorkerEndpointUri("transcribe-token");
        BuddyLog.Info($"Requesting AssemblyAI temporary token from {transcribeTokenEndpointUri}.");
        using HttpRequestMessage requestMessage = new(HttpMethod.Post, transcribeTokenEndpointUri);
        using HttpResponseMessage responseMessage = await httpClient.SendAsync(
            requestMessage,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        string responseBody = await responseMessage.Content.ReadAsStringAsync(cancellationToken);

        if (!responseMessage.IsSuccessStatusCode)
        {
            BuddyLog.Error(
                $"AssemblyAI token request failed (HTTP {(int)responseMessage.StatusCode}): {BuddyLog.TrimForLog(responseBody)}");
            throw new InvalidOperationException(
                $"Failed to fetch AssemblyAI token (HTTP {(int)responseMessage.StatusCode}): {responseBody}");
        }

        TemporaryTokenResponse? temporaryTokenResponse = JsonSerializer.Deserialize<TemporaryTokenResponse>(
            responseBody,
            JsonSerializerOptions);

        if (string.IsNullOrWhiteSpace(temporaryTokenResponse?.Token))
        {
            BuddyLog.Error(
                $"Worker returned an invalid AssemblyAI token response: {BuddyLog.TrimForLog(responseBody)}");
            throw new InvalidOperationException("Worker returned an invalid AssemblyAI token response.");
        }

        BuddyLog.Info("Received AssemblyAI temporary token.");
        return temporaryTokenResponse.Token;
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

    private sealed class TemporaryTokenResponse
    {
        [JsonPropertyName("token")]
        public string? Token { get; init; }
    }
}
