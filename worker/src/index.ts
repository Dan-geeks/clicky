/**
 * Buddy Proxy Worker
 *
 * Proxies requests to AI, AssemblyAI, and ElevenLabs APIs so the app never
 * ships with raw API keys. Keys are stored as Cloudflare secrets.
 *
 * Routes:
 *   POST /chat             -> Anthropic, OpenAI, xAI Grok, or Gemini chat
 *   POST /computer-use     -> Gemini Computer Use generateContent (non-streaming, structured tool calls)
 *   POST /tts              -> ElevenLabs TTS API
 *   POST /transcribe-token -> AssemblyAI temporary streaming token
 */

type ChatProvider = "anthropic" | "openai" | "grok" | "gemini";

interface Env {
  ANTHROPIC_API_KEY?: string;
  OPENAI_API_KEY?: string;
  XAI_API_KEY?: string;
  GEMINI_API_KEY?: string;
  ELEVENLABS_API_KEY: string;
  ELEVENLABS_VOICE_ID: string;
  ASSEMBLYAI_API_KEY: string;
}

interface ChatRequest {
  provider?: string;
  model: string;
  max_tokens: number;
  stream: boolean;
  system: string;
  messages: ChatMessage[];
}

interface ChatMessage {
  role: "user" | "assistant";
  content: string | AnthropicContentBlock[];
}

interface AnthropicContentBlock {
  type: "text" | "image";
  text?: string;
  source?: {
    type: "base64";
    media_type: string;
    data: string;
  };
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    const requestId = crypto.randomUUID();
    const startedAt = Date.now();

    if (request.method !== "POST") {
      console.warn(`[${requestId}] ${url.pathname} rejected method ${request.method}`);
      return new Response("Method not allowed", { status: 405 });
    }

    try {
      if (url.pathname === "/chat") {
        return await handleChat(request, env, requestId, startedAt);
      }

      if (url.pathname === "/computer-use") {
        return await handleComputerUse(request, env, requestId, startedAt);
      }

      if (url.pathname === "/tts") {
        return await handleTTS(request, env, requestId, startedAt);
      }

      if (url.pathname === "/transcribe-token") {
        return await handleTranscribeToken(env, requestId, startedAt);
      }
    } catch (error) {
      console.error(
        `[${requestId}] ${url.pathname} unhandled error after ${Date.now() - startedAt}ms:`,
        error
      );
      return jsonResponse({ error: String(error) }, 500);
    }

    console.warn(`[${requestId}] ${url.pathname} not found`);
    return new Response("Not found", { status: 404 });
  },
};

async function handleChat(
  request: Request,
  env: Env,
  requestId: string,
  startedAt: number
): Promise<Response> {
  const chatRequest = (await request.json()) as ChatRequest;
  const provider = normalizeChatProvider(chatRequest.provider);
  console.log(`[${requestId}] /chat received: ${summarizeChatRequest(chatRequest, provider)}`);

  if (provider === "anthropic") {
    return await handleAnthropicChat(chatRequest, env, requestId, startedAt);
  }

  if (provider === "gemini") {
    return await handleGeminiChat(chatRequest, env, requestId, startedAt);
  }

  return await handleOpenAICompatibleChat(chatRequest, env, provider, requestId, startedAt);
}

async function handleAnthropicChat(
  chatRequest: ChatRequest,
  env: Env,
  requestId: string,
  startedAt: number
): Promise<Response> {
  if (!env.ANTHROPIC_API_KEY) {
    console.error(`[${requestId}] /chat Anthropic secret missing`);
    return jsonResponse({ error: "ANTHROPIC_API_KEY is not configured." }, 500);
  }

  const { provider: _provider, ...anthropicRequestBody } = chatRequest;

  const response = await fetch("https://api.anthropic.com/v1/messages", {
    method: "POST",
    headers: {
      "x-api-key": env.ANTHROPIC_API_KEY,
      "anthropic-version": "2023-06-01",
      "content-type": "application/json",
    },
    body: JSON.stringify(anthropicRequestBody),
  });

  if (!response.ok) {
    const errorBody = await response.text();
    console.error(`[${requestId}] /chat Anthropic API error ${response.status}: ${trimForLog(errorBody)}`);
    return new Response(errorBody, {
      status: response.status,
      headers: { "content-type": "application/json" },
    });
  }

  console.log(
    `[${requestId}] /chat Anthropic stream opened in ${Date.now() - startedAt}ms with status ${response.status}`
  );
  return new Response(response.body, {
    status: response.status,
    headers: createEventStreamHeaders(response.headers),
  });
}

