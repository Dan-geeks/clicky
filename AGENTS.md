# Buddy - Agent Instructions

<!-- This is the single source of truth for all AI coding agents. CLAUDE.md is a symlink to this file. -->
<!-- AGENTS.md spec: https://github.com/agentsmd/agents.md — supported by Claude Code, Cursor, Copilot, Gemini CLI, and others. -->

## Overview

macOS menu bar companion app. Lives entirely in the macOS status bar (no dock icon, no main window). Clicking the menu bar icon opens a custom floating panel with companion voice controls. Uses push-to-talk (ctrl+option) to capture voice input, transcribes it via AssemblyAI streaming, and sends the transcript + a screenshot of the user's screen to Claude. Claude responds with text (streamed via SSE) and voice (ElevenLabs TTS). A blue cursor overlay can fly to and point at UI elements Claude references on any connected monitor.

A Windows client is being ported separately under `Buddy.Windows/`. It is a .NET/WPF tray-only app shell that will grow feature-by-feature while the macOS app remains intact.

All API keys live on a Cloudflare Worker proxy — nothing sensitive ships in the app.

## Architecture

- **App Type**: Menu bar-only (`LSUIElement=true`), no dock icon or main window
- **Framework**: SwiftUI (macOS native) with AppKit bridging for menu bar panel and cursor overlay
- **Pattern**: MVVM with `@StateObject` / `@Published` state management
- **AI Chat**: Claude (Sonnet 4.6 default, Opus 4.6 optional) via Cloudflare Worker proxy with SSE streaming
- **Speech-to-Text**: AssemblyAI real-time streaming (`u3-rt-pro` model) via websocket, with OpenAI and Apple Speech as fallbacks
- **Text-to-Speech**: ElevenLabs (`eleven_flash_v2_5` model) via Cloudflare Worker proxy
- **Screen Capture**: ScreenCaptureKit (macOS 14.2+), multi-monitor support
- **Voice Input**: Push-to-talk via `AVAudioEngine` + pluggable transcription-provider layer. System-wide keyboard shortcut via listen-only CGEvent tap.
- **Element Pointing**: Claude embeds `[POINT:x,y:label:screenN]` tags in responses. The overlay parses these, maps coordinates to the correct monitor, and animates the blue cursor along a bezier arc to the target.
- **Concurrency**: `@MainActor` isolation, async/await throughout
- **Analytics**: PostHog via `ClickyAnalytics.swift`

### Windows Client

