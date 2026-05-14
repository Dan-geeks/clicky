using System;
using System.Threading;
using System.Threading.Tasks;
using Buddy.Windows.Diagnostics;
using Buddy.Windows.Input;
using WpfClipboard = System.Windows.Clipboard;

namespace Buddy.Windows.Screen;

/// <summary>
/// Grabs the actual text content of whatever window is focused by sending Ctrl+A then
/// Ctrl+C, reading the clipboard, and restoring the user's previous clipboard contents.
/// Lets the AI see the verbatim text of an article, document, or page when a screenshot
/// alone is too small or too low-resolution to read every word.
/// </summary>
public sealed class WindowsClipboardTextCaptureService
{
    private static readonly TimeSpan AfterClearClipboardSettleDelay = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan AfterSelectAllSettleDelay = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan AfterCopySettleDelay = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan ClipboardRestoreSettleDelay = TimeSpan.FromMilliseconds(60);
    private const int MaximumCapturedCharacters = 12000;

    public async Task<string?> CaptureFocusedWindowTextAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ClipboardSnapshot originalClipboard = ReadClipboardSnapshot();
        bool didChangeClipboard = false;

        try
        {
            ClearClipboardOnUiThread();
            didChangeClipboard = true;
            await Task.Delay(AfterClearClipboardSettleDelay, cancellationToken);

            await WindowsInputSimulator.PressKeyComboAsync("ctrl+a");
            await Task.Delay(AfterSelectAllSettleDelay, cancellationToken);

            await WindowsInputSimulator.PressKeyComboAsync("ctrl+c");
            await Task.Delay(AfterCopySettleDelay, cancellationToken);

            string? capturedText = ReadClipboardTextOnUiThread();

            if (string.IsNullOrWhiteSpace(capturedText))
            {
                BuddyLog.Workflow(
                    "Text context capture: focused window did not produce selectable text via Ctrl+A/Ctrl+C.");
                return null;
            }

            string trimmedCapturedText = capturedText.Length > MaximumCapturedCharacters
                ? capturedText[..MaximumCapturedCharacters] + "\n\n[truncated by Buddy after the first 12000 characters]"
                : capturedText;

            BuddyLog.Workflow(
                $"Text context capture: collected {trimmedCapturedText.Length} characters from focused window.");
            return trimmedCapturedText;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception captureException)
        {
            BuddyLog.Error("Text context capture failed", captureException);
            return null;
        }
        finally
        {
            if (didChangeClipboard)
            {
                RestoreClipboardOnUiThread(originalClipboard);

                try
                {
                    await Task.Delay(ClipboardRestoreSettleDelay, CancellationToken.None);
                }
                catch (Exception)
                {
                    // Ignored — restoring the clipboard is best-effort.
                }
            }
        }
    }

    private static ClipboardSnapshot ReadClipboardSnapshot()
    {
        ClipboardSnapshot snapshot = new(null, false);
        InvokeOnUiThread(() =>
        {
            try
            {
                if (WpfClipboard.ContainsText())
                {
                    snapshot = new ClipboardSnapshot(WpfClipboard.GetText(), true);
                }
            }
            catch (Exception readException)
            {
                BuddyLog.Workflow(
                    $"Text context capture: failed to read original clipboard ({readException.Message}).");
            }
        });

        return snapshot;
    }

    private static string? ReadClipboardTextOnUiThread()
    {
        string? capturedText = null;
        InvokeOnUiThread(() =>
        {
            try
            {
                if (WpfClipboard.ContainsText())
                {
                    capturedText = WpfClipboard.GetText();
                }
            }
            catch (Exception readException)
            {
                BuddyLog.Workflow(
                    $"Text context capture: clipboard read after Ctrl+C failed ({readException.Message}).");
            }
        });

        return capturedText;
    }

    private static void ClearClipboardOnUiThread()
    {
        InvokeOnUiThread(() =>
        {
            try
            {
                WpfClipboard.Clear();
            }
            catch (Exception clearException)
            {
                BuddyLog.Workflow(
                    $"Text context capture: clipboard clear failed ({clearException.Message}).");
            }
        });
    }

    private static void RestoreClipboardOnUiThread(ClipboardSnapshot snapshotToRestore)
    {
        InvokeOnUiThread(() =>
        {
            try
            {
                if (snapshotToRestore.HadText && snapshotToRestore.Text is not null)
                {
                    WpfClipboard.SetText(snapshotToRestore.Text);
                }
                else
                {
                    WpfClipboard.Clear();
                }
            }
            catch (Exception restoreException)
            {
                BuddyLog.Workflow(
                    $"Text context capture: clipboard restore failed ({restoreException.Message}).");
            }
        });
    }

    private static void InvokeOnUiThread(Action uiAction)
    {
        System.Windows.Application application = System.Windows.Application.Current;

        if (application is null)
        {
            uiAction();
            return;
        }

        application.Dispatcher.Invoke(uiAction);
    }

    private readonly record struct ClipboardSnapshot(string? Text, bool HadText);
}
