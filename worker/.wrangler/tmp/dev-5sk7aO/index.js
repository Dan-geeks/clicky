var __defProp = Object.defineProperty;
var __name = (target, value) => __defProp(target, "name", { value, configurable: true });

// .wrangler/tmp/bundle-Vd7jLx/checked-fetch.js
var urls = /* @__PURE__ */ new Set();
function checkURL(request, init) {
  const url = request instanceof URL ? request : new URL(
    (typeof request === "string" ? new Request(request, init) : request).url
  );
  if (url.port && url.port !== "443" && url.protocol === "https:") {
    if (!urls.has(url.toString())) {
      urls.add(url.toString());
      console.warn(
        `WARNING: known issue with \`fetch()\` requests to custom HTTPS ports in published Workers:
 - ${url.toString()} - the custom port will be ignored when the Worker is published using the \`wrangler deploy\` command.
`
      );
    }
  }
}
__name(checkURL, "checkURL");
globalThis.fetch = new Proxy(globalThis.fetch, {
  apply(target, thisArg, argArray) {
    const [request, init] = argArray;
    checkURL(request, init);
    return Reflect.apply(target, thisArg, argArray);
  }
});

// .wrangler/tmp/bundle-Vd7jLx/strip-cf-connecting-ip-header.js
function stripCfConnectingIPHeader(input, init) {
  const request = new Request(input, init);
  request.headers.delete("CF-Connecting-IP");
  return request;
}
__name(stripCfConnectingIPHeader, "stripCfConnectingIPHeader");
globalThis.fetch = new Proxy(globalThis.fetch, {
  apply(target, thisArg, argArray) {
    return Reflect.apply(target, thisArg, [
      stripCfConnectingIPHeader.apply(null, argArray)
    ]);
  }
});