- **App Type**: Windows tray-only app, no normal main window
- **Framework**: .NET 8 WPF with Windows Forms `NotifyIcon` for tray integration and NAudio for microphone capture/audio playback
- **Current Scope**: Milestone 12 text-first shell + Milestone 13 Computer Use action mode — tray icon, custom floating panel, quit action, `Ctrl + Alt + Esc` shutdown shortcut, transparent click-through companion overlay, `Ctrl + Alt + Space` typed prompt near the cursor, `Ctrl + Alt + A` Computer Use agent that actually clicks/types/scrolls via SendInput, `Ctrl + Alt + M` Ask Buddy model cycling, screen capture for all connected Windows displays plus temporary scroll-context capture near the cursor, Anthropic/OpenAI/Grok/Gemini vision SSE responses through the Worker `/chat` route, point-tag parsing/stripping, animated overlay pointing with Bezier flight + tangent rotation + scale/glow pulse + animated return-to-cursor, and a follower cursor that lerps toward the mouse instead of hard-tracking it. Voice classes remain in the codebase but are off by default while the Windows client focuses on typed prompts.
- **Configuration**: Set `BUDDY_WORKER_BASE_URL` to the deployed Cloudflare Worker origin before testing typed prompts, for example `https://your-worker.workers.dev`. Gemini is the default Windows chat provider and defaults to `gemini-3.1-flash-lite-preview`. Optional Windows chat provider env vars: `BUDDY_AI_PROVIDER=anthropic|openai|grok|gemini` and `BUDDY_AI_MODEL=<provider-model>`. The runtime Ask Buddy selector starts from those env vars and `Ctrl + Alt + M` cycles the visible Ask model across `gemini-3.1-flash-lite-preview`, `gemini-2.5-flash`, and `gemini-flash-lite-latest` for side-by-side testing. Optional fast follow-up env vars for guided cursor verification: `BUDDY_FAST_AI_PROVIDER=anthropic|openai|grok|gemini` and `BUDDY_FAST_AI_MODEL=<provider-model>`. Optional scroll-context env var: `BUDDY_ENABLE_SCROLL_CONTEXT=false` disables the default temporary down-scroll capture. Optional Computer Use env vars: `BUDDY_COMPUTER_USE_MODEL=<gemini-model>` overrides the default `gemini-2.5-computer-use-preview-10-2025` and `BUDDY_COMPUTER_USE_MAX_TURNS=<int>` (1..64) raises/lowers the safety cap on agent loop turns (default 16). Optional experimental voice env var: `BUDDY_ENABLE_VOICE=true`; only then are AssemblyAI and ElevenLabs secrets required for the Windows client.
- **Screen Capture**: Uses Windows Forms display enumeration and `System.Drawing` to capture each connected display as a JPEG immediately after the user submits a prompt or finishes speaking. Standard prompts also send a cursor-screen context-only capture after Buddy temporarily scrolls down and restores the original direction. Captures are sent only with the current Claude request; conversation history remains text-only.
- **Overlay**: Uses a transparent, topmost, click-through WPF window that spans the Windows virtual desktop. `CompanionOverlayController` subscribes to the existing shortcut, microphone, transcription, Claude, and TTS services, shows transient listening/thinking/speaking UI near the current cursor, and animates parsed point targets onto the correct Windows display.
- **Pointing**: Claude may append `[POINT:x,y:label:screenN]` tags. The Windows client strips those tags from UI/TTS/history text, parses the latest instruction, maps screenshot-relative pixels to Windows virtual-screen coordinates, and flies a blue cursor marker with a small label to the target.
- **Text Input**: `Ctrl + Alt + Space` opens a focusable typed prompt near the current cursor. Submitted text uses the screen-capture, chat, and pointing response path. Bare Space is intentionally not captured because it would interfere with normal typing in other apps. `Ctrl + Alt + M` cycles the Ask Buddy chat model and the prompt/panel show the currently selected model. `Ctrl + Alt + Esc` shuts down the Windows tray app through the same WPF cleanup path as the tray Quit action. `Ctrl + Alt + A` opens the same prompt window in "act on my desktop" mode and routes the submitted text into the Computer Use agent run.
- **Computer Use Action Mode**: `ComputerUseAgentCoordinator` runs a multi-turn loop against the Worker `/computer-use` route (Gemini Computer Use). Each turn captures the connected displays, sends them with the running tool history, executes the returned FunctionCall (`click_at`, `type_text_at`, `key_combination`, `scroll_at`, `drag_and_drop`, `wait_for`, …) through `WindowsInputSimulator` (SendInput-based mouse + keyboard), and replies with a `function_response` plus a fresh screenshot. The Worker uses Google's accepted `ENVIRONMENT_BROWSER` Computer Use enum while Buddy maps returned coordinates/actions onto the Windows desktop. `CoordinateMapper` translates Gemini's normalized 0..999 coordinates back to virtual-desktop pixels using the screenshot's actual screen bounds. Existing overlay/pointing flow is untouched — action mode runs in parallel for "do it" requests while the standard prompt remains for "point at it" requests. The Computer Use preview model may require a billing-enabled key/quota even when normal Gemini chat models work.
- **AI Providers**: The Windows client sends a provider hint and model to `/chat`. The Worker calls Anthropic directly, converts the same screen-aware request to OpenAI-compatible Chat Completions for OpenAI and xAI Grok, or converts it to Gemini `streamGenerateContent`, then normalizes streamed text back into the Anthropic-style SSE events consumed by the app.
- **Next Milestones**: packaging, installer/autostart polish, persisted settings UI for shortcuts, Worker URL, provider/model defaults, and optional voice re-enable

### API Proxy (Cloudflare Worker)

The app never calls external APIs directly. All requests go through a Cloudflare Worker (`worker/src/index.ts`) that holds the real API keys as secrets.

