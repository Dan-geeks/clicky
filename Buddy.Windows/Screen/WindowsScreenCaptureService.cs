using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Buddy.Windows.Diagnostics;
using Forms = System.Windows.Forms;

namespace Buddy.Windows.Screen;

public sealed class WindowsScreenCaptureService
{
    public const long DefaultJpegQuality = 85L;
    private const int MouseInputType = 0;
    private const int MouseWheelEventFlag = 0x0800;
    private const int ScrollContextDownWheelDelta = -720;
    private static readonly TimeSpan ScrollContextSettleDelay = TimeSpan.FromMilliseconds(350);

    public Task<IReadOnlyList<WindowsScreenCapture>> CaptureAllScreensAsJpegAsync(
        CancellationToken cancellationToken,
        bool captureCursorScreenOnly = false,
        long jpegQuality = DefaultJpegQuality)
    {
        return CaptureSelectedScreensAsJpegAsync(
            cancellationToken,
            captureCursorScreenOnly,
            jpegQuality,
            "live Windows desktop viewport",
            canUseForPointing: true);
    }

    public async Task<IReadOnlyList<WindowsScreenCapture>> CaptureAllScreensWithScrollContextAsJpegAsync(
        CancellationToken cancellationToken,
        bool captureCursorScreenOnly = false,
        long jpegQuality = DefaultJpegQuality,
        bool includeScrollContext = false)
    {
        List<WindowsScreenCapture> screenCaptures = new(await CaptureAllScreensAsJpegAsync(
            cancellationToken,
            captureCursorScreenOnly,
            jpegQuality));

        if (!includeScrollContext || captureCursorScreenOnly)
        {
            return screenCaptures;
        }

        Point currentCursorPosition = Forms.Cursor.Position;
        Forms.Screen[] allScreens = Forms.Screen.AllScreens;
        bool cursorIsOnKnownScreen = allScreens.Any(screen => screen.Bounds.Contains(currentCursorPosition));

        if (!cursorIsOnKnownScreen)
        {
            BuddyLog.Workflow("Scroll context capture skipped because the cursor is not on a known display.");
            return screenCaptures;
        }

        bool didSendScrollDownInput = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            BuddyLog.Workflow(
                $"Scroll context capture starting. WheelDelta={ScrollContextDownWheelDelta}; Cursor={currentCursorPosition.X},{currentCursorPosition.Y}.");
            didSendScrollDownInput = TrySendMouseWheelInput(ScrollContextDownWheelDelta);

            if (!didSendScrollDownInput)
            {
                BuddyLog.Workflow("Scroll context capture skipped because Windows did not accept the scroll input.");
                return screenCaptures;
            }

            await Task.Delay(ScrollContextSettleDelay, cancellationToken);

            IReadOnlyList<WindowsScreenCapture> scrolledScreenCaptures =
                await CaptureSelectedScreensAsJpegAsync(
                    cancellationToken,
                    captureCursorScreenOnly: true,
                    jpegQuality,
                    "context-only viewport after Buddy temporarily scrolled down near the cursor",
                    canUseForPointing: false);

            screenCaptures.AddRange(scrolledScreenCaptures);
            BuddyLog.Workflow(
                $"Scroll context capture completed. AddedCaptures={scrolledScreenCaptures.Count}; TotalCaptures={screenCaptures.Count}.");
            return screenCaptures;
        }
        finally
        {
            if (didSendScrollDownInput)
            {
                bool didSendRestoreInput = TrySendMouseWheelInput(-ScrollContextDownWheelDelta);
                BuddyLog.Workflow(didSendRestoreInput
                    ? "Scroll context capture restored the previous scroll direction."
                    : "Scroll context capture could not send the restore scroll input.");
                await Task.Delay(ScrollContextSettleDelay);
            }
        }
    }

    private Task<IReadOnlyList<WindowsScreenCapture>> CaptureSelectedScreensAsJpegAsync(
        CancellationToken cancellationToken,
        bool captureCursorScreenOnly,
        long jpegQuality,
        string captureContextDescription,
        bool canUseForPointing)
    {
        return Task.Run<IReadOnlyList<WindowsScreenCapture>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<WindowsScreenCapture> screenCaptures = new();
            Forms.Screen[] allScreens = Forms.Screen.AllScreens;
            Point currentCursorPosition = Forms.Cursor.Position;
            bool cursorIsOnKnownScreen = allScreens.Any(screen =>
                screen.Bounds.Contains(currentCursorPosition));

            BuddyLog.Workflow(
                $"Screen capture starting. Screens={allScreens.Length}; CursorScreenOnly={captureCursorScreenOnly}; JpegQuality={jpegQuality}; Context=\"{captureContextDescription}\".");

            for (int screenIndex = 0; screenIndex < allScreens.Length; screenIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Forms.Screen currentScreen = allScreens[screenIndex];
                Rectangle currentScreenBounds = currentScreen.Bounds;
                int screenNumber = screenIndex + 1;
                bool isCursorScreen = currentScreenBounds.Contains(currentCursorPosition);
                bool shouldCaptureCurrentScreen =
                    !captureCursorScreenOnly
                    || isCursorScreen
                    || (!cursorIsOnKnownScreen && currentScreen.Primary);

                if (!shouldCaptureCurrentScreen)
                {
                    continue;
                }

                using Bitmap screenBitmap = new(
                    currentScreenBounds.Width,
                    currentScreenBounds.Height,
                    PixelFormat.Format24bppRgb);
                using Graphics screenGraphics = Graphics.FromImage(screenBitmap);

                screenGraphics.CopyFromScreen(
                    currentScreenBounds.Left,
                    currentScreenBounds.Top,
                    0,
                    0,
                    currentScreenBounds.Size,
                    CopyPixelOperation.SourceCopy);

                using MemoryStream screenImageStream = new();
                SaveBitmapAsJpeg(screenBitmap, screenImageStream, jpegQuality);

                string screenLabel = CreateScreenLabel(
                    screenNumber,
                    currentScreen.Primary,
                    isCursorScreen,
                    currentScreenBounds.Width,
                    currentScreenBounds.Height,
                    currentScreenBounds.Left,
                    currentScreenBounds.Top,
                    captureContextDescription,
                    canUseForPointing);

                screenCaptures.Add(new WindowsScreenCapture(
                    screenImageStream.ToArray(),
                    screenLabel,
                    screenNumber,
                    currentScreenBounds.Width,
                    currentScreenBounds.Height,
                    currentScreenBounds.Left,
                    currentScreenBounds.Top,
                    isCursorScreen,
                    currentScreen.Primary));
            }

            BuddyLog.Workflow($"Screen capture completed. CapturedScreens={screenCaptures.Count}.");
            return screenCaptures;
        }, cancellationToken);
    }

    private static void SaveBitmapAsJpeg(
        Bitmap bitmap,
        Stream destinationStream,
        long jpegQuality)
    {
        ImageCodecInfo? jpegCodec = ImageCodecInfo
            .GetImageEncoders()
            .FirstOrDefault(imageCodecInfo => imageCodecInfo.FormatID == ImageFormat.Jpeg.Guid);

        if (jpegCodec is null)
        {
            bitmap.Save(destinationStream, ImageFormat.Jpeg);
            return;
        }

        using EncoderParameters encoderParameters = new(1);
        encoderParameters.Param[0] = new EncoderParameter(
            System.Drawing.Imaging.Encoder.Quality,
            Math.Clamp(jpegQuality, 1L, 100L));
        bitmap.Save(destinationStream, jpegCodec, encoderParameters);
    }

    private static string CreateScreenLabel(
        int screenNumber,
        bool isPrimary,
        bool isCursorScreen,
        int widthInPixels,
        int heightInPixels,
        int boundsLeftInPixels,
        int boundsTopInPixels,
        string captureContextDescription,
        bool canUseForPointing)
    {
        string primarySuffix = isPrimary ? " primary" : "";
        string cursorSuffix = isCursorScreen ? " cursor is here, primary focus" : "";
        string pointingGuidance = canUseForPointing
            ? "This is the live viewport; POINT coordinates may use this screenshot."
            : "This is context-only scroll capture; Buddy restored the original scroll position, so do not use this screenshot for POINT coordinates.";

        return string.Format(
            CultureInfo.InvariantCulture,
            "Screen {0}{1}{2}: {3}, {4} x {5} pixels, virtual origin {6},{7}. {8}",
            screenNumber,
            primarySuffix,
            cursorSuffix,
            captureContextDescription,
            widthInPixels,
            heightInPixels,
            boundsLeftInPixels,
            boundsTopInPixels,
            pointingGuidance);
    }

    private static bool TrySendMouseWheelInput(int wheelDelta)
    {
        Input[] inputs =
        [
            new()
            {
                Type = MouseInputType,
                Data = new InputUnion
                {
                    MouseInput = new MouseInput
                    {
                        MouseData = wheelDelta,
                        Flags = MouseWheelEventFlag
                    }
                }
            }
        ];

        uint sentInputCount = SendInput(
            (uint)inputs.Length,
            inputs,
            Marshal.SizeOf<Input>());

        if (sentInputCount == inputs.Length)
        {
            return true;
        }

        int win32ErrorCode = Marshal.GetLastWin32Error();
        BuddyLog.Error($"SendInput mouse wheel failed ({win32ErrorCode}).");
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint inputCount,
        Input[] inputs,
        int inputSize);

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
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;

        public int Y;

        public int MouseData;

        public int Flags;

        public int Time;

        public IntPtr ExtraInfo;
    }
}