async function handleOpenAICompatibleChat(
  chatRequest: ChatRequest,
  env: Env,
  provider: "openai" | "grok",
  requestId: string,
  startedAt: number
): Promise<Response> {
  const apiKey = provider === "openai" ? env.OPENAI_API_KEY : env.XAI_API_KEY;
  const providerName = provider === "openai" ? "OpenAI" : "xAI";

  if (!apiKey) {
    const secretName = provider === "openai" ? "OPENAI_API_KEY" : "XAI_API_KEY";
    console.error(`[${requestId}] /chat ${providerName} secret missing: ${secretName}`);
    return jsonResponse({ error: `${secretName} is not configured.` }, 500);
  }

  const upstreamUrl = provider === "openai"
    ? "https://api.openai.com/v1/chat/completions"
    : "https://api.x.ai/v1/chat/completions";
  const upstreamRequestBody = createOpenAICompatibleChatRequest(chatRequest);
  const response = await fetch(upstreamUrl, {
    method: "POST",
    headers: {
      authorization: `Bearer ${apiKey}`,
      "content-type": "application/json",
    },
    body: JSON.stringify(upstreamRequestBody),
  });

  if (!response.ok) {
    const errorBody = await response.text();
    console.error(`[${requestId}] /chat ${providerName} API error ${response.status}: ${trimForLog(errorBody)}`);
    return new Response(errorBody, {
      status: response.status,
      headers: { "content-type": "application/json" },
    });
  }

  console.log(
    `[${requestId}] /chat ${providerName} stream opened in ${Date.now() - startedAt}ms with status ${response.status}`
  );
  return new Response(normalizeOpenAICompatibleStream(response.body), {
    status: response.status,
    headers: createEventStreamHeaders(response.headers),
  });
}

async function handleGeminiChat(
  chatRequest: ChatRequest,
  env: Env,
  requestId: string,
  startedAt: number
): Promise<Response> {
  if (!env.GEMINI_API_KEY) {
    console.error(`[${requestId}] /chat Gemini secret missing: GEMINI_API_KEY`);
    return jsonResponse({ error: "GEMINI_API_KEY is not configured." }, 500);
  }

  const upstreamUrl =
    `https://generativelanguage.googleapis.com/v1beta/${createGeminiModelPath(chatRequest.model)}:streamGenerateContent?alt=sse`;
  const upstreamRequestBody = createGeminiChatRequest(chatRequest);
  const response = await fetch(upstreamUrl, {
    method: "POST",
    headers: {
      "x-goog-api-key": env.GEMINI_API_KEY,
      "content-type": "application/json",
    },
    body: JSON.stringify(upstreamRequestBody),
  });

  if (!response.ok) {
    const errorBody = await response.text();
    console.error(`[${requestId}] /chat Gemini API error ${response.status}: ${trimForLog(errorBody)}`);
    return new Response(errorBody, {
      status: response.status,
      headers: { "content-type": "application/json" },
    });
  }

  console.log(
    `[${requestId}] /chat Gemini stream opened in ${Date.now() - startedAt}ms with status ${response.status}`
  );
  return new Response(normalizeGeminiStream(response.body), {
    status: response.status,
    headers: createEventStreamHeaders(response.headers),
  });
}

function createOpenAICompatibleChatRequest(chatRequest: ChatRequest): unknown {
  return {
    model: chatRequest.model,
    max_tokens: chatRequest.max_tokens,
    stream: true,
    messages: [
      {
        role: "system",
        content: chatRequest.system,
      },
      ...chatRequest.messages.map(convertMessageToOpenAICompatibleMessage),
    ],
  };
}

function createGeminiChatRequest(chatRequest: ChatRequest): unknown {
  return {
    system_instruction: {
      parts: [
        {
          text: chatRequest.system,
        },
      ],
    },
    contents: chatRequest.messages
      .map(convertMessageToGeminiContent)
      .filter((content): content is { role: string; parts: unknown[] } => content.parts.length > 0),
    generationConfig: {
      maxOutputTokens: chatRequest.max_tokens,
    },
  };
}

function convertMessageToGeminiContent(chatMessage: ChatMessage): { role: string; parts: unknown[] } {
  return {
    role: chatMessage.role === "assistant" ? "model" : "user",
    parts: typeof chatMessage.content === "string"
      ? [{ text: chatMessage.content }]
      : chatMessage.content
        .map(convertContentBlockToGeminiPart)
        .filter((contentPart): contentPart is unknown => contentPart !== null),
  };
}

