using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using Buddy.Windows.AI;
using Buddy.Windows.Diagnostics;
using Buddy.Windows.Input;
using Buddy.Windows.Screen;

namespace Buddy.Windows.ComputerUse;

/// <summary>
/// Translates a single Gemini Computer Use FunctionCall into actual Windows input. Each
/// predefined Gemini action (click_at, type_text_at, scroll_at, key_combination, drag,
/// wait_for, …) is mapped to the corresponding <see cref="WindowsInputSimulator"/> call,
/// with normalized 0..999 coordinates resolved through <see cref="CoordinateMapper"/>.
/// </summary>
public sealed class ComputerUseActionHandler
{
    private readonly IReadOnlyList<WindowsScreenCapture> screenCapturesForCurrentTurn;

    public ComputerUseActionHandler(IReadOnlyList<WindowsScreenCapture> screenCapturesForCurrentTurn)
    {
        this.screenCapturesForCurrentTurn = screenCapturesForCurrentTurn;
    }

    /// <summary>
    /// Executes the function call. Returns a short human-readable description of what was
    /// done so it can be sent back to Gemini as the function response and shown in logs.
    /// </summary>
    public async Task<string> ExecuteAsync(ComputerUseFunctionCall functionCallToExecute)
    {
        string normalizedFunctionName = functionCallToExecute.Name.Trim().ToLowerInvariant();
        BuddyLog.Workflow(
            $"Computer Use executing function call: {normalizedFunctionName} args={SerializeArgsForLog(functionCallToExecute.Args)}.");

        try
        {
            switch (normalizedFunctionName)
            {
                case "click_at":
                case "left_click_at":
                    return await ExecuteClickAtAsync(functionCallToExecute, isRightClick: false);
                case "right_click_at":
                    return await ExecuteClickAtAsync(functionCallToExecute, isRightClick: true);
                case "double_click_at":
                    return await ExecuteDoubleClickAtAsync(functionCallToExecute);
                case "hover_at":
                case "move_mouse":
                case "move_mouse_to":
                    return ExecuteHoverAt(functionCallToExecute);
                case "type_text_at":
                    return await ExecuteTypeTextAtAsync(functionCallToExecute);
                case "type_text":
                    return await ExecuteTypeTextAsync(functionCallToExecute);
                case "key_combination":
                case "press_key":
                case "press_keys":
                    return await ExecuteKeyComboAsync(functionCallToExecute);
                case "scroll_at":
                case "scroll_document_at":
                    return await ExecuteScrollAtAsync(functionCallToExecute);
                case "drag_and_drop":
                case "drag":
                    return await ExecuteDragAndDropAsync(functionCallToExecute);
                case "wait_for":
                case "wait":
                case "wait_for_seconds":
                    return await ExecuteWaitForAsync(functionCallToExecute);
                case "open_app":
                case "open_application":
                    return await ExecuteOpenAppAsync(functionCallToExecute);
                default:
                    string unsupportedMessage =
                        $"unsupported function \"{normalizedFunctionName}\" — Buddy.Windows did not execute it";
                    BuddyLog.Error($"Computer Use {unsupportedMessage}.");
                    return unsupportedMessage;
            }
        }
        catch (Exception executionException)
        {
            BuddyLog.Error(
                $"Computer Use action \"{normalizedFunctionName}\" threw {executionException.GetType().Name}: {executionException.Message}");
            return $"action raised exception: {executionException.Message}";
        }
    }

    private async Task<string> ExecuteClickAtAsync(ComputerUseFunctionCall functionCallToExecute, bool isRightClick)
    {
        (int virtualPixelX, int virtualPixelY) = ResolveTargetVirtualDesktopPixel(functionCallToExecute);

        if (isRightClick)
        {
            await WindowsInputSimulator.RightClickAsync(virtualPixelX, virtualPixelY);
            return $"right-clicked at ({virtualPixelX}, {virtualPixelY})";
        }

        await WindowsInputSimulator.LeftClickAsync(virtualPixelX, virtualPixelY);
        return $"clicked at ({virtualPixelX}, {virtualPixelY})";
    }

