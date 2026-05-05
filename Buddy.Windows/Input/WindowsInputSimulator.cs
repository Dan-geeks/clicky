using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Buddy.Windows.Diagnostics;
using Forms = System.Windows.Forms;

namespace Buddy.Windows.Input;

/// <summary>
/// Synthesises mouse and keyboard input via the Win32 <c>SendInput</c> API. Used by the
/// Computer Use action mode to actually click, drag, type, and scroll on behalf of the
/// AI agent — the existing overlay only draws on top of the screen, so this class is
/// what turns a Gemini FunctionCall into a real desktop action.
/// </summary>
public static class WindowsInputSimulator
{
    private const int InputTypeMouse = 0;
    private const int InputTypeKeyboard = 1;

    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint MouseEventWheel = 0x0800;
    private const uint MouseEventHWheel = 0x01000;
    private const uint MouseEventAbsolute = 0x8000;
    private const uint MouseEventVirtualDesk = 0x4000;

    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const uint KeyEventScanCode = 0x0008;
    private const uint KeyEventExtendedKey = 0x0001;

    private const int VirtualScreenWidthSystemMetric = 78;
    private const int VirtualScreenHeightSystemMetric = 79;
    private const int VirtualScreenLeftSystemMetric = 76;
    private const int VirtualScreenTopSystemMetric = 77;

    // SendInput's absolute-mouse coordinates use the 0..65535 range across the virtual
    // desktop when MOUSEEVENTF_VIRTUALDESK is set. We pre-compute the fractional pixel
    // values so per-call math stays cheap.
    private const double AbsoluteMouseCoordinateScale = 65535.0;

    private const int DefaultClickHoldMilliseconds = 30;
    private const int DefaultModifierHoldMilliseconds = 24;
    private const int DefaultBetweenKeystrokesMilliseconds = 8;

    public static void MoveMouseToVirtualDesktopPixel(int virtualDesktopPixelX, int virtualDesktopPixelY)
    {
        int virtualScreenLeft = GetSystemMetrics(VirtualScreenLeftSystemMetric);
        int virtualScreenTop = GetSystemMetrics(VirtualScreenTopSystemMetric);
        int virtualScreenWidth = GetSystemMetrics(VirtualScreenWidthSystemMetric);
        int virtualScreenHeight = GetSystemMetrics(VirtualScreenHeightSystemMetric);

        if (virtualScreenWidth <= 0 || virtualScreenHeight <= 0)
        {
            BuddyLog.Error(
                "Computer Use mouse move skipped because the virtual screen has zero width or height.");
            return;
        }

        double normalizedX = (virtualDesktopPixelX - virtualScreenLeft) * (AbsoluteMouseCoordinateScale / virtualScreenWidth);
        double normalizedY = (virtualDesktopPixelY - virtualScreenTop) * (AbsoluteMouseCoordinateScale / virtualScreenHeight);
        Input mouseMoveInput = new()
        {
            Type = InputTypeMouse,
            Data = new InputUnion
            {
                MouseInput = new MouseInput
                {
                    X = (int)Math.Round(normalizedX),
                    Y = (int)Math.Round(normalizedY),
                    Flags = MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk
                }
            }
        };

        SendInputBatch(new[] { mouseMoveInput }, "MoveMouseToVirtualDesktopPixel");
    }

    public static async Task LeftClickAsync(int virtualDesktopPixelX, int virtualDesktopPixelY)
    {
        MoveMouseToVirtualDesktopPixel(virtualDesktopPixelX, virtualDesktopPixelY);
        await Task.Delay(DefaultClickHoldMilliseconds);
        SendInputBatch(new[] { CreateMouseButtonInput(MouseEventLeftDown) }, "LeftButtonDown");
        await Task.Delay(DefaultClickHoldMilliseconds);
        SendInputBatch(new[] { CreateMouseButtonInput(MouseEventLeftUp) }, "LeftButtonUp");
    }