function convertContentBlockToGeminiPart(contentBlock: AnthropicContentBlock): unknown | null {
  if (contentBlock.type === "text") {
    return {
      text: contentBlock.text ?? "",
    };
  }

  if (contentBlock.type === "image" && contentBlock.source) {
    return {
      inline_data: {
        mime_type: contentBlock.source.media_type,
        data: contentBlock.source.data,
      },
    };
  }

  return null;
}

function createGeminiModelPath(model: string): string {
  const trimmedModel = model.trim();
  const modelName = trimmedModel.startsWith("models/")
    ? trimmedModel.slice("models/".length)
    : trimmedModel;

  return `models/${encodeURIComponent(modelName)}`;
}

function convertMessageToOpenAICompatibleMessage(chatMessage: ChatMessage): unknown {
  if (typeof chatMessage.content === "string") {
    return {
      role: chatMessage.role,
      content: chatMessage.content,
    };
  }

  return {
    role: chatMessage.role,
    content: chatMessage.content
      .map(convertContentBlockToOpenAICompatibleContentPart)
      .filter((contentPart): contentPart is unknown => contentPart !== null),
  };
}

function convertContentBlockToOpenAICompatibleContentPart(
  contentBlock: AnthropicContentBlock
): unknown | null {
  if (contentBlock.type === "text") {
    return {
      type: "text",
      text: contentBlock.text ?? "",
    };
  }

  if (contentBlock.type === "image" && contentBlock.source) {
    return {
      type: "image_url",
      image_url: {
        url: `data:${contentBlock.source.media_type};base64,${contentBlock.source.data}`,
        detail: "high",
      },
    };
  }

  return null;
}

function normalizeOpenAICompatibleStream(
  upstreamBody: ReadableStream<Uint8Array> | null
): ReadableStream<Uint8Array> {
  if (!upstreamBody) {
    return new ReadableStream({
      start(controller) {
        controller.close();
      },
    });
  }

  const textDecoder = new TextDecoder();
  const textEncoder = new TextEncoder();
  const reader = upstreamBody.getReader();
  let bufferedText = "";

  return new ReadableStream<Uint8Array>({
    async pull(controller) {
      while (true) {
        const { done, value } = await reader.read();

        if (done) {
          enqueueDoneEvent(controller, textEncoder);
          controller.close();
          return;
        }

        bufferedText += textDecoder.decode(value, { stream: true });
        const completeLines = bufferedText.split(/\r?\n/);
        bufferedText = completeLines.pop() ?? "";

        for (const line of completeLines) {
          const normalizedEvent = convertOpenAICompatibleLineToAnthropicEvent(line);

          if (normalizedEvent) {
            controller.enqueue(textEncoder.encode(normalizedEvent));
          }
        }

        if (completeLines.length > 0) {
          return;
        }
      }
    },
    cancel() {
      return reader.cancel();
    },
  });
}

function normalizeGeminiStream(
  upstreamBody: ReadableStream<Uint8Array> | null
): ReadableStream<Uint8Array> {
  if (!upstreamBody) {
    return new ReadableStream({
      start(controller) {
        controller.close();
      },
    });
  }

  const textDecoder = new TextDecoder();
  const textEncoder = new TextEncoder();
  const reader = upstreamBody.getReader();
  let bufferedText = "";

  return new ReadableStream<Uint8Array>({
    async pull(controller) {
      while (true) {
        const { done, value } = await reader.read();

        if (done) {
          enqueueDoneEvent(controller, textEncoder);
          controller.close();
          return;
        }

        bufferedText += textDecoder.decode(value, { stream: true });
        const completeLines = bufferedText.split(/\r?\n/);
        bufferedText = completeLines.pop() ?? "";

        for (const line of completeLines) {
          const normalizedEvent = convertGeminiLineToAnthropicEvent(line);

          if (normalizedEvent) {
            controller.enqueue(textEncoder.encode(normalizedEvent));
          }
        }

        if (completeLines.length > 0) {
          return;
        }
      }
    },
    cancel() {
      return reader.cancel();
    },
  });
}

