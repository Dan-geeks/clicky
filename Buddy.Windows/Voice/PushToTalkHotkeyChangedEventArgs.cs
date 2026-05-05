using System;

namespace Buddy.Windows.Voice;

public sealed class PushToTalkHotkeyChangedEventArgs : EventArgs
{
    public PushToTalkHotkeyChangedEventArgs(
        bool isPushToTalkPressed,
        bool isMonitoring,
        string? monitoringErrorMessage)
    {
        IsPushToTalkPressed = isPushToTalkPressed;
        IsMonitoring = isMonitoring;
        MonitoringErrorMessage = monitoringErrorMessage;
    }

    public bool IsPushToTalkPressed { get; }

    public bool IsMonitoring { get; }

    public string? MonitoringErrorMessage { get; }
}
