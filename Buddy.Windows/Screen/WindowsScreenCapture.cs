namespace Buddy.Windows.Screen;

public sealed class WindowsScreenCapture
{
    public WindowsScreenCapture(
        byte[] imageBytes,
        string label,
        int screenNumber,
        int widthInPixels,
        int heightInPixels,
        int boundsLeftInPixels,
        int boundsTopInPixels,
        bool isCursorScreen,
        bool isPrimary)
    {
        ImageBytes = imageBytes;
        Label = label;
        ScreenNumber = screenNumber;
        WidthInPixels = widthInPixels;
        HeightInPixels = heightInPixels;
        BoundsLeftInPixels = boundsLeftInPixels;
        BoundsTopInPixels = boundsTopInPixels;
        IsCursorScreen = isCursorScreen;
        IsPrimary = isPrimary;
    }

    public byte[] ImageBytes { get; }

    public string Label { get; }

    public int ScreenNumber { get; }

    public int WidthInPixels { get; }

    public int HeightInPixels { get; }

    public int BoundsLeftInPixels { get; }

    public int BoundsTopInPixels { get; }

    public bool IsCursorScreen { get; }

    public bool IsPrimary { get; }
}
