using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Buddy.Windows.Diagnostics;

namespace Buddy.Windows.Voice;

public sealed class PushToTalkHotkeyMonitor : IDisposable
{
    private const int LowLevelKeyboardHookIdentifier = 13;
    private const int KeyDownMessageIdentifier = 0x0100;
    private const int KeyUpMessageIdentifier = 0x0101;
    private const int SystemKeyDownMessageIdentifier = 0x0104;
    private const int SystemKeyUpMessageIdentifier = 0x0105;

    private const int ControlVirtualKeyCode = 0x11;
    private const int LeftControlVirtualKeyCode = 0xA2;
    private const int RightControlVirtualKeyCode = 0xA3;
    private const int AltVirtualKeyCode = 0x12;
    private const int LeftAltVirtualKeyCode = 0xA4;
    private const int RightAltVirtualKeyCode = 0xA5;
    private const int SpaceVirtualKeyCode = 0x20;
    private const int EscapeVirtualKeyCode = 0x1B;
    private const int ModelCycleVirtualKeyCode = 0x4D; // M: Ctrl+Alt+M cycles the Ask Buddy model.
    private const int ActionModeVirtualKeyCode = 0x41; // 'A' — Ctrl+Alt+A launches Computer Use action mode.

    private readonly LowLevelKeyboardProcedure keyboardProcedure;
    private readonly HashSet<int> pressedModifierVirtualKeyCodes = new();
    private readonly bool isVoiceShortcutEnabled;
    private IntPtr keyboardHookHandle = IntPtr.Zero;
    private bool isSpaceKeyPressed;
    private bool isTextPromptHotkeyPressed;
    private bool isEscapeKeyPressed;
    private bool isShutdownHotkeyPressed;
    private bool isActionModeKeyPressed;
    private bool isActionModeHotkeyPressed;
    private bool isModelCycleKeyPressed;
    private bool isModelCycleHotkeyPressed;
    private bool isDisposed;

    public PushToTalkHotkeyMonitor(bool isVoiceShortcutEnabled)
    {
        this.isVoiceShortcutEnabled = isVoiceShortcutEnabled;
        keyboardProcedure = HandleKeyboardEvent;
    }

    public event EventHandler<PushToTalkHotkeyChangedEventArgs>? PushToTalkHotkeyChanged;

    public event EventHandler? TextPromptHotkeyPressed;

    public event EventHandler? ShutdownHotkeyPressed;

    public event EventHandler? ChatModelCycleHotkeyPressed;

    /// <summary>
    /// Fires once when Ctrl+Alt+A transitions from up→down. Routed to the Computer Use
    /// agent so the user can ask Buddy to actually operate the desktop (separate flow
    /// from the Ctrl+Alt+Space typed prompt and Ctrl+Alt+Esc shutdown shortcut).
    /// </summary>
    public event EventHandler? ActionModeHotkeyPressed;

    public bool IsPushToTalkPressed { get; private set; }

    public bool IsMonitoring { get; private set; }

    public string? MonitoringErrorMessage { get; private set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);

        if (keyboardHookHandle != IntPtr.Zero)
        {
            return;
        }

        BuddyLog.Workflow("Starting global keyboard hook for Buddy shortcuts.");
        using Process currentProcess = Process.GetCurrentProcess();
        ProcessModule? currentProcessModule = currentProcess.MainModule;
        IntPtr currentProcessModuleHandle = currentProcessModule is null
            ? IntPtr.Zero
            : GetModuleHandle(currentProcessModule.ModuleName);

        keyboardHookHandle = SetWindowsHookEx(
            LowLevelKeyboardHookIdentifier,
            keyboardProcedure,
            currentProcessModuleHandle,
            0);

        if (keyboardHookHandle == IntPtr.Zero)
        {
            int win32ErrorCode = Marshal.GetLastWin32Error();
            IsMonitoring = false;
            IsPushToTalkPressed = false;
            MonitoringErrorMessage = $"Keyboard hook failed ({win32ErrorCode})";
            BuddyLog.Error(MonitoringErrorMessage);
            NotifyPushToTalkHotkeyChanged();
            return;
        }

