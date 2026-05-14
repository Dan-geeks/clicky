using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Buddy.Windows.Diagnostics;
using Forms = System.Windows.Forms;
using WpfClipboard = System.Windows.Clipboard;

namespace Buddy.Windows.Overlay;

public partial class CopyResponseButtonWindow : Window
{
    private const int ExtendedWindowStyleIndex = -20;
    private const int ToolWindowExtendedStyle = 0x00000080;
    private const int NoActivateExtendedStyle = 0x08000000;
    private const uint ExcludeFromCaptureDisplayAffinity = 0x00000011;
    private const double TopRightHorizontalMargin = 24;
    private const double TopRightVerticalMargin = 24;
    private static readonly TimeSpan CopiedConfirmationDuration = TimeSpan.FromSeconds(1.6);

    private readonly DispatcherTimer copiedConfirmationTimer;
    private string responseTextToCopy = "";

    public CopyResponseButtonWindow()
    {
        InitializeComponent();
        copiedConfirmationTimer = new DispatcherTimer
        {
            Interval = CopiedConfirmationDuration
        };
        copiedConfirmationTimer.Tick += HandleCopiedConfirmationTimerTick;
    }

    public void ShowForResponse(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            HideButton();
            return;
        }

        responseTextToCopy = responseText;

        if (!IsVisible)
        {
            Show();
        }

        PositionAtCursorScreenTopRight();
    }

    public void HideButton()
    {
        if (IsVisible)
        {
            Hide();
        }

        copiedConfirmationTimer.Stop();
        CopyButtonLabel.Text = "Copy";
        responseTextToCopy = "";
    }

    protected override void OnSourceInitialized(EventArgs eventArguments)
    {
        base.OnSourceInitialized(eventArguments);
        ApplyToolWindowStyles();
    }

    protected override void OnClosed(EventArgs eventArguments)
    {
        copiedConfirmationTimer.Stop();
        copiedConfirmationTimer.Tick -= HandleCopiedConfirmationTimerTick;
        base.OnClosed(eventArguments);
    }

    private void HandleCopyButtonClick(object sender, RoutedEventArgs eventArguments)
    {
        if (string.IsNullOrWhiteSpace(responseTextToCopy))
        {
            return;
        }

        try
        {
            WpfClipboard.SetText(responseTextToCopy);
            BuddyLog.Workflow($"Copy button copied {responseTextToCopy.Length} characters to clipboard.");
        }
        catch (Exception copyException)
        {
            BuddyLog.Error("Copy button failed to write clipboard", copyException);
            return;
        }

        CopyButtonLabel.Text = "Copied";
        copiedConfirmationTimer.Stop();
        copiedConfirmationTimer.Start();
    }

    private void HandleCopiedConfirmationTimerTick(object? sender, EventArgs eventArguments)
    {
        copiedConfirmationTimer.Stop();
        CopyButtonLabel.Text = "Copy";
    }

    private void PositionAtCursorScreenTopRight()
    {
        Forms.Screen cursorScreen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        System.Drawing.Rectangle workingArea = cursorScreen.WorkingArea;
        DpiScale dpiScale = VisualTreeHelper.GetDpi(this);
        double widthInDips = ActualWidth > 0 ? ActualWidth : Width;
        double heightInDips = ActualHeight > 0 ? ActualHeight : Height;

        if (widthInDips <= 0)
        {
            UpdateLayout();
            widthInDips = ActualWidth;
            heightInDips = ActualHeight;
        }

        double leftInDips = (workingArea.Right / dpiScale.DpiScaleX) - widthInDips - TopRightHorizontalMargin;
        double topInDips = (workingArea.Top / dpiScale.DpiScaleY) + TopRightVerticalMargin;

        Left = leftInDips;
        Top = topInDips;
    }

    private void ApplyToolWindowStyles()
    {
        IntPtr windowHandle = new WindowInteropHelper(this).Handle;
        IntPtr existingExtendedWindowStyle = GetExtendedWindowStyle(windowHandle);
        IntPtr updatedExtendedWindowStyle = new(
            existingExtendedWindowStyle.ToInt64()
            | ToolWindowExtendedStyle
            | NoActivateExtendedStyle);

        SetExtendedWindowStyle(windowHandle, updatedExtendedWindowStyle);
        _ = SetWindowDisplayAffinity(windowHandle, ExcludeFromCaptureDisplayAffinity);
    }

    private static IntPtr GetExtendedWindowStyle(IntPtr windowHandle)
    {
        if (IntPtr.Size == 8)
        {
            return GetWindowLongPtr64(windowHandle, ExtendedWindowStyleIndex);
        }

        return new IntPtr(GetWindowLong32(windowHandle, ExtendedWindowStyleIndex));
    }

    private static void SetExtendedWindowStyle(IntPtr windowHandle, IntPtr updatedExtendedWindowStyle)
    {
        if (IntPtr.Size == 8)
        {
            _ = SetWindowLongPtr64(windowHandle, ExtendedWindowStyleIndex, updatedExtendedWindowStyle);
            return;
        }

        _ = SetWindowLong32(windowHandle, ExtendedWindowStyleIndex, updatedExtendedWindowStyle.ToInt32());
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr windowHandle, int index, int updatedValue);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr windowHandle, int index, IntPtr updatedValue);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr windowHandle, uint displayAffinity);
}