function convertGeminiLineToAnthropicEvent(line: string): string | null {
  if (!line.startsWith("data: ")) {
    return null;
  }

  const jsonPayload = line.slice("data: ".length).trim();

  if (jsonPayload === "[DONE]") {
    return "data: [DONE]\n\n";
  }

  try {
    const parsedPayload = JSON.parse(jsonPayload);
    const errorMessage = parsedPayload?.error?.message;

    if (typeof errorMessage === "string" && errorMessage.length > 0) {
      return `data: ${JSON.stringify({
        type: "error",
        error: {
          message: errorMessage,
        },
      })}\n\n`;
    }

    const textDelta = (parsedPayload?.candidates ?? [])
      .flatMap((candidate: { content?: { parts?: Array<{ text?: string }> } }) => candidate.content?.parts ?? [])
      .map((part: { text?: string }) => part.text ?? "")
      .join("");

    if (textDelta.length === 0) {
      return null;
    }

    return `data: ${JSON.stringify({
      type: "content_block_delta",
      delta: {
        type: "text_delta",
        text: textDelta,
      },
    })}\n\n`;
  } catch (error) {
    console.error("[/chat] Could not parse Gemini stream event:", error);
    return null;
  }
}

function convertOpenAICompatibleLineToAnthropicEvent(line: string): string | null {
  if (!line.startsWith("data: ")) {
    return null;
  }

  const jsonPayload = line.slice("data: ".length).trim();

  if (jsonPayload === "[DONE]") {
    return "data: [DONE]\n\n";
  }

  try {
    const parsedPayload = JSON.parse(jsonPayload);
    const textDelta = parsedPayload?.choices?.[0]?.delta?.content;

    if (typeof textDelta !== "string" || textDelta.length === 0) {
      return null;
    }

    return `data: ${JSON.stringify({
      type: "content_block_delta",
      delta: {
        type: "text_delta",
        text: textDelta,
      },
    })}\n\n`;
  } catch (error) {
    console.error("[/chat] Could not parse OpenAI-compatible stream event:", error);
    return null;
  }
}

function summarizeChatRequest(chatRequest: ChatRequest, provider: ChatProvider): string {
  let imageCount = 0;
  let textCharacterCount = chatRequest.system?.length ?? 0;

  for (const message of chatRequest.messages ?? []) {
    if (typeof message.content === "string") {
      textCharacterCount += message.content.length;
      continue;
    }

    for (const contentBlock of message.content) {
      if (contentBlock.type === "image") {
        imageCount += 1;
      } else {
        textCharacterCount += contentBlock.text?.length ?? 0;
      }
    }
  }

  return [
    `provider=${provider}`,
    `model=${chatRequest.model}`,
    `messages=${chatRequest.messages?.length ?? 0}`,
    `images=${imageCount}`,
    `textChars=${textCharacterCount}`,
    `maxTokens=${chatRequest.max_tokens}`,
    `stream=${chatRequest.stream}`,
  ].join("; ");
}

function enqueueDoneEvent(
  controller: ReadableStreamDefaultController<Uint8Array>,
  textEncoder: TextEncoder
): void {
  controller.enqueue(textEncoder.encode("data: [DONE]\n\n"));
}

async function handleTranscribeToken(env: Env, requestId: string, startedAt: number): Promise<Response> {
  console.log(`[${requestId}] /transcribe-token received`);
  const response = await fetch(
    "https://streaming.assemblyai.com/v3/token?expires_in_seconds=480",
    {
      method: "GET",
      headers: {
        authorization: env.ASSEMBLYAI_API_KEY,
      },
    }
  );

  if (!response.ok) {
    const errorBody = await response.text();
    console.error(`[${requestId}] /transcribe-token AssemblyAI error ${response.status}: ${trimForLog(errorBody)}`);
    return new Response(errorBody, {
      status: response.status,
      headers: { "content-type": "application/json" },
    });
  }

  const data = await response.text();
  console.log(
    `[${requestId}] /transcribe-token completed in ${Date.now() - startedAt}ms with status ${response.status}`
  );
  return new Response(data, {
    status: 200,
    headers: { "content-type": "application/json" },
  });
}

async function handleTTS(
  request: Request,
  env: Env,
  requestId: string,
  startedAt: number
): Promise<Response> {
  const body = await request.text();
  const voiceId = env.ELEVENLABS_VOICE_ID;
  console.log(`[${requestId}] /tts received. BodyBytes=${body.length}; VoiceConfigured=${Boolean(voiceId)}`);

  const response = await fetch(
    `https://api.elevenlabs.io/v1/text-to-speech/${voiceId}`,
    {
      method: "POST",
      headers: {
        "xi-api-key": env.ELEVENLABS_API_KEY,
        "content-type": "application/json",
        accept: "audio/mpeg",
      },
      body,
    }
  );

  if (!response.ok) {
    const errorBody = await response.text();
    console.error(`[${requestId}] /tts ElevenLabs API error ${response.status}: ${trimForLog(errorBody)}`);
    return new Response(errorBody, {
      status: response.status,
      headers: { "content-type": "application/json" },
    });
  }

  console.log(
    `[${requestId}] /tts completed in ${Date.now() - startedAt}ms with status ${response.status}`
  );
  return new Response(response.body, {
    status: response.status,
    headers: {
      "content-type": response.headers.get("content-type") || "audio/mpeg",
    },
  });
}