// src/index.ts
var src_default = {
  async fetch(request, env) {
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
  }
};
async function handleChat(request, env, requestId, startedAt) {
  const chatRequest = await request.json();
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
__name(handleChat, "handleChat");
async function handleAnthropicChat(chatRequest, env, requestId, startedAt) {
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
      "content-type": "application/json"
    },
    body: JSON.stringify(anthropicRequestBody)
  });
  if (!response.ok) {
    const errorBody = await response.text();
    console.error(`[${requestId}] /chat Anthropic API error ${response.status}: ${trimForLog(errorBody)}`);
    return new Response(errorBody, {
      status: response.status,
      headers: { "content-type": "application/json" }
    });
  }
  console.log(
    `[${requestId}] /chat Anthropic stream opened in ${Date.now() - startedAt}ms with status ${response.status}`
  );
  return new Response(response.body, {
    status: response.status,
    headers: createEventStreamHeaders(response.headers)
  });
}
__name(handleAnthropicChat, "handleAnthropicChat");
async function handleOpenAICompatibleChat(chatRequest, env, provider, requestId, startedAt) {
  const apiKey = provider === "openai" ? env.OPENAI_API_KEY : env.XAI_API_KEY;
  const providerName = provider === "openai" ? "OpenAI" : "xAI";
  if (!apiKey) {
    const secretName = provider === "openai" ? "OPENAI_API_KEY" : "XAI_API_KEY";
    console.error(`[${requestId}] /chat ${providerName} secret missing: ${secretName}`);
    return jsonResponse({ error: `${secretName} is not configured.` }, 500);
  }
  const upstreamUrl = provider === "openai" ? "https://api.openai.com/v1/chat/completions" : "https://api.x.ai/v1/chat/completions";
  const upstreamRequestBody = createOpenAICompatibleChatRequest(chatRequest);
  const response = await fetch(upstreamUrl, {
    method: "POST",
    headers: {
      authorization: `Bearer ${apiKey}`,
      "content-type": "application/json"
    },
    body: JSON.stringify(upstreamRequestBody)
  });
  if (!response.ok) {
    const errorBody = await response.text();
    console.error(`[${requestId}] /chat ${providerName} API error ${response.status}: ${trimForLog(errorBody)}`);
    return new Response(errorBody, {
      status: response.status,
      headers: { "content-type": "application/json" }
    });
  }
  console.log(
    `[${requestId}] /chat ${providerName} stream opened in ${Date.now() - startedAt}ms with status ${response.status}`
  );
  return new Response(normalizeOpenAICompatibleStream(response.body), {
    status: response.status,
    headers: createEventStreamHeaders(response.headers)
  });
}
__name(handleOpenAICompatibleChat, "handleOpenAICompatibleChat");
async function handleGeminiChat(chatRequest, env, requestId, startedAt) {
  if (!env.GEMINI_API_KEY) {
    console.error(`[${requestId}] /chat Gemini secret missing: GEMINI_API_KEY`);
    return jsonResponse({ error: "GEMINI_API_KEY is not configured." }, 500);
  }
  const upstreamUrl = `https://generativelanguage.googleapis.com/v1beta/${createGeminiModelPath(chatRequest.model)}:streamGenerateContent?alt=sse`;
  const upstreamRequestBody = createGeminiChatRequest(chatRequest);
  const response = await fetch(upstreamUrl, {
    method: "POST",
    headers: {
      "x-goog-api-key": env.GEMINI_API_KEY,
      "content-type": "application/json"
    },
    body: JSON.stringify(upstreamRequestBody)
  });
  if (!response.ok) {
    const errorBody = await response.text();
    console.error(`[${requestId}] /chat Gemini API error ${response.status}: ${trimForLog(errorBody)}`);
    return new Response(errorBody, {
      status: response.status,
      headers: { "content-type": "application/json" }
    });
  }
  console.log(
    `[${requestId}] /chat Gemini stream opened in ${Date.now() - startedAt}ms with status ${response.status}`
  );
  return new Response(normalizeGeminiStream(response.body), {
    status: response.status,
    headers: createEventStreamHeaders(response.headers)
  });
}
__name(handleGeminiChat, "handleGeminiChat");
function createOpenAICompatibleChatRequest(chatRequest) {
  return {
    model: chatRequest.model,
    max_tokens: chatRequest.max_tokens,
    stream: true,
    messages: [
      {
        role: "system",
        content: chatRequest.system
      },
      ...chatRequest.messages.map(convertMessageToOpenAICompatibleMessage)
    ]
  };
}
__name(createOpenAICompatibleChatRequest, "createOpenAICompatibleChatRequest");
function createGeminiChatRequest(chatRequest) {
  return {
    system_instruction: {
      parts: [
        {
          text: chatRequest.system
        }
      ]
    },
    contents: chatRequest.messages.map(convertMessageToGeminiContent).filter((content) => content.parts.length > 0),
    generationConfig: {
      maxOutputTokens: chatRequest.max_tokens
    }
  };
}
__name(createGeminiChatRequest, "createGeminiChatRequest");
function convertMessageToGeminiContent(chatMessage) {
  return {
    role: chatMessage.role === "assistant" ? "model" : "user",
    parts: typeof chatMessage.content === "string" ? [{ text: chatMessage.content }] : chatMessage.content.map(convertContentBlockToGeminiPart).filter((contentPart) => contentPart !== null)
  };
}
__name(convertMessageToGeminiContent, "convertMessageToGeminiContent");
function convertContentBlockToGeminiPart(contentBlock) {
  if (contentBlock.type === "text") {
    return {
      text: contentBlock.text ?? ""
    };
  }
  if (contentBlock.type === "image" && contentBlock.source) {
    return {
      inline_data: {
        mime_type: contentBlock.source.media_type,
        data: contentBlock.source.data
      }
    };
  }
  return null;
}
__name(convertContentBlockToGeminiPart, "convertContentBlockToGeminiPart");
function createGeminiModelPath(model) {
  const trimmedModel = model.trim();
  const modelName = trimmedModel.startsWith("models/") ? trimmedModel.slice("models/".length) : trimmedModel;
  return `models/${encodeURIComponent(modelName)}`;
}
__name(createGeminiModelPath, "createGeminiModelPath");
function convertMessageToOpenAICompatibleMessage(chatMessage) {
  if (typeof chatMessage.content === "string") {
    return {
      role: chatMessage.role,
      content: chatMessage.content
    };
  }
  return {
    role: chatMessage.role,
    content: chatMessage.content.map(convertContentBlockToOpenAICompatibleContentPart).filter((contentPart) => contentPart !== null)
  };
}
__name(convertMessageToOpenAICompatibleMessage, "convertMessageToOpenAICompatibleMessage");
function convertContentBlockToOpenAICompatibleContentPart(contentBlock) {
  if (contentBlock.type === "text") {
    return {
      type: "text",
      text: contentBlock.text ?? ""
    };
  }
  if (contentBlock.type === "image" && contentBlock.source) {
    return {
      type: "image_url",
      image_url: {
        url: `data:${contentBlock.source.media_type};base64,${contentBlock.source.data}`,
        detail: "high"
      }
    };
  }
  return null;
}
__name(convertContentBlockToOpenAICompatibleContentPart, "convertContentBlockToOpenAICompatibleContentPart");
function normalizeOpenAICompatibleStream(upstreamBody) {
  if (!upstreamBody) {
    return new ReadableStream({
      start(controller) {
        controller.close();
      }
    });
  }
  const textDecoder = new TextDecoder();
  const textEncoder = new TextEncoder();
  const reader = upstreamBody.getReader();
  let bufferedText = "";
  return new ReadableStream({
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
    }
  });
}
__name(normalizeOpenAICompatibleStream, "normalizeOpenAICompatibleStream");
function normalizeGeminiStream(upstreamBody) {
  if (!upstreamBody) {
    return new ReadableStream({
      start(controller) {
        controller.close();
      }
    });
  }
  const textDecoder = new TextDecoder();
  const textEncoder = new TextEncoder();
  const reader = upstreamBody.getReader();
  let bufferedText = "";
  return new ReadableStream({
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
    }
  });
}
__name(normalizeGeminiStream, "normalizeGeminiStream");
function convertGeminiLineToAnthropicEvent(line) {
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
          message: errorMessage
        }
      })}