    private async Task<string> ExecuteDoubleClickAtAsync(ComputerUseFunctionCall functionCallToExecute)
    {
        (int virtualPixelX, int virtualPixelY) = ResolveTargetVirtualDesktopPixel(functionCallToExecute);
        await WindowsInputSimulator.DoubleLeftClickAsync(virtualPixelX, virtualPixelY);
        return $"double-clicked at ({virtualPixelX}, {virtualPixelY})";
    }

    private string ExecuteHoverAt(ComputerUseFunctionCall functionCallToExecute)
    {
        (int virtualPixelX, int virtualPixelY) = ResolveTargetVirtualDesktopPixel(functionCallToExecute);
        WindowsInputSimulator.MoveMouseToVirtualDesktopPixel(virtualPixelX, virtualPixelY);
        return $"moved mouse to ({virtualPixelX}, {virtualPixelY})";
    }

    private async Task<string> ExecuteTypeTextAtAsync(ComputerUseFunctionCall functionCallToExecute)
    {
        (int virtualPixelX, int virtualPixelY) = ResolveTargetVirtualDesktopPixel(functionCallToExecute);
        string textToType = ReadStringArgument(functionCallToExecute, "text", "value", "string");

        await WindowsInputSimulator.LeftClickAsync(virtualPixelX, virtualPixelY);
        await Task.Delay(80);
        await WindowsInputSimulator.TypeTextAsync(textToType);
        return $"clicked ({virtualPixelX}, {virtualPixelY}) and typed {textToType.Length} characters";
    }

    private async Task<string> ExecuteTypeTextAsync(ComputerUseFunctionCall functionCallToExecute)
    {
        string textToType = ReadStringArgument(functionCallToExecute, "text", "value", "string");
        await WindowsInputSimulator.TypeTextAsync(textToType);
        return $"typed {textToType.Length} characters at the focused control";
    }

    private async Task<string> ExecuteKeyComboAsync(ComputerUseFunctionCall functionCallToExecute)
    {
        string keyComboDescription = ReadStringArgument(
            functionCallToExecute,
            "keys",
            "key",
            "combination",
            "key_combination");

        if (string.IsNullOrWhiteSpace(keyComboDescription)
            && functionCallToExecute.Args.TryGetValue("keys", out JsonElement keysElement)
            && keysElement.ValueKind == JsonValueKind.Array)
        {
            List<string> keyParts = new();

            foreach (JsonElement keysArrayItem in keysElement.EnumerateArray())
            {
                if (keysArrayItem.ValueKind == JsonValueKind.String)
                {
                    keyParts.Add(keysArrayItem.GetString() ?? "");
                }
            }

            keyComboDescription = string.Join("+", keyParts);
        }

        await WindowsInputSimulator.PressKeyComboAsync(keyComboDescription);
        return $"pressed key combo \"{keyComboDescription}\"";
    }

    private async Task<string> ExecuteScrollAtAsync(ComputerUseFunctionCall functionCallToExecute)
    {
        (int virtualPixelX, int virtualPixelY) = ResolveTargetVirtualDesktopPixel(functionCallToExecute);
        string scrollDirection = ReadStringArgument(functionCallToExecute, "direction").ToLowerInvariant();
        double scrollAmountInWheelClicks = ReadDoubleArgument(
            functionCallToExecute,
            defaultValue: 3.0,
            "amount",
            "magnitude",
            "wheel_clicks",
            "clicks");

        // One mouse-wheel "click" is 120 wheel-delta units (WHEEL_DELTA). Negative wheel
        // delta scrolls down, positive scrolls up; horizontal mirrors that with hwheel.
        const int wheelDeltaPerClick = 120;
        int wheelDelta = (int)Math.Round(scrollAmountInWheelClicks * wheelDeltaPerClick);
        bool isHorizontal = scrollDirection is "left" or "right";
        bool isNegative = scrollDirection is "down" or "right";

        if (isNegative)
        {
            wheelDelta = -wheelDelta;
        }

        if (isHorizontal)
        {
            WindowsInputSimulator.ScrollHorizontal(virtualPixelX, virtualPixelY, wheelDelta);
        }
        else
        {
            WindowsInputSimulator.ScrollVertical(virtualPixelX, virtualPixelY, wheelDelta);
        }

        // Apps often debounce scrolls a frame or two before the new viewport finalizes.
        await Task.Delay(60);
        return $"scrolled {scrollDirection} by {scrollAmountInWheelClicks:0.##} clicks at ({virtualPixelX}, {virtualPixelY})";
    }

