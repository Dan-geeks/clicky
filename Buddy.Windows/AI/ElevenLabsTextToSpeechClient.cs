using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Buddy.Windows.Configuration;
using Buddy.Windows.Diagnostics;

namespace Buddy.Windows.AI;

public sealed class ElevenLabsTextToSpeechClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    private bool isDisposed;

    public async Task<byte[]> FetchSpeechAudioAsync(string text, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);

        if (!BuddyWindowsConfiguration.IsWorkerBaseUrlConfigured)
        {
            throw new InvalidOperationException(
                $"Set {BuddyWindowsConfiguration.WorkerBaseUrlEnvironmentVariableName} to your Cloudflare Worker URL.");
        }

        Uri textToSpeechEndpointUri = BuddyWindowsConfiguration.CreateWorkerEndpointUri("tts");
        BuddyLog.Info($"Requesting ElevenLabs TTS from {textToSpeechEndpointUri}. Characters={text.Length}.");
        using HttpRequestMessage requestMessage = new(HttpMethod.Post, textToSpeechEndpointUri);
        requestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));
        requestMessage.Content = new StringContent(
            CreateRequestJson(text),
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage responseMessage = await httpClient.SendAsync(
            requestMessage,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        byte[] responseBody = await responseMessage.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!responseMessage.IsSuccessStatusCode)
        {
            string errorBody = Encoding.UTF8.GetString(responseBody);
            BuddyLog.Error(
                $"TTS request failed (HTTP {(int)responseMessage.StatusCode}): {BuddyLog.TrimForLog(errorBody)}");
            throw new InvalidOperationException(
                $"TTS request failed (HTTP {(int)responseMessage.StatusCode}): {errorBody}");
        }

        if (responseBody.Length == 0)
        {
            BuddyLog.Error("TTS returned an empty audio response.");
            throw new InvalidOperationException("TTS returned an empty audio response.");
        }

        return responseBody;
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

    private static string CreateRequestJson(string text)
    {
        TextToSpeechRequest requestBody = new(
            text,
            "eleven_flash_v2_5",
            new VoiceSettings(0.5, 0.75));

        return JsonSerializer.Serialize(requestBody, JsonSerializerOptions);
    }

    private sealed class TextToSpeechRequest
    {
        public TextToSpeechRequest(
            string text,
            string modelId,
            VoiceSettings voiceSettings)
        {
            Text = text;
            ModelId = modelId;
            VoiceSettings = voiceSettings;
        }

        [JsonPropertyName("text")]
        public string Text { get; }

        [JsonPropertyName("model_id")]
        public string ModelId { get; }

        [JsonPropertyName("voice_settings")]
        public VoiceSettings VoiceSettings { get; }
    }

    private sealed class VoiceSettings
    {
        public VoiceSettings(double stability, double similarityBoost)
        {
            Stability = stability;
            SimilarityBoost = similarityBoost;
        }

        [JsonPropertyName("stability")]
        public double Stability { get; }

        [JsonPropertyName("similarity_boost")]
        public double SimilarityBoost { get; }
    }
}
