using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace Buddy.Windows.TextInput;

public partial class TextPromptWindow : Window
{
    private const double WindowScreenMargin = 16;
    private const double CursorHorizontalOffset = 22;
    private const double CursorVerticalOffset = 18;

    public TextPromptWindow()
    {
        InitializeComponent();
    }

    public event EventHandler<TextPromptSubmittedEventArgs>? PromptSubmitted;

    /// <summary>
    /// Reconfigures the prompt window for one of the supported input flows. The standard
    /// flow asks Buddy a question (Ctrl+Alt+Space). The action flow tells Buddy to
    /// actually do something on the desktop via Computer Use (Ctrl+Alt+A).
    /// </summary>
    public void ApplyMode(TextPromptMode promptMode)
    {
        switch (promptMode)
        {
            case TextPromptMode.AskBuddy:
                Title = "Ask Buddy";
                PromptHeaderTextBlock.Text = "Ask Buddy";
                PromptTextBox.Tag = "Ask anything about what's on your screen.";
                break;
            case TextPromptMode.ActOnDesktop:
                Title = "Buddy: act on my desktop";
                PromptHeaderTextBlock.Text = "Buddy: act on my desktop";
                PromptTextBox.Tag = "Describe the task. Buddy will click, type, and scroll for you.";
                break;
        }
    }

    public void ShowNearCurrentCursor()
    {
        PromptTextBox.Clear();
        PositionNearCurrentCursor();

        if (!IsVisible)
        {
            Show();
        }

        Activate();
        PromptTextBox.Focus();
        Keyboard.Focus(PromptTextBox);
    }

    private void HandlePromptTextBoxPreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs keyEventArguments)
    {
        if (keyEventArguments.Key == Key.Escape)
        {
            keyEventArguments.Handled = true;
            Hide();
            return;
        }

        if (keyEventArguments.Key == Key.Enter
            && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            keyEventArguments.Handled = true;
            SubmitPrompt();
        }
    }

    private void HandleSendButtonClick(object sender, RoutedEventArgs routedEventArguments)
    {
        SubmitPrompt();
    }

    private void HandleCancelButtonClick(object sender, RoutedEventArgs routedEventArguments)
    {
        Hide();
    }

    private void SubmitPrompt()
    {
        string trimmedPromptText = PromptTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(trimmedPromptText))
        {
            Hide();
            return;
        }

        Hide();
        PromptSubmitted?.Invoke(this, new TextPromptSubmittedEventArgs(trimmedPromptText));
    }

    private void PositionNearCurrentCursor()
    {
        Forms.Screen currentCursorScreen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        System.Drawing.Rectangle screenWorkingArea = currentCursorScreen.WorkingArea;
        DpiScale windowDpiScale = VisualTreeHelper.GetDpi(this);

        double screenLeft = screenWorkingArea.Left / windowDpiScale.DpiScaleX;
        double screenTop = screenWorkingArea.Top / windowDpiScale.DpiScaleY;
        double screenRight = screenWorkingArea.Right / windowDpiScale.DpiScaleX;
        double screenBottom = screenWorkingArea.Bottom / windowDpiScale.DpiScaleY;
        double cursorX = Forms.Cursor.Position.X / windowDpiScale.DpiScaleX;
        double cursorY = Forms.Cursor.Position.Y / windowDpiScale.DpiScaleY;
        double preferredLeft = cursorX + CursorHorizontalOffset;
        double preferredTop = cursorY + CursorVerticalOffset;

        if (preferredLeft + Width > screenRight - WindowScreenMargin)
        {
            preferredLeft = cursorX - Width - CursorHorizontalOffset;
        }

        if (preferredTop + Height > screenBottom - WindowScreenMargin)
        {
            preferredTop = cursorY - Height - CursorVerticalOffset;
        }

        Left = ClampToScreenRange(
            preferredLeft,
            screenLeft + WindowScreenMargin,
            screenRight - Width - WindowScreenMargin);
        Top = ClampToScreenRange(
            preferredTop,
            screenTop + WindowScreenMargin,
            screenBottom - Height - WindowScreenMargin);
    }

    private static double ClampToScreenRange(double value, double minimumValue, double maximumValue)
    {
        if (maximumValue < minimumValue)
        {
            return minimumValue;
        }

        return Math.Clamp(value, minimumValue, maximumValue);
    }
}
