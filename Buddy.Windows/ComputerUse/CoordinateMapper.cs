using System;
using System.Drawing;
using Forms = System.Windows.Forms;

namespace Buddy.Windows.ComputerUse;

/// <summary>
/// The Gemini Computer Use API does not know the user's actual screen resolution. It
/// returns coordinates on a normalized 1000 × 1000 grid (0..999) relative to whatever
/// screenshot was provided. This helper translates those normalized coordinates back to
/// real Windows virtual-desktop pixels so the input simulator can click the right spot.
/// </summary>
public static class CoordinateMapper
{
    private const double GeminiNormalizedCoordinateRange = 1000.0;

    /// <summary>
    /// Maps a normalized (0..999) coordinate inside the screenshot of a specific Windows
    /// display back to virtual-desktop pixel space, ready to feed into the input simulator.
    /// </summary>
    public static (int VirtualDesktopPixelX, int VirtualDesktopPixelY) DenormalizeToVirtualDesktopPixel(
        double normalizedXInScreenshot,
        double normalizedYInScreenshot,
        Rectangle screenshotScreenBounds)
    {
        double clampedNormalizedX = Math.Clamp(normalizedXInScreenshot, 0, GeminiNormalizedCoordinateRange - 1);
        double clampedNormalizedY = Math.Clamp(normalizedYInScreenshot, 0, GeminiNormalizedCoordinateRange - 1);
        double pixelXWithinScreen = clampedNormalizedX / GeminiNormalizedCoordinateRange * screenshotScreenBounds.Width;
        double pixelYWithinScreen = clampedNormalizedY / GeminiNormalizedCoordinateRange * screenshotScreenBounds.Height;
        int virtualDesktopPixelX = screenshotScreenBounds.Left + (int)Math.Round(pixelXWithinScreen);
        int virtualDesktopPixelY = screenshotScreenBounds.Top + (int)Math.Round(pixelYWithinScreen);

        return (virtualDesktopPixelX, virtualDesktopPixelY);
    }

    /// <summary>
    /// Convenience overload when only a screen number (1-based) is known. Falls back to
    /// the cursor's current screen if the requested screen index is out of range, mirroring
    /// the lenient behavior of the existing pointing parser.
    /// </summary>
    public static (int VirtualDesktopPixelX, int VirtualDesktopPixelY) DenormalizeToVirtualDesktopPixel(
        double normalizedXInScreenshot,
        double normalizedYInScreenshot,
        int screenNumber)
    {
        Forms.Screen[] allScreens = Forms.Screen.AllScreens;
        int screenIndex = screenNumber - 1;
        Forms.Screen targetScreen = screenIndex >= 0 && screenIndex < allScreens.Length
            ? allScreens[screenIndex]
            : Forms.Screen.FromPoint(Forms.Cursor.Position);

        return DenormalizeToVirtualDesktopPixel(
            normalizedXInScreenshot,
            normalizedYInScreenshot,
            targetScreen.Bounds);
    }
}