    private async Task<string> ExecuteDragAndDropAsync(ComputerUseFunctionCall functionCallToExecute)
    {
        double normalizedFromX = ReadDoubleArgument(functionCallToExecute, defaultValue: 0, "from_x", "start_x", "x1");
        double normalizedFromY = ReadDoubleArgument(functionCallToExecute, defaultValue: 0, "from_y", "start_y", "y1");
        double normalizedToX = ReadDoubleArgument(functionCallToExecute, defaultValue: 0, "to_x", "end_x", "x2");
        double normalizedToY = ReadDoubleArgument(functionCallToExecute, defaultValue: 0, "to_y", "end_y", "y2");
        int screenNumber = ReadScreenNumberArgument(functionCallToExecute);

        (int fromVirtualPixelX, int fromVirtualPixelY) = CoordinateMapper.DenormalizeToVirtualDesktopPixel(
            normalizedFromX,
            normalizedFromY,
            ResolveScreenshotBoundsForScreenNumber(screenNumber));
        (int toVirtualPixelX, int toVirtualPixelY) = CoordinateMapper.DenormalizeToVirtualDesktopPixel(
            normalizedToX,
            normalizedToY,
            ResolveScreenshotBoundsForScreenNumber(screenNumber));

        await WindowsInputSimulator.DragAsync(
            fromVirtualPixelX,
            fromVirtualPixelY,
            toVirtualPixelX,
            toVirtualPixelY);
        return $"dragged from ({fromVirtualPixelX}, {fromVirtualPixelY}) to ({toVirtualPixelX}, {toVirtualPixelY})";
    }

    private static async Task<string> ExecuteWaitForAsync(ComputerUseFunctionCall functionCallToExecute)
    {
        double waitDurationInSeconds = ReadDoubleArgument(
            functionCallToExecute,
            defaultValue: 1.0,
            "seconds",
            "duration",
            "duration_seconds",
            "wait");
        TimeSpan waitDuration = TimeSpan.FromSeconds(Math.Clamp(waitDurationInSeconds, 0.05, 10.0));
        await Task.Delay(waitDuration);
        return $"waited {waitDuration.TotalSeconds:0.##} seconds";
    }

    private static Task<string> ExecuteOpenAppAsync(ComputerUseFunctionCall functionCallToExecute)
    {
        string applicationName = ReadStringArgument(functionCallToExecute, "app", "application", "name", "value");
        string fallbackResult =
            $"open_app(\"{applicationName}\") is not implemented yet — please use Win key + type instead";
        BuddyLog.Workflow($"Computer Use {fallbackResult}.");
        return Task.FromResult(fallbackResult);
    }

    private (int VirtualDesktopPixelX, int VirtualDesktopPixelY) ResolveTargetVirtualDesktopPixel(
        ComputerUseFunctionCall functionCallToExecute)
    {
        double normalizedX = ReadDoubleArgument(functionCallToExecute, defaultValue: 0, "x", "x_coordinate", "norm_x");
        double normalizedY = ReadDoubleArgument(functionCallToExecute, defaultValue: 0, "y", "y_coordinate", "norm_y");
        int screenNumber = ReadScreenNumberArgument(functionCallToExecute);

        return CoordinateMapper.DenormalizeToVirtualDesktopPixel(
            normalizedX,
            normalizedY,
            ResolveScreenshotBoundsForScreenNumber(screenNumber));
    }