        IsMonitoring = true;
        MonitoringErrorMessage = null;
        SynchronizeModifierStateFromKeyboard();
        BuddyLog.Workflow("Global keyboard hook is active.");
        NotifyPushToTalkHotkeyChanged();
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        StopMonitoring();
    }

    private void StopMonitoring()
    {
        if (keyboardHookHandle != IntPtr.Zero)
        {
            BuddyLog.Workflow("Stopping global keyboard hook.");
            _ = UnhookWindowsHookEx(keyboardHookHandle);
            keyboardHookHandle = IntPtr.Zero;
        }

        pressedModifierVirtualKeyCodes.Clear();
        isSpaceKeyPressed = false;
        isTextPromptHotkeyPressed = false;
        isEscapeKeyPressed = false;
        isShutdownHotkeyPressed = false;
        isActionModeKeyPressed = false;
        isActionModeHotkeyPressed = false;
        isModelCycleKeyPressed = false;
        isModelCycleHotkeyPressed = false;
        IsMonitoring = false;
        IsPushToTalkPressed = false;
    }

    private IntPtr HandleKeyboardEvent(int keyboardEventCode, IntPtr messageIdentifier, IntPtr keyboardEventData)
    {
        if (keyboardEventCode >= 0)
        {
            int messageIdentifierValue = messageIdentifier.ToInt32();

            if (IsKeyboardMessageThatChangesKeyState(messageIdentifierValue))
            {
                int virtualKeyCode = Marshal.ReadInt32(keyboardEventData);

                if (IsTrackedModifierVirtualKeyCode(virtualKeyCode))
                {
                    bool isModifierKeyPressed = IsKeyDownMessage(messageIdentifierValue);
                    UpdateTrackedModifierState(virtualKeyCode, isModifierKeyPressed);
                }
                else if (virtualKeyCode == SpaceVirtualKeyCode)
                {
                    bool isSpaceKeyPressed = IsKeyDownMessage(messageIdentifierValue);
                    UpdateSpaceKeyState(isSpaceKeyPressed);
                }
                else if (virtualKeyCode == EscapeVirtualKeyCode)
                {
                    bool isEscapeKeyPressed = IsKeyDownMessage(messageIdentifierValue);
                    UpdateEscapeKeyState(isEscapeKeyPressed);
                }
                else if (virtualKeyCode == ActionModeVirtualKeyCode)
                {
                    bool isActionModeKeyPressed = IsKeyDownMessage(messageIdentifierValue);
                    UpdateActionModeKeyState(isActionModeKeyPressed);
                }
                else if (virtualKeyCode == ModelCycleVirtualKeyCode)
                {
                    bool isModelCycleKeyPressed = IsKeyDownMessage(messageIdentifierValue);
                    UpdateModelCycleKeyState(isModelCycleKeyPressed);
                }
            }
        }

        return CallNextHookEx(keyboardHookHandle, keyboardEventCode, messageIdentifier, keyboardEventData);
    }

    private void SynchronizeModifierStateFromKeyboard()
    {
        pressedModifierVirtualKeyCodes.Clear();

        AddModifierIfCurrentlyPressed(ControlVirtualKeyCode);
        AddModifierIfCurrentlyPressed(LeftControlVirtualKeyCode);
        AddModifierIfCurrentlyPressed(RightControlVirtualKeyCode);
        AddModifierIfCurrentlyPressed(AltVirtualKeyCode);
        AddModifierIfCurrentlyPressed(LeftAltVirtualKeyCode);
        AddModifierIfCurrentlyPressed(RightAltVirtualKeyCode);

        isSpaceKeyPressed = IsVirtualKeyCurrentlyPressed(SpaceVirtualKeyCode);
        isEscapeKeyPressed = IsVirtualKeyCurrentlyPressed(EscapeVirtualKeyCode);
        isActionModeKeyPressed = IsVirtualKeyCurrentlyPressed(ActionModeVirtualKeyCode);
        isModelCycleKeyPressed = IsVirtualKeyCurrentlyPressed(ModelCycleVirtualKeyCode);
        isTextPromptHotkeyPressed = isSpaceKeyPressed && IsAnyControlKeyPressed() && IsAnyAltKeyPressed();
        isShutdownHotkeyPressed = isEscapeKeyPressed && IsAnyControlKeyPressed() && IsAnyAltKeyPressed();
        isActionModeHotkeyPressed = isActionModeKeyPressed && IsAnyControlKeyPressed() && IsAnyAltKeyPressed();
        isModelCycleHotkeyPressed = isModelCycleKeyPressed && IsAnyControlKeyPressed() && IsAnyAltKeyPressed();
        IsPushToTalkPressed = isVoiceShortcutEnabled
            && IsAnyControlKeyPressed()
            && IsAnyAltKeyPressed()
            && !isTextPromptHotkeyPressed
            && !isShutdownHotkeyPressed
            && !isActionModeHotkeyPressed
            && !isModelCycleHotkeyPressed;
    }

    private void AddModifierIfCurrentlyPressed(int virtualKeyCode)
    {
        if (IsVirtualKeyCurrentlyPressed(virtualKeyCode))
        {
            pressedModifierVirtualKeyCodes.Add(virtualKeyCode);
        }
    }

    private void UpdateTrackedModifierState(int virtualKeyCode, bool isModifierKeyPressed)
    {
        if (isModifierKeyPressed)
        {
            pressedModifierVirtualKeyCodes.Add(virtualKeyCode);
        }
        else
        {
            RemoveReleasedModifierVirtualKeyCode(virtualKeyCode);
        }

        RefreshShortcutStates();
    }

    private void UpdateSpaceKeyState(bool isSpaceKeyPressed)
    {
        if (this.isSpaceKeyPressed == isSpaceKeyPressed)
        {
            return;
        }

        this.isSpaceKeyPressed = isSpaceKeyPressed;
        RefreshShortcutStates();
    }

    private void UpdateEscapeKeyState(bool isEscapeKeyPressed)
    {
        if (this.isEscapeKeyPressed == isEscapeKeyPressed)
        {
            return;
        }

        this.isEscapeKeyPressed = isEscapeKeyPressed;
        RefreshShortcutStates();
    }

    private void UpdateActionModeKeyState(bool isActionModeKeyPressed)
    {
        if (this.isActionModeKeyPressed == isActionModeKeyPressed)
        {
            return;
        }

        this.isActionModeKeyPressed = isActionModeKeyPressed;
        RefreshShortcutStates();
    }

    private void UpdateModelCycleKeyState(bool isModelCycleKeyPressed)
    {
        if (this.isModelCycleKeyPressed == isModelCycleKeyPressed)
        {
            return;
        }

        this.isModelCycleKeyPressed = isModelCycleKeyPressed;
        RefreshShortcutStates();
    }

    private void RefreshShortcutStates()
    {
        bool updatedTextPromptHotkeyPressedState = isSpaceKeyPressed
            && IsAnyControlKeyPressed()
            && IsAnyAltKeyPressed();
        bool shouldNotifyTextPromptHotkeyPressed = updatedTextPromptHotkeyPressedState
            && !isTextPromptHotkeyPressed;

        isTextPromptHotkeyPressed = updatedTextPromptHotkeyPressedState;

        bool updatedShutdownHotkeyPressedState = isEscapeKeyPressed
            && IsAnyControlKeyPressed()
            && IsAnyAltKeyPressed();
        bool shouldNotifyShutdownHotkeyPressed = updatedShutdownHotkeyPressedState
            && !isShutdownHotkeyPressed;

        isShutdownHotkeyPressed = updatedShutdownHotkeyPressedState;

        bool updatedActionModeHotkeyPressedState = isActionModeKeyPressed
            && IsAnyControlKeyPressed()
            && IsAnyAltKeyPressed();
        bool shouldNotifyActionModeHotkeyPressed = updatedActionModeHotkeyPressedState
            && !isActionModeHotkeyPressed;

        isActionModeHotkeyPressed = updatedActionModeHotkeyPressedState;

        bool updatedModelCycleHotkeyPressedState = isModelCycleKeyPressed
            && IsAnyControlKeyPressed()
            && IsAnyAltKeyPressed();
        bool shouldNotifyModelCycleHotkeyPressed = updatedModelCycleHotkeyPressedState
            && !isModelCycleHotkeyPressed;

        isModelCycleHotkeyPressed = updatedModelCycleHotkeyPressedState;

        bool updatedPushToTalkPressedState = isVoiceShortcutEnabled
            && IsAnyControlKeyPressed()
            && IsAnyAltKeyPressed()
            && !isTextPromptHotkeyPressed
            && !isShutdownHotkeyPressed
            && !isActionModeHotkeyPressed
            && !isModelCycleHotkeyPressed;

        if (updatedPushToTalkPressedState != IsPushToTalkPressed)
        {
            IsPushToTalkPressed = updatedPushToTalkPressedState;
            BuddyLog.Workflow(IsPushToTalkPressed
                ? "Push-to-talk shortcut pressed."
                : "Push-to-talk shortcut released.");
            NotifyPushToTalkHotkeyChanged();
        }

        if (shouldNotifyTextPromptHotkeyPressed)
        {
            BuddyLog.Workflow("Typed prompt shortcut pressed.");
            TextPromptHotkeyPressed?.Invoke(this, EventArgs.Empty);
        }

        if (shouldNotifyShutdownHotkeyPressed)
        {
            BuddyLog.Workflow("Shutdown shortcut pressed.");
            ShutdownHotkeyPressed?.Invoke(this, EventArgs.Empty);
        }

        if (shouldNotifyActionModeHotkeyPressed)
        {
            BuddyLog.Workflow("Action mode shortcut pressed (Ctrl+Alt+A).");
            ActionModeHotkeyPressed?.Invoke(this, EventArgs.Empty);
        }

        if (shouldNotifyModelCycleHotkeyPressed)
        {
            BuddyLog.Workflow("Ask model cycle shortcut pressed (Ctrl+Alt+M).");
            ChatModelCycleHotkeyPressed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RemoveReleasedModifierVirtualKeyCode(int virtualKeyCode)
    {
        pressedModifierVirtualKeyCodes.Remove(virtualKeyCode);

        if (virtualKeyCode == LeftControlVirtualKeyCode || virtualKeyCode == RightControlVirtualKeyCode)
        {
            pressedModifierVirtualKeyCodes.Remove(ControlVirtualKeyCode);
        }
        else if (virtualKeyCode == LeftAltVirtualKeyCode || virtualKeyCode == RightAltVirtualKeyCode)
        {
            pressedModifierVirtualKeyCodes.Remove(AltVirtualKeyCode);
        }
    }

    private bool IsAnyControlKeyPressed()
    {
        return pressedModifierVirtualKeyCodes.Contains(ControlVirtualKeyCode)
            || pressedModifierVirtualKeyCodes.Contains(LeftControlVirtualKeyCode)
            || pressedModifierVirtualKeyCodes.Contains(RightControlVirtualKeyCode);
    }

    private bool IsAnyAltKeyPressed()
    {
        return pressedModifierVirtualKeyCodes.Contains(AltVirtualKeyCode)
            || pressedModifierVirtualKeyCodes.Contains(LeftAltVirtualKeyCode)
            || pressedModifierVirtualKeyCodes.Contains(RightAltVirtualKeyCode);
    }

    private void NotifyPushToTalkHotkeyChanged()
    {
        PushToTalkHotkeyChanged?.Invoke(
            this,
            new PushToTalkHotkeyChangedEventArgs(
                IsPushToTalkPressed,
                IsMonitoring,
                MonitoringErrorMessage));
    }

    private static bool IsKeyboardMessageThatChangesKeyState(int messageIdentifierValue)
    {
        return messageIdentifierValue is KeyDownMessageIdentifier
            or KeyUpMessageIdentifier
            or SystemKeyDownMessageIdentifier
            or SystemKeyUpMessageIdentifier;
    }

    private static bool IsKeyDownMessage(int messageIdentifierValue)
    {
        return messageIdentifierValue is KeyDownMessageIdentifier or SystemKeyDownMessageIdentifier;
    }

    private static bool IsTrackedModifierVirtualKeyCode(int virtualKeyCode)
    {
        return IsControlVirtualKeyCode(virtualKeyCode) || IsAltVirtualKeyCode(virtualKeyCode);
    }

    private static bool IsControlVirtualKeyCode(int virtualKeyCode)
    {
        return virtualKeyCode is ControlVirtualKeyCode
            or LeftControlVirtualKeyCode
            or RightControlVirtualKeyCode;
    }

    private static bool IsAltVirtualKeyCode(int virtualKeyCode)
    {
        return virtualKeyCode is AltVirtualKeyCode
            or LeftAltVirtualKeyCode
            or RightAltVirtualKeyCode;
    }

    private static bool IsVirtualKeyCurrentlyPressed(int virtualKeyCode)
    {
        return (GetAsyncKeyState(virtualKeyCode) & 0x8000) != 0;
    }

    private delegate IntPtr LowLevelKeyboardProcedure(
        int keyboardEventCode,
        IntPtr messageIdentifier,
        IntPtr keyboardEventData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookIdentifier,
        LowLevelKeyboardProcedure hookProcedure,
        IntPtr moduleHandle,
        uint threadIdentifier);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hookHandle,
        int keyboardEventCode,
        IntPtr messageIdentifier,
        IntPtr keyboardEventData);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKeyCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