| Route | Upstream | Purpose |
|-------|----------|---------|
| `POST /chat` | Anthropic Messages / OpenAI Chat Completions / xAI Chat Completions / Gemini streamGenerateContent | Vision + streaming chat |
| `POST /computer-use` | Gemini `generateContent` with the `computer_use` tool | Non-streaming Computer Use action turn (returns text or FunctionCalls) |
| `POST /tts` | `api.elevenlabs.io/v1/text-to-speech/{voiceId}` | ElevenLabs TTS audio |
| `POST /transcribe-token` | `streaming.assemblyai.com/v3/token` | Fetches a short-lived (480s) AssemblyAI websocket token |

Worker secrets: `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `XAI_API_KEY`, `GEMINI_API_KEY`, `ASSEMBLYAI_API_KEY`, `ELEVENLABS_API_KEY`
Worker vars: `ELEVENLABS_VOICE_ID`

### Key Architecture Decisions

**Menu Bar Panel Pattern**: The companion panel uses `NSStatusItem` for the menu bar icon and a custom borderless `NSPanel` for the floating control panel. This gives full control over appearance (dark, rounded corners, custom shadow) and avoids the standard macOS menu/popover chrome. The panel is non-activating so it doesn't steal focus. A global event monitor auto-dismisses it on outside clicks.

**Cursor Overlay**: A full-screen transparent `NSPanel` hosts the blue cursor companion. It's non-activating, joins all Spaces, and never steals focus. The cursor position, response text, waveform, and pointing animations all render in this overlay via SwiftUI through `NSHostingView`.

**Global Push-To-Talk Shortcut**: Background push-to-talk uses a listen-only `CGEvent` tap instead of an AppKit global monitor so modifier-based shortcuts like `ctrl + option` are detected more reliably while the app is running in the background.

**Shared URLSession for AssemblyAI**: A single long-lived `URLSession` is shared across all AssemblyAI streaming sessions (owned by the provider, not the session). Creating and invalidating a URLSession per session corrupts the OS connection pool and causes "Socket is not connected" errors after a few rapid reconnections.

**Transient Cursor Mode**: When "Show Clicky" is off, pressing the hotkey fades in the cursor overlay for the duration of the interaction (recording → response → TTS → optional pointing), then fades it out automatically after 1 second of inactivity.

## Key Files

| File | Lines | Purpose |
|------|-------|---------|
| `leanring_buddyApp.swift` | ~89 | Menu bar app entry point. Uses `@NSApplicationDelegateAdaptor` with `CompanionAppDelegate` which creates `MenuBarPanelManager` and starts `CompanionManager`. No main window — the app lives entirely in the status bar. |
| `CompanionManager.swift` | ~1026 | Central state machine. Owns dictation, shortcut monitoring, screen capture, Claude API, ElevenLabs TTS, and overlay management. Tracks voice state (idle/listening/processing/responding), conversation history, model selection, and cursor visibility. Coordinates the full push-to-talk → screenshot → Claude → TTS → pointing pipeline. |
| `MenuBarPanelManager.swift` | ~243 | NSStatusItem + custom NSPanel lifecycle. Creates the menu bar icon, manages the floating companion panel (show/hide/position), installs click-outside-to-dismiss monitor. |
| `CompanionPanelView.swift` | ~761 | SwiftUI panel content for the menu bar dropdown. Shows companion status, push-to-talk instructions, model picker (Sonnet/Opus), permissions UI, DM feedback button, and quit button. Dark aesthetic using `DS` design system. |
| `OverlayWindow.swift` | ~881 | Full-screen transparent overlay hosting the blue cursor, response text, waveform, and spinner. Handles cursor animation, element pointing with bezier arcs, multi-monitor coordinate mapping, and fade-out transitions. |
| `CompanionResponseOverlay.swift` | ~217 | SwiftUI view for the response text bubble and waveform displayed next to the cursor in the overlay. |
| `CompanionScreenCaptureUtility.swift` | ~132 | Multi-monitor screenshot capture using ScreenCaptureKit. Returns labeled image data for each connected display. |
| `BuddyDictationManager.swift` | ~866 | Push-to-talk voice pipeline. Handles microphone capture via `AVAudioEngine`, provider-aware permission checks, keyboard/button dictation sessions, transcript finalization, shortcut parsing, contextual keyterms, and live audio-level reporting for waveform feedback. |
| `BuddyTranscriptionProvider.swift` | ~100 | Protocol surface and provider factory for voice transcription backends. Resolves provider based on `VoiceTranscriptionProvider` in Info.plist — AssemblyAI, OpenAI, or Apple Speech. |
| `AssemblyAIStreamingTranscriptionProvider.swift` | ~478 | Streaming transcription provider. Fetches temp tokens from the Cloudflare Worker, opens an AssemblyAI v3 websocket, streams PCM16 audio, tracks turn-based transcripts, and delivers finalized text on key-up. Shares a single URLSession across all sessions. |
| `OpenAIAudioTranscriptionProvider.swift` | ~317 | Upload-based transcription provider. Buffers push-to-talk audio locally, uploads as WAV on release, returns finalized transcript. |
| `AppleSpeechTranscriptionProvider.swift` | ~147 | Local fallback transcription provider backed by Apple's Speech framework. |
| `BuddyAudioConversionSupport.swift` | ~108 | Audio conversion helpers. Converts live mic buffers to PCM16 mono audio and builds WAV payloads for upload-based providers. |
| `GlobalPushToTalkShortcutMonitor.swift` | ~132 | System-wide push-to-talk monitor. Owns the listen-only `CGEvent` tap and publishes press/release transitions. |
| `ClaudeAPI.swift` | ~291 | Claude vision API client with streaming (SSE) and non-streaming modes. TLS warmup optimization, image MIME detection, conversation history support. |
| `OpenAIAPI.swift` | ~142 | OpenAI GPT vision API client. |
| `ElevenLabsTTSClient.swift` | ~81 | ElevenLabs TTS client. Sends text to the Worker proxy, plays back audio via `AVAudioPlayer`. Exposes `isPlaying` for transient cursor scheduling. |
| `ElementLocationDetector.swift` | ~335 | Detects UI element locations in screenshots for cursor pointing. |
| `DesignSystem.swift` | ~880 | Design system tokens — colors, corner radii, shared styles. All UI references `DS.Colors`, `DS.CornerRadius`, etc. |
| `ClickyAnalytics.swift` | ~121 | PostHog analytics integration for usage tracking. |
| `WindowPositionManager.swift` | ~262 | Window placement logic, Screen Recording permission flow, and accessibility permission helpers. |
| `AppBundleConfiguration.swift` | ~28 | Runtime configuration reader for keys stored in the app bundle Info.plist. |
| `worker/src/index.ts` | ~919 | Cloudflare Worker proxy. Four routes: `/chat` (Anthropic/OpenAI/Grok/Gemini vision chat with normalized streaming), `/computer-use` (non-streaming Gemini Computer Use using Google's accepted `ENVIRONMENT_BROWSER` enum and returning structured FunctionCalls + final text), `/tts` (ElevenLabs), `/transcribe-token` (AssemblyAI temp token). |
| `worker/test-gemini-worker.cmd` | ~30 | Windows CMD curl smoke test for the Worker `/chat` Gemini provider path using `gemini-3.1-flash-lite-preview` and `gemini-2.5-flash`. |
| `Buddy.Windows/App.xaml.cs` | ~188 | Windows WPF app startup. Enforces a single running instance, creates the tray icon manager, global shortcut monitor, optional voice services, screen capture service, Claude response services, companion overlay controller, typed prompt controller, runtime model selector hotkey, and Computer Use action mode (Gemini client + agent coordinator + action prompt controller) while keeping the app alive without a normal main window. Voice coordination is only created when `BUDDY_ENABLE_VOICE=true`. |
| `Buddy.Windows/app.manifest` | ~14 | Windows application manifest declaring supported OS compatibility metadata for the tray app. PerMonitorV2 DPI awareness is configured via the `ApplicationHighDpiMode` MSBuild property in `Buddy.Windows.csproj`. |
| `Buddy.Windows/Configuration/ClickyWindowsConfiguration.cs` | ~185 | Windows runtime configuration helpers. Reads `BUDDY_WORKER_BASE_URL`, optional primary/fast chat provider/model env vars, optional scroll-context and voice flags, optional Computer Use model + max-turns env vars, and builds Worker endpoint URLs. Checks Process, User, then Machine environment scopes. |
| `Buddy.Windows/Configuration/BuddyRuntimeModelSelection.cs` | ~224 | Runtime model selector for the Windows client. Starts from env defaults, exposes the active Ask Buddy model, cycles the three Gemini chat test models, and exposes the active Computer Use model/display text for UI and request clients. |
| `Buddy.Windows/Diagnostics/BuddyLog.cs` | ~79 | Small local diagnostics logger. Writes startup, workflow breadcrumbs, Worker/API request failures, streaming errors, screen-capture errors, and TTS/transcription failures to `%LOCALAPPDATA%\Buddy.Windows\Logs\buddy.log`. |
| `Buddy.Windows/Overlay/CompanionOverlayPresentationState.cs` | ~35 | Immutable presentation payload for the Windows companion overlay status, transcript, response text, audio-level display, copy/paste layout mode, and thinking animation state. |
| `Buddy.Windows/Overlay/CompanionOverlayWindow.xaml` | ~317 | Transparent WPF overlay UI. Renders the blue cursor glyph, pointing cursor label (with named transforms + per-element glow effect for the bezier flight animation), status text, audio level, transcript snippet, and response snippet without taking normal input focus. |
| `Buddy.Windows/Overlay/CompanionOverlayWindow.xaml.cs` | ~879 | Sizes the Windows companion overlay to the virtual desktop, applies click-through/no-activate Win32 styles, positions companion UI near the current cursor, maps point instructions to display coordinates, drives a `CompositionTarget.Rendering` 60 fps frame loop that animates the pointing cursor along a quadratic Bezier with smoothstep easing/tangent rotation/scale + glow pulse, lerps the follower cursor toward the live mouse, supports an animated return-to-cursor flight, and runs the thinking dots wave. |
| `Buddy.Windows/Overlay/CompanionOverlayController.cs` | ~1075 | Subscribes to Windows shortcut, microphone, transcription, Claude, and TTS state changes, drives the transient companion overlay lifecycle, forwards parsed pointing instructions to the overlay window, advances guided pointing after the user acts on the target, surfaces API/service errors in the overlay, preserves guided-task context for fast follow-up models, and keeps copy/paste-style responses visible until paste or explicit dismiss. |
| `Buddy.Windows/Pointing/PointingInstruction.cs` | ~24 | Immutable Windows point target with screenshot-relative coordinates, label, and screen number parsed from Claude point tags. |
| `Buddy.Windows/Pointing/PointingInstructionParseResult.cs` | ~18 | Result object containing cleaned response text and parsed point instructions. |
| `Buddy.Windows/Pointing/PointingInstructionParser.cs` | ~152 | Parses `[POINT:x,y:label:screenN]` tags (the `:screenN` suffix is optional and resolves to the cursor's current screen when omitted, mirroring macOS), strips complete and trailing partial tags from streamed response text, and normalizes visible response text. |
| `Buddy.Windows/Screen/WindowsScreenCapture.cs` | ~40 | Immutable Windows screen capture payload with JPEG bytes, Claude-facing label, pixel dimensions, display origin, and primary-display metadata. |
| `Buddy.Windows/Screen/WindowsScreenCaptureService.cs` | ~267 | Captures all connected Windows displays as JPEG images using Windows Forms screen enumeration and `System.Drawing` screen copy APIs. Also performs optional temporary mouse-wheel scroll context capture near the cursor and labels those captures as context-only so point coordinates stay tied to live viewports. |
| `Buddy.Windows/TextInput/TextPromptController.cs` | ~99 | Opens the typed prompt on `Ctrl + Alt + Space`, interrupts active work, and submits typed prompts through the existing Claude response pipeline. |
| `Buddy.Windows/TextInput/TextPromptWindow.xaml` | ~92 | Focusable WPF text prompt shown near the cursor, with current model text, typed input, Send/Cancel controls, Enter-to-send, Shift+Enter newline, and Escape-to-close behavior. |
| `Buddy.Windows/TextInput/TextPromptWindow.xaml.cs` | ~190 | Positions the typed prompt near the current cursor, handles keyboard/button submission, switches between Ask Buddy / Act on my desktop modes, updates the visible model label, and publishes submitted prompt text. |
| `Buddy.Windows/TextInput/TextPromptSubmittedEventArgs.cs` | ~13 | Event payload for typed prompt submission. |
| `Buddy.Windows/TextInput/TextPromptMode.cs` | ~16 | Enum picking which input flow the typed prompt drives — `AskBuddy` (Ctrl+Alt+Space) or `ActOnDesktop` (Ctrl+Alt+A). |
| `Buddy.Windows/Input/WindowsInputSimulator.cs` | ~508 | Win32 SendInput wrapper used by Computer Use action mode. Handles absolute mouse moves across the virtual desktop, left/right/double click, drag with interpolated steps, vertical and horizontal scrolling, Unicode text typing, and keyboard chord parsing for tokens like `ctrl+shift+t` / `enter` / `f5`. |
| `Buddy.Windows/ComputerUse/CoordinateMapper.cs` | ~57 | Converts Gemini Computer Use's normalized 0..999 coordinates into Windows virtual-desktop pixels using either explicit screen bounds or a 1-based screen number (with cursor-screen fallback). |
| `Buddy.Windows/ComputerUse/ComputerUseActionHandler.cs` | ~390 | Switches on a Gemini Computer Use FunctionCall name (`click_at`, `right_click_at`, `double_click_at`, `hover_at`, `type_text_at`, `type_text`, `key_combination`, `scroll_at`, `drag_and_drop`, `wait_for`, `open_app`) and dispatches to the input simulator with coordinates resolved against the screenshot the model actually saw. |
| `Buddy.Windows/ComputerUse/ComputerUseAgentCoordinator.cs` | ~338 | Multi-turn agent loop. Captures all displays, asks Gemini for the next action, executes it, captures again, and feeds the result back as a `function_response` until Gemini returns no more FunctionCalls or the safety turn cap is hit. Publishes `Capturing/Thinking/Acting/Completed/Cancelled/Failed` state for UI surfaces. |
| `Buddy.Windows/ComputerUse/ComputerUseActionPromptController.cs` | ~114 | Owns the `Ctrl + Alt + A` flow: opens the typed prompt window in `ActOnDesktop` mode, stops any active voice/AI/TTS work, and hands the submitted text to `ComputerUseAgentCoordinator`. |
| `Buddy.Windows/AI/GeminiComputerUseClient.cs` | ~290 | Posts to the Worker `/computer-use` route with the system instruction, accumulated tool history, and screenshots; deserializes the normalized envelope (text + function_calls + is_complete) the Worker returns from Gemini's `generateContent`. |
| `Buddy.Windows/AI/AssemblyAITemporaryTokenClient.cs` | ~74 | Fetches short-lived AssemblyAI streaming tokens from the Cloudflare Worker `/transcribe-token` route. |
| `Buddy.Windows/AI/ClaudeStreamingChatClient.cs` | ~369 | Streams AI responses from the Worker `/chat` route using normalized Anthropic-style SSE events, recent text conversation history, current-turn Windows screen captures as Anthropic image content blocks, provider/model hints, live-vs-scroll-context guidance, and point-tag prompting. |
| `Buddy.Windows/AI/ClaudeResponseService.cs` | ~411 | Manages Windows screen capture, optional scroll-context capture, point-tag parsing, Claude response state, cancellation, recent conversation history, and streamed response updates for the Windows client. |
| `Buddy.Windows/AI/ClaudeResponseStateChangedEventArgs.cs` | ~44 | Event payload for Windows screen capture status, Claude response status, user transcript, stripped streamed response text, parsed point instructions, and errors. |
| `Buddy.Windows/AI/ClaudeConversationExchange.cs` | ~14 | Stores a user transcript and assistant response pair for Windows Claude conversation history. |
| `Buddy.Windows/AI/ElevenLabsTextToSpeechClient.cs` | ~124 | Fetches ElevenLabs MP3 audio from the Cloudflare Worker `/tts` route using the Windows response text and shared voice settings. |
| `Buddy.Windows/AI/ElevenLabsTextToSpeechPlaybackService.cs` | ~309 | Coordinates TTS fetch cancellation and local MP3 playback through NAudio `Mp3FileReader` and `WaveOutEvent`. |
| `Buddy.Windows/AI/TextToSpeechPlaybackStateChangedEventArgs.cs` | ~26 | Event payload for Windows TTS fetch/playback state, spoken text, and errors. |
| `Buddy.Windows/Tray/TrayIconManager.cs` | ~350 | Windows tray icon lifecycle. Handles left-click panel toggling, right-click context menu, model cycling, panel positioning, shortcut/capture/transcription/screen-capture/AI/TTS/Computer-Use-state tray text, and cleanup. |
| `Buddy.Windows/Tray/FloatingPanelWindow.xaml` | ~220 | Borderless, topmost WPF floating panel used by the Windows tray shell. Shows current Ask/Act models, typed-input shortcuts, microphone capture, live audio level, transcript text, Claude response text, and Computer Use state. |
| `Buddy.Windows/Tray/FloatingPanelWindow.xaml.cs` | ~693 | Applies no-activate/tool-window Win32 styles to the Windows floating panel, reflects push-to-talk/microphone/transcription/screen-capture/AI/TTS/Computer-Use/model-selection state changes, and handles the quit button. |
| `Buddy.Windows/Voice/PushToTalkHotkeyMonitor.cs` | ~456 | Windows low-level keyboard hook for `Ctrl + Alt + Space` typed prompt detection, `Ctrl + Alt + Esc` shutdown, `Ctrl + Alt + A` Computer Use action-mode trigger, `Ctrl + Alt + M` Ask model cycling, and optional `Ctrl + Alt` push-to-talk press/release detection when voice mode is enabled. |
| `Buddy.Windows/Voice/PushToTalkHotkeyChangedEventArgs.cs` | ~22 | Event payload for Windows push-to-talk state, monitoring availability, and hook errors. |
| `Buddy.Windows/Voice/AssemblyAIStreamingTranscriptionService.cs` | ~723 | AssemblyAI v3 WebSocket transcription service. Opens token-authenticated `u3-rt-pro` sessions, sends PCM16 audio chunks, handles `Begin`/`Turn`/`Termination`/`Error` messages, and publishes live/final transcripts. |
| `Buddy.Windows/Voice/StreamingTranscriptionStateChangedEventArgs.cs` | ~30 | Event payload for Windows streaming transcription connection, live transcript, final transcript, and errors. |
| `Buddy.Windows/Voice/MicrophoneCaptureService.cs` | ~279 | NAudio-backed microphone capture service. Captures default mic input as 16 kHz PCM16 mono, publishes live audio chunks, audio levels, duration, byte counts, and capture errors. |
| `Buddy.Windows/Voice/MicrophoneCaptureStateChangedEventArgs.cs` | ~42 | Event payload for Windows microphone capture state, format details, live level, byte count, duration, and errors. |
| `Buddy.Windows/Voice/MicrophoneAudioCapturedEventArgs.cs` | ~26 | Event payload for captured PCM16 microphone audio chunks consumed by AssemblyAI streaming transcription. |
| `Buddy.Windows/Voice/PushToTalkVoiceCaptureCoordinator.cs` | ~225 | Connects global push-to-talk press/release events to AssemblyAI session start/finalization, microphone capture start/stop, Claude response submission, and TTS playback interruption through a serialized background transition queue. Includes a short voice-start delay so the typed prompt chord can suppress voice capture cleanly. |

## Build & Run

```bash
# Open in Xcode
open leanring-buddy.xcodeproj