    public static async Task RightClickAsync(int virtualDesktopPixelX, int virtualDesktopPixelY)
    {
        MoveMouseToVirtualDesktopPixel(virtualDesktopPixelX, virtualDesktopPixelY);
        await Task.Delay(DefaultClickHoldMilliseconds);
        SendInputBatch(new[] { CreateMouseButtonInput(MouseEventRightDown) }, "RightButtonDown");
        await Task.Delay(DefaultClickHoldMilliseconds);
        SendInputBatch(new[] { CreateMouseButtonInput(MouseEventRightUp) }, "RightButtonUp");
    }

    public static async Task DoubleLeftClickAsync(int virtualDesktopPixelX, int virtualDesktopPixelY)
    {
        await LeftClickAsync(virtualDesktopPixelX, virtualDesktopPixelY);
        await Task.Delay(DefaultClickHoldMilliseconds);
        await LeftClickAsync(virtualDesktopPixelX, virtualDesktopPixelY);
    }

    public static async Task DragAsync(
        int fromVirtualDesktopPixelX,
        int fromVirtualDesktopPixelY,
        int toVirtualDesktopPixelX,
        int toVirtualDesktopPixelY)
    {
        MoveMouseToVirtualDesktopPixel(fromVirtualDesktopPixelX, fromVirtualDesktopPixelY);
        await Task.Delay(DefaultClickHoldMilliseconds);
        SendInputBatch(new[] { CreateMouseButtonInput(MouseEventLeftDown) }, "DragStartLeftButtonDown");
        await Task.Delay(DefaultClickHoldMilliseconds);

        // Move in a few steps so apps that watch for actual drag deltas (like reorderable
        // lists or canvas tools) see motion rather than a single teleport.
        const int dragInterpolationSteps = 14;
        for (int interpolationStep = 1; interpolationStep <= dragInterpolationSteps; interpolationStep++)
        {
            double progress = interpolationStep / (double)dragInterpolationSteps;
            int interpolatedX = (int)Math.Round(fromVirtualDesktopPixelX
                + (toVirtualDesktopPixelX - fromVirtualDesktopPixelX) * progress);
            int interpolatedY = (int)Math.Round(fromVirtualDesktopPixelY
                + (toVirtualDesktopPixelY - fromVirtualDesktopPixelY) * progress);
            MoveMouseToVirtualDesktopPixel(interpolatedX, interpolatedY);
            await Task.Delay(8);
        }

        SendInputBatch(new[] { CreateMouseButtonInput(MouseEventLeftUp) }, "DragEndLeftButtonUp");
    }

    public static void ScrollVertical(int virtualDesktopPixelX, int virtualDesktopPixelY, int wheelDelta)
    {
        MoveMouseToVirtualDesktopPixel(virtualDesktopPixelX, virtualDesktopPixelY);
        Input wheelInput = new()
        {
            Type = InputTypeMouse,
            Data = new InputUnion
            {
                MouseInput = new MouseInput
                {
                    MouseData = wheelDelta,
                    Flags = MouseEventWheel
                }
            }
        };
        SendInputBatch(new[] { wheelInput }, "ScrollVertical");
    }

    public static void ScrollHorizontal(int virtualDesktopPixelX, int virtualDesktopPixelY, int wheelDelta)
    {
        MoveMouseToVirtualDesktopPixel(virtualDesktopPixelX, virtualDesktopPixelY);
        Input wheelInput = new()
        {
            Type = InputTypeMouse,
            Data = new InputUnion
            {
                MouseInput = new MouseInput
                {
                    MouseData = wheelDelta,
                    Flags = MouseEventHWheel
                }
            }
        };
        SendInputBatch(new[] { wheelInput }, "ScrollHorizontal");
    }