`;
    }
    const textDelta = (parsedPayload?.candidates ?? []).flatMap((candidate) => candidate.content?.parts ?? []).map((part) => part.text ?? "").join("");
    if (textDelta.length === 0) {
      return null;
    }
    return `data: ${JSON.stringify({
      type: "content_block_delta",
      delta: {
        type: "text_delta",
        text: textDelta
      }
    })}

`;
  } catch (error) {
    console.error("[/chat] Could not parse Gemini stream event:", error);
    return null;
  }
}
__name(convertGeminiLineToAnthropicEvent, "convertGeminiLineToAnthropicEvent");
function convertOpenAICompatibleLineToAnthropicEvent(line) {
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
        text: textDelta
      }
    })}

`;
  } catch (error) {
    console.error("[/chat] Could not parse OpenAI-compatible stream event:", error);
    return null;
  }
}
__name(convertOpenAICompatibleLineToAnthropicEvent, "convertOpenAICompatibleLineToAnthropicEvent");
function summarizeChatRequest(chatRequest, provider) {
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
    `stream=${chatRequest.stream}`
  ].join("; ");
}
__name(summarizeChatRequest, "summarizeChatRequest");
function enqueueDoneEvent(controller, textEncoder) {
  controller.enqueue(textEncoder.encode("data: [DONE]\n\n"));
}
__name(enqueueDoneEvent, "enqueueDoneEvent");
async function handleTranscribeToken(env, requestId, startedAt) {
  console.log(`[${requestId}] /transcribe-token received`);
  const response = await fetch(
    "https://streaming.assemblyai.com/v3/token?expires_in_seconds=480",
    {
      method: "GET",
      headers: {
        authorization: env.ASSEMBLYAI_API_KEY
      }
    }
  );
  if (!response.ok) {
    const errorBody = await response.text();
    console.error(`[${requestId}] /transcribe-token AssemblyAI error ${response.status}: ${trimForLog(errorBody)}`);
    return new Response(errorBody, {
      status: response.status,
      headers: { "content-type": "application/json" }
    });
  }
  const data = await response.text();
  console.log(
    `[${requestId}] /transcribe-token completed in ${Date.now() - startedAt}ms with status ${response.status}`
  );
  return new Response(data, {
    status: 200,
    headers: { "content-type": "application/json" }
  });
}
__name(handleTranscribeToken, "handleTranscribeToken");
async function handleTTS(request, env, requestId, startedAt) {
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
        accept: "audio/mpeg"
      },
      body
    }
  );
  if (!response.ok) {
    const errorBody = await response.text();
    console.error(`[${requestId}] /tts ElevenLabs API error ${response.status}: ${trimForLog(errorBody)}`);
    return new Response(errorBody, {
      status: response.status,
      headers: { "content-type": "application/json" }
    });
  }
  console.log(
    `[${requestId}] /tts completed in ${Date.now() - startedAt}ms with status ${response.status}`
  );
  return new Response(response.body, {
    status: response.status,
    headers: {
      "content-type": response.headers.get("content-type") || "audio/mpeg"
    }
  });
}
__name(handleTTS, "handleTTS");
function normalizeChatProvider(provider) {
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
__name(normalizeChatProvider, "normalizeChatProvider");
function createEventStreamHeaders(upstreamHeaders) {
  return new Headers({
    "content-type": upstreamHeaders.get("content-type") || "text/event-stream",
    "cache-control": "no-cache"
  });
}
__name(createEventStreamHeaders, "createEventStreamHeaders");
function trimForLog(text, maximumLength = 1e3) {
  const trimmedText = text.trim();
  return trimmedText.length <= maximumLength ? trimmedText : `${trimmedText.slice(0, maximumLength)}...`;
}
__name(trimForLog, "trimForLog");
function jsonResponse(body, status) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json" }
  });
}
__name(jsonResponse, "jsonResponse");