# Select the leanring-buddy scheme, set signing team, Cmd+R to build and run

# Known non-blocking warnings: Swift 6 concurrency warnings,
# deprecated onChange warning in OverlayWindow.swift. Do NOT attempt to fix these.
```

**Do NOT run `xcodebuild` from the terminal** — it invalidates TCC (Transparency, Consent, and Control) permissions and the app will need to re-request screen recording, accessibility, etc.

### Windows Client

```powershell
# Requires the .NET 8 Desktop SDK
cd Buddy.Windows
$env:BUDDY_WORKER_BASE_URL="https://your-worker.workers.dev"
$env:BUDDY_AI_PROVIDER="gemini" # anthropic, openai, grok, or gemini
# Optional: override provider default model
# $env:BUDDY_AI_MODEL="gemini-3.1-flash-lite-preview"
# Optional: use a cheaper/faster model for guided follow-up verification
# $env:BUDDY_FAST_AI_PROVIDER="openai"
# $env:BUDDY_FAST_AI_MODEL="gpt-4o-mini"
# Optional: disable temporary down-scroll context screenshots
# $env:BUDDY_ENABLE_SCROLL_CONTEXT="false"
# Optional: enable experimental voice mode; off by default for text-only use
# $env:BUDDY_ENABLE_VOICE="true"
# Optional: override Computer Use model and safety turn cap
# $env:BUDDY_COMPUTER_USE_MODEL="gemini-2.5-computer-use-preview-10-2025"
# $env:BUDDY_COMPUTER_USE_MAX_TURNS="16"
dotnet run --project Buddy.Windows.csproj
```

After launch:
- `Ctrl + Alt + Space` opens the typed prompt for "ask Buddy" (chat + pointing).
- `Ctrl + Alt + A` opens the typed prompt for "act on my desktop" (Computer Use agent that actually clicks/types/scrolls).
- `Ctrl + Alt + Esc` quits the Windows tray app.

## Cloudflare Worker

```bash
cd worker
npm install