    /// <summary>
    /// Types arbitrary Unicode text by sending KEYEVENTF_UNICODE inputs. Uses Unicode
    /// rather than per-character VK lookups so accented characters and emoji come through
    /// cleanly regardless of the active keyboard layout.
    /// </summary>
    public static async Task TypeTextAsync(string textToType)
    {
        if (string.IsNullOrEmpty(textToType))
        {
            return;
        }

        foreach (char characterToType in textToType)
        {
            Input keyDownInput = new()
            {
                Type = InputTypeKeyboard,
                Data = new InputUnion
                {
                    KeyboardInput = new KeyboardInput
                    {
                        VirtualKeyCode = 0,
                        ScanCode = characterToType,
                        Flags = KeyEventUnicode
                    }
                }
            };
            Input keyUpInput = new()
            {
                Type = InputTypeKeyboard,
                Data = new InputUnion
                {
                    KeyboardInput = new KeyboardInput
                    {
                        VirtualKeyCode = 0,
                        ScanCode = characterToType,
                        Flags = KeyEventUnicode | KeyEventKeyUp
                    }
                }
            };

            SendInputBatch(new[] { keyDownInput, keyUpInput }, "TypeTextCharacter");
            await Task.Delay(DefaultBetweenKeystrokesMilliseconds);
        }
    }

    /// <summary>
    /// Presses a keyboard chord like <c>"ctrl+shift+t"</c> or a single named key like
    /// <c>"enter"</c>. Modifier keys are pressed in order, the main key is tapped, and
    /// modifiers are released in reverse — the standard pattern apps expect.
    /// </summary>
    public static async Task PressKeyComboAsync(string keyComboDescription)
    {
        if (string.IsNullOrWhiteSpace(keyComboDescription))
        {
            return;
        }

        List<int> modifierVirtualKeyCodes = new();
        int? mainVirtualKeyCode = null;

        foreach (string keyToken in keyComboDescription.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmedKeyToken = keyToken.Trim();

            if (TryResolveModifierVirtualKeyCode(trimmedKeyToken, out int modifierVirtualKeyCode))
            {
                modifierVirtualKeyCodes.Add(modifierVirtualKeyCode);
                continue;
            }

            mainVirtualKeyCode = ResolveNonModifierVirtualKeyCode(trimmedKeyToken);
        }

        if (mainVirtualKeyCode is null)
        {
            BuddyLog.Error($"Computer Use key combo \"{keyComboDescription}\" did not include a non-modifier key.");
            return;
        }

        foreach (int modifierVirtualKeyCode in modifierVirtualKeyCodes)
        {
            SendVirtualKey(modifierVirtualKeyCode, isKeyDown: true);
            await Task.Delay(DefaultModifierHoldMilliseconds);
        }

        SendVirtualKey(mainVirtualKeyCode.Value, isKeyDown: true);
        await Task.Delay(DefaultClickHoldMilliseconds);
        SendVirtualKey(mainVirtualKeyCode.Value, isKeyDown: false);

        for (int modifierIndex = modifierVirtualKeyCodes.Count - 1; modifierIndex >= 0; modifierIndex--)
        {
            await Task.Delay(DefaultModifierHoldMilliseconds);
            SendVirtualKey(modifierVirtualKeyCodes[modifierIndex], isKeyDown: false);
        }
    }

    public static (int VirtualDesktopPixelX, int VirtualDesktopPixelY) GetCurrentMouseVirtualDesktopPixel()
    {
        System.Drawing.Point currentMousePosition = Forms.Cursor.Position;
        return (currentMousePosition.X, currentMousePosition.Y);
    }

    private static Input CreateMouseButtonInput(uint mouseEventFlag)
    {
        return new Input
        {
            Type = InputTypeMouse,
            Data = new InputUnion
            {
                MouseInput = new MouseInput
                {
                    Flags = mouseEventFlag
                }
            }
        };
    }

    private static void SendVirtualKey(int virtualKeyCode, bool isKeyDown)
    {
        uint flags = isKeyDown ? 0u : KeyEventKeyUp;

        if (IsExtendedVirtualKey(virtualKeyCode))
        {
            flags |= KeyEventExtendedKey;
        }

        Input keyboardInput = new()
        {
            Type = InputTypeKeyboard,
            Data = new InputUnion
            {
                KeyboardInput = new KeyboardInput
                {
                    VirtualKeyCode = (ushort)virtualKeyCode,
                    Flags = flags
                }
            }
        };

        SendInputBatch(new[] { keyboardInput }, isKeyDown ? "KeyDown" : "KeyUp");
    }

