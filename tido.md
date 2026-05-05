Integrating the Computer Use API into your existing Buddy.Windows codebase without a full rewrite is absolutely possible. Instead of replacing your current PointingInstructionParser, you can build Computer Use as a separate "Action Mode" module that runs alongside your existing overlay system.

To do this, you need to transition from just drawing on the screen to actively controlling the Windows mouse and keyboard, and handling Google's structured JSON tool calls instead of parsing text.

Here is the exact architectural plan and the new files you need to create to make this work.

1. The Input Simulation Layer (New)
Currently, your app draws overlays. To use the Computer Use API, your app must simulate actual mouse clicks and keyboard presses. You will need to use Windows user32.dll (P/Invoke) or a library like InputSimulatorPlus.

File to Create: Buddy.Windows/Input/WindowsInputSimulator.cs

What it does: Wraps the native Windows API calls to move the cursor, trigger left/right clicks, and send keystrokes.

Key Methods:

MoveMouse(int x, int y)

LeftClick()

TypeText(string text)

PressKey(string keyCombination)

2. The Coordinate Mapper (New)
The Computer Use API does not know your screen resolution. It always returns coordinates on a normalized 1000x1000 grid (0 to 999). You must translate these to your actual Windows screen pixels.

File to Create: Buddy.Windows/ComputerUse/CoordinateMapper.cs

What it does: Converts Gemini's abstract coordinates to your local screen bounds.

Code Concept:

C#
public static class CoordinateMapper
{
    public static (int X, int Y) Denormalize(int geminiX, int geminiY, int screenWidth, int screenHeight)
    {
        int actualX = (int)((geminiX / 1000.0) * screenWidth);
        int actualY = (int)((geminiY / 1000.0) * screenHeight);
        return (actualX, actualY);
    }
}
3. The Action Handler (New)
When Gemini returns a FunctionCall, your app needs to parse the arguments and execute the correct input simulation.

File to Create: Buddy.Windows/ComputerUse/ComputerUseActionHandler.cs

What it does: Takes the FunctionCall object from the Gemini response, checks the name (e.g., "click_at", "type_text_at"), extracts the arguments, and calls your WindowsInputSimulator.

Key Logic: A large switch statement handling the predefined Google actions:

click_at: Extract x and y, map them using CoordinateMapper, and execute LeftClick().

type_text_at: Click at the coordinates, then execute TypeText().

scroll_at: Map coordinates, then send mouse wheel scroll events.

4. The Agent Loop Coordinator (New)
Unlike a standard chat where you ask a question and get an answer, Computer Use requires a continuous loop until the task is done.

File to Create: Buddy.Windows/ComputerUse/ComputerUseAgentCoordinator.cs

What it does: Manages the multi-turn state.

The Loop Logic:

Uses your existing WindowsScreenCaptureService to take a screenshot.

Sends the user's prompt + the screenshot to Gemini (with the ComputerUse tool enabled in the API configuration).

Receives the response. If the response contains a FunctionCall, it passes it to ComputerUseActionHandler.cs.

Waits a brief moment for the UI to react (e.g., a menu opening).

Takes a new screenshot.

Sends a FunctionResponse (acknowledging the action was taken) + the new screenshot back to Gemini.

Repeats until Gemini returns standard text indicating the task is complete.

5. Updates to Existing Files
You will need to modify your API client (likely ClaudeStreamingChatClient.cs or whichever client you use for Gemini) to support Tool Use.

Tool Configuration: You must inject the ComputerUse tool definition into the API request configuration.

History Management: You must update your conversation history manager to store FunctionCall requests from the assistant and FunctionResponse objects from the user role. If the history breaks, the agent loop will crash.

Summary of the Integration Path
By creating these four files inside a new Buddy.Windows/ComputerUse/ directory, you isolate the complex autonomous agent logic. Your existing overlay logic remains untouched and can be used when you just want the AI to "point" at things, while the new ComputerUseAgentCoordinator takes over when you ask the AI to "do" things.

Additional Current Gaps and Requirements

Per-monitor overlay strategy is still not done. Windows still uses one virtual-desktop overlay, so mixed-DPI setups remain riskier than macOS's one-window-per-screen model.

Follower cursor now updates at render-frame cadence, but it still hard-tracks the cursor position; there is no spring/lerp smoothing like macOS.

Scroll-context capture is still enabled for standard prompts, so the latency/temporary scroll concern remains unless disabled with BUDDY_ENABLE_SCROLL_CONTEXT=false.

AGENTS.md maintenance item: keep CompanionOverlayWindow.xaml.cs at approximately 725 lines, CompanionOverlayWindow.xaml at approximately 310 lines, and list the new Buddy.Windows/app.manifest file.

Agentic action mode should be enabled from a dedicated hotkey, such as Ctrl + Alt + A, so the screen-manipulating cursor flow is separate from the existing Ctrl + Alt + Space typed prompt and Ctrl + Alt + Esc shutdown shortcut.