# Add secrets
npx wrangler secret put ANTHROPIC_API_KEY
npx wrangler secret put OPENAI_API_KEY
npx wrangler secret put XAI_API_KEY
npx wrangler secret put GEMINI_API_KEY
npx wrangler secret put ASSEMBLYAI_API_KEY
npx wrangler secret put ELEVENLABS_API_KEY

# Deploy
npx wrangler deploy

# Local dev (create worker/.dev.vars with your keys)
npx wrangler dev
```

The deployed Windows Worker is `clicky-proxy` and `worker/wrangler.toml` should keep `name = "clicky-proxy"` so secret and deploy commands target the same Worker used by `BUDDY_WORKER_BASE_URL`.

## Code Style & Conventions

### Variable and Method Naming

IMPORTANT: Follow these naming rules strictly. Clarity is the top priority.

- Be as clear and specific with variable and method names as possible
- **Optimize for clarity over concision.** A developer with zero context on the codebase should immediately understand what a variable or method does just from reading its name
- Use longer names when it improves clarity. Do NOT use single-character variable names
- Example: use `originalQuestionLastAnsweredDate` instead of `originalAnswered`
- When passing props or arguments to functions, keep the same names as the original variable. Do not shorten or abbreviate parameter names. If you have `currentCardData`, pass it as `currentCardData`, not `card` or `cardData`

### Code Clarity

- **Clear is better than clever.** Do not write functionality in fewer lines if it makes the code harder to understand
- Write more lines of code if additional lines improve readability and comprehension
- Make things so clear that someone with zero context would completely understand the variable names, method names, what things do, and why they exist
- When a variable or method name alone cannot fully explain something, add a comment explaining what is happening and why

### Swift/SwiftUI Conventions

- Use SwiftUI for all UI unless a feature is only supported in AppKit (e.g., `NSPanel` for floating windows)
- All UI state updates must be on `@MainActor`
- Use async/await for all asynchronous operations
- Comments should explain "why" not just "what", especially for non-obvious AppKit bridging
- AppKit `NSPanel`/`NSWindow` bridged into SwiftUI via `NSHostingView`
- All buttons must show a pointer cursor on hover
- For any interactive element, explicitly think through its hover behavior (cursor, visual feedback, and whether hover should communicate clickability)

### Do NOT

- Do not add features, refactor code, or make "improvements" beyond what was asked
- Do not add docstrings, comments, or type annotations to code you did not change
- Do not try to fix the known non-blocking warnings (Swift 6 concurrency, deprecated onChange)
- Do not rename the project directory or scheme (the "leanring" typo is intentional/legacy)
- Do not run `xcodebuild` from the terminal — it invalidates TCC permissions

## Git Workflow

- Branch naming: `feature/description` or `fix/description`
- Commit messages: imperative mood, concise, explain the "why" not the "what"
- Do not force-push to main

## Self-Update Instructions

<!-- AI agents: follow these instructions to keep this file accurate. -->

When you make changes to this project that affect the information in this file, update this file to reflect those changes. Specifically:

1. **New files**: Add new source files to the "Key Files" table with their purpose and approximate line count
2. **Deleted files**: Remove entries for files that no longer exist
3. **Architecture changes**: Update the architecture section if you introduce new patterns, frameworks, or significant structural changes
4. **Build changes**: Update build commands if the build process changes
5. **New conventions**: If the user establishes a new coding convention during a session, add it to the appropriate conventions section
6. **Line count drift**: If a file's line count changes significantly (>50 lines), update the approximate count in the Key Files table

Do NOT update this file for minor edits, bug fixes, or changes that don't affect the documented architecture or conventions.