// node_modules/wrangler/templates/middleware/middleware-ensure-req-body-drained.ts
var drainBody = /* @__PURE__ */ __name(async (request, env, _ctx, middlewareCtx) => {
  try {
    return await middlewareCtx.next(request, env);
  } finally {
    try {
      if (request.body !== null && !request.bodyUsed) {
        const reader = request.body.getReader();
        while (!(await reader.read()).done) {
        }
      }
    } catch (e) {
      console.error("Failed to drain the unused request body.", e);
    }
  }
}, "drainBody");
var middleware_ensure_req_body_drained_default = drainBody;

// node_modules/wrangler/templates/middleware/middleware-miniflare3-json-error.ts
function reduceError(e) {
  return {
    name: e?.name,
    message: e?.message ?? String(e),
    stack: e?.stack,
    cause: e?.cause === void 0 ? void 0 : reduceError(e.cause)
  };
}
__name(reduceError, "reduceError");
var jsonError = /* @__PURE__ */ __name(async (request, env, _ctx, middlewareCtx) => {
  try {
    return await middlewareCtx.next(request, env);
  } catch (e) {
    const error = reduceError(e);
    return Response.json(error, {
      status: 500,
      headers: { "MF-Experimental-Error-Stack": "true" }
    });
  }
}, "jsonError");
var middleware_miniflare3_json_error_default = jsonError;

// .wrangler/tmp/bundle-Vd7jLx/middleware-insertion-facade.js
var __INTERNAL_WRANGLER_MIDDLEWARE__ = [
  middleware_ensure_req_body_drained_default,
  middleware_miniflare3_json_error_default
];
var middleware_insertion_facade_default = src_default;

// node_modules/wrangler/templates/middleware/common.ts
var __facade_middleware__ = [];
function __facade_register__(...args) {
  __facade_middleware__.push(...args.flat());
}
__name(__facade_register__, "__facade_register__");
function __facade_invokeChain__(request, env, ctx, dispatch, middlewareChain) {
  const [head, ...tail] = middlewareChain;
  const middlewareCtx = {
    dispatch,
    next(newRequest, newEnv) {
      return __facade_invokeChain__(newRequest, newEnv, ctx, dispatch, tail);
    }
  };
  return head(request, env, ctx, middlewareCtx);
}
__name(__facade_invokeChain__, "__facade_invokeChain__");
function __facade_invoke__(request, env, ctx, dispatch, finalMiddleware) {
  return __facade_invokeChain__(request, env, ctx, dispatch, [
    ...__facade_middleware__,
    finalMiddleware
  ]);
}
__name(__facade_invoke__, "__facade_invoke__");

