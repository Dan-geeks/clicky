using System;
using System.IO;

namespace Buddy.Windows.Diagnostics;

public static class BuddyLog
{
    private const int DefaultMaximumLoggedMessageLength = 4000;
    private static readonly object LogFileLock = new();

    public static string LogFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Buddy.Windows",
        "Logs",
        "buddy.log");

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Workflow(string message)
    {
        Write("FLOW", message);
    }

    public static void Error(string message)
    {
        Write("ERROR", message);
    }

    public static void Error(string message, Exception exception)
    {
        Write("ERROR", $"{message}: {exception}");
    }

    public static string TrimForLog(string? message, int maximumLength = DefaultMaximumLoggedMessageLength)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "";
        }

        string trimmedMessage = message.Trim();

        return trimmedMessage.Length <= maximumLength
            ? trimmedMessage
            : trimmedMessage[..maximumLength] + "...";
    }

    public static string DescribeTextForLog(string? text, int maximumPreviewLength = 160)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "0 chars";
        }

        string normalizedText = string.Join(
            " ",
            text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        string previewText = normalizedText.Length <= maximumPreviewLength
            ? normalizedText
            : normalizedText[..maximumPreviewLength] + "...";

        return $"{text.Length} chars; preview=\"{previewText}\"";
    }

    private static void Write(string level, string message)
    {
        try
        {
            string logDirectoryPath = Path.GetDirectoryName(LogFilePath) ?? "";

            if (!string.IsNullOrWhiteSpace(logDirectoryPath))
            {
                Directory.CreateDirectory(logDirectoryPath);
            }

            string logLine = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0:O} [{1}] {2}{3}",
                DateTimeOffset.UtcNow,
                level,
                message,
                Environment.NewLine);

            lock (LogFileLock)
            {
                File.AppendAllText(LogFilePath, logLine);
            }
        }
        catch
        {
        }
    }
}