function normalizeChatProvider(provider: string | undefined): ChatProvider {
  const normalizedProvider = provider?.trim().toLowerCase();

  if (normalizedProvider === "openai") {
    return "openai";
  }

  if (normalizedProvider === "grok" || normalizedProvider === "xai" || normalizedProvider === "x.ai") {
    return "grok";
  }

  if (normalizedProvider === "gemini" || normalizedProvider === "google") {
    return "gemini";
  }

  return "anthropic";
}

function createEventStreamHeaders(upstreamHeaders: Headers): Headers {
  return new Headers({
    "content-type": upstreamHeaders.get("content-type") || "text/event-stream",
    "cache-control": "no-cache",
  });
}

function trimForLog(text: string, maximumLength = 1000): string {
  const trimmedText = text.trim();
  return trimmedText.length <= maximumLength
    ? trimmedText
    : `${trimmedText.slice(0, maximumLength)}...`;
}

function jsonResponse(body: unknown, status: number): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json" },
  });
}

// ============================================================================
// Gemini Computer Use route (/computer-use)
// ----------------------------------------------------------------------------
// The Computer Use API returns structured FunctionCalls (click_at, type_text_at,
// scroll_at, etc.) that the Windows client executes via SendInput. The Windows
// client posts the user's prompt + each turn's screenshot + accumulated tool
// history; this route forwards to Gemini's generateContent (non-streaming) with
// the computer_use tool enabled and returns a normalized JSON envelope.
// ============================================================================

interface ComputerUseRequest {
  model: string;
  system?: string;
  environment?: string;
  exclude_predefined_functions?: string[];
  messages: ComputerUseMessage[];
}

interface ComputerUseMessage {
  role: "user" | "assistant";
  content: ComputerUseContentBlock[];
}

type ComputerUseContentBlock =
  | { type: "text"; text: string }
  | { type: "image"; source: { type: "base64"; media_type: string; data: string } }
  | { type: "function_call"; id: string; name: string; args: Record<string, unknown> }
  | {
      type: "function_response";
      id: string;
      name: string;
      result?: Record<string, unknown> | string;
      screenshot?: { media_type: string; data: string };
      url?: string;
    };

async function handleComputerUse(
  request: Request,
  env: Env,
  requestId: string,
  startedAt: number
): Promise<Response> {
  if (!env.GEMINI_API_KEY) {
    console.error(`[${requestId}] /computer-use Gemini secret missing: GEMINI_API_KEY`);
    return jsonResponse({ error: "GEMINI_API_KEY is not configured." }, 500);
  }

  const computerUseRequest = (await request.json()) as ComputerUseRequest;
  console.log(
    `[${requestId}] /computer-use received: Model=${computerUseRequest.model}; Messages=${computerUseRequest.messages.length}; Environment=${computerUseRequest.environment ?? "ENVIRONMENT_DESKTOP_OS"}`
  );

  const upstreamUrl =
    `https://generativelanguage.googleapis.com/v1beta/${createGeminiModelPath(computerUseRequest.model)}:generateContent`;
  const upstreamRequestBody = createGeminiComputerUseRequest(computerUseRequest);
  const response = await fetch(upstreamUrl, {
    method: "POST",
    headers: {
      "x-goog-api-key": env.GEMINI_API_KEY,
      "content-type": "application/json",
    },
    body: JSON.stringify(upstreamRequestBody),
  });

  const upstreamBodyText = await response.text();

  if (!response.ok) {
    console.error(
      `[${requestId}] /computer-use Gemini API error ${response.status}: ${trimForLog(upstreamBodyText)}`
    );
    return new Response(upstreamBodyText, {
      status: response.status,
      headers: { "content-type": "application/json" },
    });
  }

  const normalizedEnvelope = normalizeComputerUseResponse(upstreamBodyText, requestId);
  console.log(
    `[${requestId}] /computer-use completed in ${Date.now() - startedAt}ms; FunctionCalls=${normalizedEnvelope.function_calls.length}; IsComplete=${normalizedEnvelope.is_complete}`
  );
  return jsonResponse(normalizedEnvelope, 200);
}