// .wrangler/tmp/bundle-Vd7jLx/middleware-loader.entry.ts
var __Facade_ScheduledController__ = class {
  constructor(scheduledTime, cron, noRetry) {
    this.scheduledTime = scheduledTime;
    this.cron = cron;
    this.#noRetry = noRetry;
  }
  #noRetry;
  noRetry() {
    if (!(this instanceof __Facade_ScheduledController__)) {
      throw new TypeError("Illegal invocation");
    }
    this.#noRetry();
  }
};
__name(__Facade_ScheduledController__, "__Facade_ScheduledController__");
function wrapExportedHandler(worker) {
  if (__INTERNAL_WRANGLER_MIDDLEWARE__ === void 0 || __INTERNAL_WRANGLER_MIDDLEWARE__.length === 0) {
    return worker;
  }
  for (const middleware of __INTERNAL_WRANGLER_MIDDLEWARE__) {
    __facade_register__(middleware);
  }
  const fetchDispatcher = /* @__PURE__ */ __name(function(request, env, ctx) {
    if (worker.fetch === void 0) {
      throw new Error("Handler does not export a fetch() function.");
    }
    return worker.fetch(request, env, ctx);
  }, "fetchDispatcher");
  return {
    ...worker,
    fetch(request, env, ctx) {
      const dispatcher = /* @__PURE__ */ __name(function(type, init) {
        if (type === "scheduled" && worker.scheduled !== void 0) {
          const controller = new __Facade_ScheduledController__(
            Date.now(),
            init.cron ?? "",
            () => {
            }
          );
          return worker.scheduled(controller, env, ctx);
        }
      }, "dispatcher");
      return __facade_invoke__(request, env, ctx, dispatcher, fetchDispatcher);
    }
  };
}
__name(wrapExportedHandler, "wrapExportedHandler");
function wrapWorkerEntrypoint(klass) {
  if (__INTERNAL_WRANGLER_MIDDLEWARE__ === void 0 || __INTERNAL_WRANGLER_MIDDLEWARE__.length === 0) {
    return klass;
  }
  for (const middleware of __INTERNAL_WRANGLER_MIDDLEWARE__) {
    __facade_register__(middleware);
  }
  return class extends klass {
    #fetchDispatcher = (request, env, ctx) => {
      this.env = env;
      this.ctx = ctx;
      if (super.fetch === void 0) {
        throw new Error("Entrypoint class does not define a fetch() function.");
      }
      return super.fetch(request);
    };
    #dispatcher = (type, init) => {
      if (type === "scheduled" && super.scheduled !== void 0) {
        const controller = new __Facade_ScheduledController__(
          Date.now(),
          init.cron ?? "",
          () => {
          }
        );
        return super.scheduled(controller);
      }
    };
    fetch(request) {
      return __facade_invoke__(
        request,
        this.env,
        this.ctx,
        this.#dispatcher,
        this.#fetchDispatcher
      );
    }
  };
}
__name(wrapWorkerEntrypoint, "wrapWorkerEntrypoint");
var WRAPPED_ENTRY;
if (typeof middleware_insertion_facade_default === "object") {
  WRAPPED_ENTRY = wrapExportedHandler(middleware_insertion_facade_default);
} else if (typeof middleware_insertion_facade_default === "function") {
  WRAPPED_ENTRY = wrapWorkerEntrypoint(middleware_insertion_facade_default);
}
var middleware_loader_entry_default = WRAPPED_ENTRY;
export {
  __INTERNAL_WRANGLER_MIDDLEWARE__,
  middleware_loader_entry_default as default
};
//# sourceMappingURL=index.js.map
