using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Buddy.Windows.AI;
using Buddy.Windows.Diagnostics;

namespace Buddy.Windows.Voice;

public sealed class AssemblyAIStreamingTranscriptionService : IDisposable
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AssemblyAITemporaryTokenClient temporaryTokenClient;
    private readonly object transcriptionStateLock = new();
    private AssemblyAIStreamingTranscriptionSession? activeStreamingSession;
    private string liveTranscriptText = "";
    private string finalTranscriptText = "";
    private string? transcriptionErrorMessage;
    private bool isConnecting;
    private bool isStreaming;
    private bool isDisposed;

    public AssemblyAIStreamingTranscriptionService(AssemblyAITemporaryTokenClient temporaryTokenClient)
    {
        this.temporaryTokenClient = temporaryTokenClient;
    }

    public event EventHandler<StreamingTranscriptionStateChangedEventArgs>? TranscriptionStateChanged;

    public bool IsConnecting
    {
        get
        {
            lock (transcriptionStateLock)
            {
                return isConnecting;
            }
        }
    }

    public bool IsStreaming
    {
        get
        {
            lock (transcriptionStateLock)
            {
                return isStreaming;
            }
        }
    }

    public string? TranscriptionErrorMessage
    {
        get
        {
            lock (transcriptionStateLock)
            {
                return transcriptionErrorMessage;
            }
        }
    }

    public async Task StartSessionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);

        lock (transcriptionStateLock)
        {
            if (activeStreamingSession is not null || isConnecting)
            {
                return;
            }

            isConnecting = true;
            isStreaming = false;
            liveTranscriptText = "";
            finalTranscriptText = "";
            transcriptionErrorMessage = null;
        }

        NotifyTranscriptionStateChanged();

        AssemblyAIStreamingTranscriptionSession? streamingSession = null;

        try
        {
            BuddyLog.Workflow("Transcription workflow requesting temporary AssemblyAI token.");
            string temporaryToken = await temporaryTokenClient.FetchTemporaryTokenAsync(cancellationToken);

            streamingSession = new AssemblyAIStreamingTranscriptionSession(
                temporaryToken,
                HandleTranscriptUpdate,
                HandleFinalTranscriptReady,
                HandleTranscriptionError);

            await streamingSession.OpenAsync(cancellationToken);
            BuddyLog.Workflow("Transcription workflow stream opened.");

            lock (transcriptionStateLock)
            {
                activeStreamingSession = streamingSession;
                isConnecting = false;
                isStreaming = true;
            }

            NotifyTranscriptionStateChanged();
        }
        catch (OperationCanceledException)
        {
            BuddyLog.Workflow("Transcription workflow start cancelled.");
            streamingSession?.Cancel();

            lock (transcriptionStateLock)
            {
                activeStreamingSession = null;
                isConnecting = false;
                isStreaming = false;
            }

            NotifyTranscriptionStateChanged();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            BuddyLog.Error("AssemblyAI streaming transcription failed to start", exception);
            streamingSession?.Cancel();

            lock (transcriptionStateLock)
            {
                activeStreamingSession = null;
                isConnecting = false;
                isStreaming = false;
                transcriptionErrorMessage = exception.Message;
            }

            NotifyTranscriptionStateChanged();
        }
    }

    public void AppendAudio(MicrophoneAudioCapturedEventArgs microphoneAudioCapturedEventArguments)
    {
        AssemblyAIStreamingTranscriptionSession? streamingSession;

        lock (transcriptionStateLock)
        {
            streamingSession = activeStreamingSession;
        }

        streamingSession?.AppendAudio(microphoneAudioCapturedEventArguments.Pcm16AudioBytes);
    }

    public void RequestFinalTranscript()
    {
        AssemblyAIStreamingTranscriptionSession? streamingSession;

        lock (transcriptionStateLock)
        {
            streamingSession = activeStreamingSession;
            activeStreamingSession = null;
            isConnecting = false;
            isStreaming = false;
        }

        NotifyTranscriptionStateChanged();
        BuddyLog.Workflow("Transcription workflow final transcript requested.");
        streamingSession?.RequestFinalTranscript();
    }

    public void CancelSession()
    {
        AssemblyAIStreamingTranscriptionSession? streamingSession;

        lock (transcriptionStateLock)
        {
            streamingSession = activeStreamingSession;
            activeStreamingSession = null;
            isConnecting = false;
            isStreaming = false;
        }

        streamingSession?.Cancel();
        NotifyTranscriptionStateChanged();
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        CancelSession();
    }

    private void HandleTranscriptUpdate(string updatedTranscriptText)
    {
        lock (transcriptionStateLock)
        {
            liveTranscriptText = updatedTranscriptText;
            transcriptionErrorMessage = null;
        }

        NotifyTranscriptionStateChanged();
    }

    private void HandleFinalTranscriptReady(string completedTranscriptText)
    {
        lock (transcriptionStateLock)
        {
            liveTranscriptText = completedTranscriptText;
            finalTranscriptText = completedTranscriptText;
            isConnecting = false;
            isStreaming = false;
            transcriptionErrorMessage = null;
        }

        BuddyLog.Workflow(
            $"Transcription workflow final transcript delivered: {BuddyLog.DescribeTextForLog(completedTranscriptText)}.");
        NotifyTranscriptionStateChanged();
    }

    private void HandleTranscriptionError(Exception exception)
    {
        BuddyLog.Error("AssemblyAI streaming transcription failed", exception);

        lock (transcriptionStateLock)
        {
            activeStreamingSession = null;
            isConnecting = false;
            isStreaming = false;
            transcriptionErrorMessage = exception.Message;
        }

        NotifyTranscriptionStateChanged();
    }

    private StreamingTranscriptionStateChangedEventArgs CreateTranscriptionStateSnapshot()
    {
        lock (transcriptionStateLock)
        {
            return new StreamingTranscriptionStateChangedEventArgs(
                isConnecting,
                isStreaming,
                liveTranscriptText,
                finalTranscriptText,
                transcriptionErrorMessage);
        }
    }

    private void NotifyTranscriptionStateChanged()
    {
        TranscriptionStateChanged?.Invoke(this, CreateTranscriptionStateSnapshot());
    }

    private sealed class AssemblyAIStreamingTranscriptionSession
    {
        private const string WebSocketBaseUrl = "wss://streaming.assemblyai.com/v3/ws";
        private const int SampleRate = 16000;
        private const double ExplicitFinalTranscriptGracePeriodSeconds = 1.4;

        private readonly string temporaryToken;
        private readonly Action<string> onTranscriptUpdate;
        private readonly Action<string> onFinalTranscriptReady;
        private readonly Action<Exception> onError;
        private readonly ClientWebSocket webSocket = new();
        private readonly SemaphoreSlim sendSemaphore = new(1, 1);
        private readonly object sessionStateLock = new();
        private readonly TaskCompletionSource<bool> beginMessageReceivedCompletionSource = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenSource receiveCancellationTokenSource = new();
        private Task? receiveLoopTask;
        private bool hasDeliveredFinalTranscript;
        private bool isAwaitingExplicitFinalTranscript;
        private string latestTranscriptText = "";
        private int? activeTurnOrder;
        private string activeTurnTranscriptText = "";
        private Dictionary<int, StoredTurnTranscript> storedTurnTranscriptsByOrder = new();
        private CancellationTokenSource? explicitFinalTranscriptDeadlineCancellationTokenSource;

        public AssemblyAIStreamingTranscriptionSession(
            string temporaryToken,
            Action<string> onTranscriptUpdate,
            Action<string> onFinalTranscriptReady,
            Action<Exception> onError)
        {
            this.temporaryToken = temporaryToken;
            this.onTranscriptUpdate = onTranscriptUpdate;
            this.onFinalTranscriptReady = onFinalTranscriptReady;
            this.onError = onError;
        }

        public async Task OpenAsync(CancellationToken cancellationToken)
        {
            Uri webSocketUri = MakeWebSocketUri(temporaryToken);

            await webSocket.ConnectAsync(webSocketUri, cancellationToken);

            receiveLoopTask = Task.Run(
                () => ReceiveMessagesAsync(receiveCancellationTokenSource.Token),
                CancellationToken.None);

            await beginMessageReceivedCompletionSource.Task.WaitAsync(cancellationToken);
        }

        public void AppendAudio(byte[] pcm16AudioBytes)
        {
            if (pcm16AudioBytes.Length == 0)
            {
                return;
            }

            _ = SendBinaryMessageAsync(pcm16AudioBytes, receiveCancellationTokenSource.Token);
        }

        public void RequestFinalTranscript()
        {
            lock (sessionStateLock)
            {
                if (hasDeliveredFinalTranscript)
                {
                    return;
                }

                isAwaitingExplicitFinalTranscript = true;
                ScheduleExplicitFinalTranscriptDeadline();
            }

            _ = SendJsonMessageAsync(new SessionControlMessage("ForceEndpoint"));
        }

        public void Cancel()
        {
            explicitFinalTranscriptDeadlineCancellationTokenSource?.Cancel();
            receiveCancellationTokenSource.Cancel();

            if (webSocket.State is WebSocketState.Open
                or WebSocketState.CloseReceived
                or WebSocketState.CloseSent)
            {
                webSocket.Abort();
            }

            webSocket.Dispose();
        }

        private async Task ReceiveMessagesAsync(CancellationToken cancellationToken)
        {
            byte[] receiveBuffer = new byte[8192];

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    using MemoryStream messageStream = new();
                    WebSocketReceiveResult receiveResult;

                    do
                    {
                        receiveResult = await webSocket.ReceiveAsync(
                            new ArraySegment<byte>(receiveBuffer),
                            cancellationToken);

                        if (receiveResult.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }

                        messageStream.Write(receiveBuffer, 0, receiveResult.Count);
                    }
                    while (!receiveResult.EndOfMessage);

                    if (receiveResult.MessageType == WebSocketMessageType.Text)
                    {
                        string messageText = Encoding.UTF8.GetString(messageStream.ToArray());
                        HandleIncomingTextMessage(messageText);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                FailSession(exception);
            }
        }

        private void HandleIncomingTextMessage(string messageText)
        {
            try
            {
                MessageEnvelope? messageEnvelope = JsonSerializer.Deserialize<MessageEnvelope>(
                    messageText,
                    JsonSerializerOptions);

                switch (messageEnvelope?.Type?.ToLowerInvariant())
                {
                    case "begin":
                        beginMessageReceivedCompletionSource.TrySetResult(true);
                        break;
                    case "turn":
                        TurnMessage? turnMessage = JsonSerializer.Deserialize<TurnMessage>(
                            messageText,
                            JsonSerializerOptions);

                        if (turnMessage is not null)
                        {
                            HandleTurnMessage(turnMessage);
                        }

                        break;
                    case "termination":
                        DeliverFinalTranscriptIfAwaitingFinalTranscript();
                        break;
                    case "error":
                        ErrorMessage? errorMessage = JsonSerializer.Deserialize<ErrorMessage>(
                            messageText,
                            JsonSerializerOptions);
                        string errorText = errorMessage?.Error
                            ?? errorMessage?.Message
                            ?? "AssemblyAI returned an error.";
                        FailSession(new InvalidOperationException(errorText));
                        break;
                }
            }
            catch (Exception exception)
            {
                FailSession(exception);
            }
        }

        private void HandleTurnMessage(TurnMessage turnMessage)
        {
            string transcriptText = turnMessage.Transcript?.Trim() ?? "";

            lock (sessionStateLock)
            {
                int turnOrder = turnMessage.TurnOrder
                    ?? activeTurnOrder
                    ?? ((storedTurnTranscriptsByOrder.Keys.DefaultIfEmpty(-1).Max()) + 1);

                if (turnMessage.EndOfTurn == true || turnMessage.TurnIsFormatted == true)
                {
                    activeTurnOrder = null;
                    activeTurnTranscriptText = "";
                    StoreTurnTranscript(
                        transcriptText,
                        turnOrder,
                        turnMessage.TurnIsFormatted == true);
                }
                else
                {
                    activeTurnOrder = turnOrder;
                    activeTurnTranscriptText = transcriptText;
                }

                string fullTranscriptText = ComposeFullTranscript();
                latestTranscriptText = fullTranscriptText;

                if (!string.IsNullOrWhiteSpace(fullTranscriptText))
                {
                    onTranscriptUpdate(fullTranscriptText);
                }

                if (!isAwaitingExplicitFinalTranscript)
                {
                    return;
                }

                if (turnMessage.EndOfTurn == true || turnMessage.TurnIsFormatted == true)
                {
                    explicitFinalTranscriptDeadlineCancellationTokenSource?.Cancel();
                    explicitFinalTranscriptDeadlineCancellationTokenSource = null;
                    DeliverFinalTranscriptIfNeeded(BestAvailableTranscriptText());
                }
            }
        }

        private void StoreTurnTranscript(string transcriptText, int turnOrder, bool isFormatted)
        {
            if (string.IsNullOrWhiteSpace(transcriptText))
            {
                return;
            }

            if (storedTurnTranscriptsByOrder.TryGetValue(turnOrder, out StoredTurnTranscript existingTurnTranscript)
                && existingTurnTranscript.IsFormatted
                && !isFormatted)
            {
                return;
            }

            storedTurnTranscriptsByOrder[turnOrder] = new StoredTurnTranscript(
                transcriptText,
                isFormatted);
        }

        private string ComposeFullTranscript()
        {
            List<string> transcriptSegments = storedTurnTranscriptsByOrder
                .OrderBy(storedTurnTranscript => storedTurnTranscript.Key)
                .Select(storedTurnTranscript => storedTurnTranscript.Value.TranscriptText)
                .Where(transcriptText => !string.IsNullOrWhiteSpace(transcriptText))
                .ToList();

            string trimmedActiveTurnTranscriptText = activeTurnTranscriptText.Trim();

            if (!string.IsNullOrWhiteSpace(trimmedActiveTurnTranscriptText))
            {
                transcriptSegments.Add(trimmedActiveTurnTranscriptText);
            }

            return string.Join(" ", transcriptSegments);
        }

        private void ScheduleExplicitFinalTranscriptDeadline()
        {
            explicitFinalTranscriptDeadlineCancellationTokenSource?.Cancel();
            explicitFinalTranscriptDeadlineCancellationTokenSource = new CancellationTokenSource();
            CancellationToken deadlineCancellationToken = explicitFinalTranscriptDeadlineCancellationTokenSource.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(ExplicitFinalTranscriptGracePeriodSeconds),
                        deadlineCancellationToken);

                    lock (sessionStateLock)
                    {
                        DeliverFinalTranscriptIfNeeded(BestAvailableTranscriptText());
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });
        }

        private void DeliverFinalTranscriptIfAwaitingFinalTranscript()
        {
            lock (sessionStateLock)
            {
                if (isAwaitingExplicitFinalTranscript)
                {
                    DeliverFinalTranscriptIfNeeded(BestAvailableTranscriptText());
                }
            }
        }

        private void DeliverFinalTranscriptIfNeeded(string transcriptText)
        {
            if (hasDeliveredFinalTranscript)
            {
                return;
            }

            hasDeliveredFinalTranscript = true;
            explicitFinalTranscriptDeadlineCancellationTokenSource?.Cancel();
            explicitFinalTranscriptDeadlineCancellationTokenSource = null;
            onFinalTranscriptReady(transcriptText);
            _ = SendJsonMessageAsync(new SessionControlMessage("Terminate"));
        }

        private string BestAvailableTranscriptText()
        {
            string composedTranscriptText = ComposeFullTranscript().Trim();

            if (!string.IsNullOrWhiteSpace(composedTranscriptText))
            {
                return composedTranscriptText;
            }

            return latestTranscriptText.Trim();
        }

        private async Task SendBinaryMessageAsync(byte[] pcm16AudioBytes, CancellationToken cancellationToken)
        {
            await sendSemaphore.WaitAsync(cancellationToken);

            try
            {
                if (webSocket.State != WebSocketState.Open)
                {
                    return;
                }

                await webSocket.SendAsync(
                    new ArraySegment<byte>(pcm16AudioBytes),
                    WebSocketMessageType.Binary,
                    true,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                FailSession(exception);
            }
            finally
            {
                sendSemaphore.Release();
            }
        }

        private async Task SendJsonMessageAsync(SessionControlMessage sessionControlMessage)
        {
            string jsonMessage = JsonSerializer.Serialize(sessionControlMessage, JsonSerializerOptions);
            byte[] jsonMessageBytes = Encoding.UTF8.GetBytes(jsonMessage);

            await sendSemaphore.WaitAsync();

            try
            {
                if (webSocket.State != WebSocketState.Open)
                {
                    return;
                }

                await webSocket.SendAsync(
                    new ArraySegment<byte>(jsonMessageBytes),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                FailSession(exception);
            }
            finally
            {
                sendSemaphore.Release();
            }
        }

        private void FailSession(Exception exception)
        {
            if (hasDeliveredFinalTranscript)
            {
                return;
            }

            beginMessageReceivedCompletionSource.TrySetException(exception);

            lock (sessionStateLock)
            {
                string bestAvailableTranscriptText = BestAvailableTranscriptText();

                if (isAwaitingExplicitFinalTranscript
                    && !hasDeliveredFinalTranscript
                    && !string.IsNullOrWhiteSpace(bestAvailableTranscriptText))
                {
                    DeliverFinalTranscriptIfNeeded(bestAvailableTranscriptText);
                    return;
                }
            }

            onError(exception);
        }

        private static Uri MakeWebSocketUri(string temporaryToken)
        {
            Dictionary<string, string> queryParameters = new()
            {
                ["speech_model"] = "u3-rt-pro",
                ["sample_rate"] = SampleRate.ToString(),
                ["encoding"] = "pcm_s16le",
                ["token"] = temporaryToken
            };

            string queryString = string.Join(
                "&",
                queryParameters.Select(queryParameter =>
                    $"{Uri.EscapeDataString(queryParameter.Key)}={Uri.EscapeDataString(queryParameter.Value)}"));

            return new Uri($"{WebSocketBaseUrl}?{queryString}");
        }

        private sealed class MessageEnvelope
        {
            [JsonPropertyName("type")]
            public string? Type { get; init; }
        }

        private sealed class TurnMessage
        {
            [JsonPropertyName("transcript")]
            public string? Transcript { get; init; }

            [JsonPropertyName("turn_order")]
            public int? TurnOrder { get; init; }

            [JsonPropertyName("end_of_turn")]
            public bool? EndOfTurn { get; init; }

            [JsonPropertyName("turn_is_formatted")]
            public bool? TurnIsFormatted { get; init; }
        }

        private sealed class ErrorMessage
        {
            [JsonPropertyName("error")]
            public string? Error { get; init; }

            [JsonPropertyName("message")]
            public string? Message { get; init; }
        }

        private sealed class SessionControlMessage
        {
            public SessionControlMessage(string type)
            {
                Type = type;
            }

            [JsonPropertyName("type")]
            public string Type { get; }
        }

        private readonly record struct StoredTurnTranscript(
            string TranscriptText,
            bool IsFormatted);
    }
}