    private static void SendInputBatch(Input[] inputsToSend, string callerOperationLabel)
    {
        uint sentInputCount = SendInput(
            (uint)inputsToSend.Length,
            inputsToSend,
            Marshal.SizeOf<Input>());

        if (sentInputCount == inputsToSend.Length)
        {
            return;
        }

        int win32ErrorCode = Marshal.GetLastWin32Error();
        BuddyLog.Error(
            $"Computer Use SendInput failed for {callerOperationLabel}: sent {sentInputCount}/{inputsToSend.Length} (win32={win32ErrorCode}).");
    }

    private static bool TryResolveModifierVirtualKeyCode(string keyToken, out int modifierVirtualKeyCode)
    {
        switch (keyToken.ToLowerInvariant())
        {
            case "ctrl":
            case "control":
                modifierVirtualKeyCode = 0x11;
                return true;
            case "shift":
                modifierVirtualKeyCode = 0x10;
                return true;
            case "alt":
            case "menu":
                modifierVirtualKeyCode = 0x12;
                return true;
            case "win":
            case "windows":
            case "meta":
            case "super":
                modifierVirtualKeyCode = 0x5B;
                return true;
            default:
                modifierVirtualKeyCode = 0;
                return false;
        }
    }

    private static int ResolveNonModifierVirtualKeyCode(string keyToken)
    {
        string normalizedKeyToken = keyToken.ToLowerInvariant();

        switch (normalizedKeyToken)
        {
            case "enter":
            case "return":
                return 0x0D;
            case "tab":
                return 0x09;
            case "esc":
            case "escape":
                return 0x1B;
            case "space":
            case "spacebar":
                return 0x20;
            case "backspace":
                return 0x08;
            case "delete":
            case "del":
                return 0x2E;
            case "home":
                return 0x24;
            case "end":
                return 0x23;
            case "pageup":
            case "pgup":
                return 0x21;
            case "pagedown":
            case "pgdn":
                return 0x22;
            case "left":
            case "arrowleft":
                return 0x25;
            case "up":
            case "arrowup":
                return 0x26;
            case "right":
            case "arrowright":
                return 0x27;
            case "down":
            case "arrowdown":
                return 0x28;
            case "insert":
            case "ins":
                return 0x2D;
        }

        if (normalizedKeyToken.StartsWith("f", StringComparison.Ordinal)
            && int.TryParse(normalizedKeyToken[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int functionKeyNumber)
            && functionKeyNumber >= 1
            && functionKeyNumber <= 24)
        {
            return 0x70 + (functionKeyNumber - 1);
        }

        if (normalizedKeyToken.Length == 1)
        {
            char singleKeyCharacter = char.ToUpperInvariant(normalizedKeyToken[0]);

            if (singleKeyCharacter is >= 'A' and <= 'Z')
            {
                return singleKeyCharacter;
            }

            if (singleKeyCharacter is >= '0' and <= '9')
            {
                return singleKeyCharacter;
            }
        }

        BuddyLog.Error($"Computer Use unknown key token \"{keyToken}\"; falling back to no-op.");
        return 0;
    }

    private static bool IsExtendedVirtualKey(int virtualKeyCode)
    {
        return virtualKeyCode is 0x21 // PageUp
            or 0x22 // PageDown
            or 0x23 // End
            or 0x24 // Home
            or 0x25 // Left
            or 0x26 // Up
            or 0x27 // Right
            or 0x28 // Down
            or 0x2D // Insert
            or 0x2E // Delete
            or 0x5B // LWin
            or 0x5C; // RWin
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int systemMetricIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int Type;

        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput MouseInput;

        [FieldOffset(0)]
        public KeyboardInput KeyboardInput;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;

        public int Y;

        public int MouseData;

        public uint Flags;

        public uint Time;

        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKeyCode;

        public ushort ScanCode;

        public uint Flags;

        public uint Time;

        public IntPtr ExtraInfo;
    }
}