function createGeminiComputerUseRequest(computerUseRequest: ComputerUseRequest): unknown {
  const computerUseToolConfig: Record<string, unknown> = {
    environment: computerUseRequest.environment ?? "ENVIRONMENT_DESKTOP_OS",
  };

  if (computerUseRequest.exclude_predefined_functions && computerUseRequest.exclude_predefined_functions.length > 0) {
    computerUseToolConfig.excluded_predefined_functions = computerUseRequest.exclude_predefined_functions;
  }

  return {
    system_instruction: computerUseRequest.system
      ? { parts: [{ text: computerUseRequest.system }] }
      : undefined,
    contents: computerUseRequest.messages
      .map(convertComputerUseMessageToGeminiContent)
      .filter((content): content is { role: string; parts: unknown[] } => content.parts.length > 0),
    tools: [
      {
        computer_use: computerUseToolConfig,
      },
    ],
  };
}

function convertComputerUseMessageToGeminiContent(message: ComputerUseMessage): {
  role: string;
  parts: unknown[];
} {
  return {
    role: message.role === "assistant" ? "model" : "user",
    parts: message.content
      .map(convertComputerUseBlockToGeminiPart)
      .filter((part): part is unknown => part !== null),
  };
}

function convertComputerUseBlockToGeminiPart(block: ComputerUseContentBlock): unknown | null {
  if (block.type === "text") {
    return { text: block.text };
  }

  if (block.type === "image") {
    return {
      inline_data: {
        mime_type: block.source.media_type,
        data: block.source.data,
      },
    };
  }

  if (block.type === "function_call") {
    return {
      function_call: {
        name: block.name,
        args: block.args,
      },
    };
  }

  if (block.type === "function_response") {
    const responsePayload: Record<string, unknown> = {};

    if (typeof block.result === "string") {
      responsePayload.output = block.result;
    } else if (block.result) {
      Object.assign(responsePayload, block.result);
    }

    if (block.url) {
      responsePayload.url = block.url;
    }

    if (block.screenshot) {
      responsePayload.screenshot = {
        mime_type: block.screenshot.media_type,
        data: block.screenshot.data,
      };
    }

    return {
      function_response: {
        name: block.name,
        response: responsePayload,
      },
    };
  }

  return null;
}

interface NormalizedComputerUseResponse {
  text: string;
  function_calls: Array<{ id: string; name: string; args: Record<string, unknown> }>;
  is_complete: boolean;
  raw_finish_reason: string | null;
}

function normalizeComputerUseResponse(
  upstreamBodyText: string,
  requestId: string
): NormalizedComputerUseResponse {
  let upstreamJson: unknown;

  try {
    upstreamJson = JSON.parse(upstreamBodyText);
  } catch (parseError) {
    console.error(`[${requestId}] /computer-use upstream JSON parse failed: ${parseError}`);
    return { text: "", function_calls: [], is_complete: true, raw_finish_reason: null };
  }

  const candidates = (upstreamJson as { candidates?: unknown[] }).candidates ?? [];

  if (candidates.length === 0) {
    return { text: "", function_calls: [], is_complete: true, raw_finish_reason: null };
  }

  const firstCandidate = candidates[0] as {
    content?: { parts?: unknown[] };
    finishReason?: string;
  };
  const finishReason = firstCandidate.finishReason ?? null;
  const parts = firstCandidate.content?.parts ?? [];

  let collectedText = "";
  const collectedFunctionCalls: Array<{ id: string; name: string; args: Record<string, unknown> }> = [];

  for (const partUnknown of parts) {
    const part = partUnknown as {
      text?: string;
      function_call?: { name?: string; args?: Record<string, unknown> };
      functionCall?: { name?: string; args?: Record<string, unknown> };
    };

    if (typeof part.text === "string" && part.text.length > 0) {
      collectedText += part.text;
      continue;
    }

    const functionCall = part.function_call ?? part.functionCall;

    if (functionCall && typeof functionCall.name === "string") {
      collectedFunctionCalls.push({
        id: `fc-${collectedFunctionCalls.length + 1}`,
        name: functionCall.name,
        args: functionCall.args ?? {},
      });
    }
  }

  const isComplete = collectedFunctionCalls.length === 0;

  return {
    text: collectedText,
    function_calls: collectedFunctionCalls,
    is_complete: isComplete,
    raw_finish_reason: finishReason,
  };
}