    private System.Drawing.Rectangle ResolveScreenshotBoundsForScreenNumber(int screenNumber)
    {
        // First preference: the screen capture sent for this turn — this is the
        // image the model literally saw, so its coordinate space is canonical.
        if (screenCapturesForCurrentTurn.Count > 0)
        {
            WindowsScreenCapture? matchedCapture = null;

            foreach (WindowsScreenCapture screenCapture in screenCapturesForCurrentTurn)
            {
                if (screenCapture.ScreenNumber == screenNumber)
                {
                    matchedCapture = screenCapture;
                    break;
                }
            }

            matchedCapture ??= screenCapturesForCurrentTurn[0];
            return new System.Drawing.Rectangle(
                matchedCapture.BoundsLeftInPixels,
                matchedCapture.BoundsTopInPixels,
                matchedCapture.WidthInPixels,
                matchedCapture.HeightInPixels);
        }

        // Fall back to live Forms.Screen enumeration if we have no captures (shouldn't
        // happen, but the action handler must remain robust to a missing baseline).
        System.Windows.Forms.Screen[] allScreens = System.Windows.Forms.Screen.AllScreens;
        int screenIndex = screenNumber - 1;
        System.Windows.Forms.Screen targetScreen = screenIndex >= 0 && screenIndex < allScreens.Length
            ? allScreens[screenIndex]
            : System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        return targetScreen.Bounds;
    }

    private static int ReadScreenNumberArgument(ComputerUseFunctionCall functionCallToExecute)
    {
        if (functionCallToExecute.Args.TryGetValue("screen", out JsonElement screenElement))
        {
            int screenNumberFromArgs = TryReadIntFromJsonElement(screenElement, defaultValue: 1);
            return Math.Max(1, screenNumberFromArgs);
        }

        if (functionCallToExecute.Args.TryGetValue("screen_number", out JsonElement screenNumberElement))
        {
            return Math.Max(1, TryReadIntFromJsonElement(screenNumberElement, defaultValue: 1));
        }

        return 1;
    }

    private static double ReadDoubleArgument(
        ComputerUseFunctionCall functionCallToExecute,
        double defaultValue,
        params string[] argumentNamesToTry)
    {
        foreach (string argumentName in argumentNamesToTry)
        {
            if (!functionCallToExecute.Args.TryGetValue(argumentName, out JsonElement argumentElement))
            {
                continue;
            }

            switch (argumentElement.ValueKind)
            {
                case JsonValueKind.Number:
                    return argumentElement.GetDouble();
                case JsonValueKind.String when double.TryParse(
                    argumentElement.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double parsedDouble):
                    return parsedDouble;
            }
        }

        return defaultValue;
    }

    private static string ReadStringArgument(
        ComputerUseFunctionCall functionCallToExecute,
        params string[] argumentNamesToTry)
    {
        foreach (string argumentName in argumentNamesToTry)
        {
            if (!functionCallToExecute.Args.TryGetValue(argumentName, out JsonElement argumentElement))
            {
                continue;
            }

            return argumentElement.ValueKind switch
            {
                JsonValueKind.String => argumentElement.GetString() ?? "",
                JsonValueKind.Number => argumentElement.GetRawText(),
                _ => argumentElement.GetRawText()
            };
        }

        return "";
    }

    private static int TryReadIntFromJsonElement(JsonElement jsonElement, int defaultValue)
    {
        return jsonElement.ValueKind switch
        {
            JsonValueKind.Number when jsonElement.TryGetInt32(out int parsedInt) => parsedInt,
            JsonValueKind.String when int.TryParse(
                jsonElement.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsedStringInt) => parsedStringInt,
            _ => defaultValue
        };
    }

    private static string SerializeArgsForLog(Dictionary<string, JsonElement> functionArguments)
    {
        try
        {
            return JsonSerializer.Serialize(functionArguments);
        }
        catch (Exception)
        {
            return "{ unserializable }";
        }
    }
}
