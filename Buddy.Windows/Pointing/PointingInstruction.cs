namespace Buddy.Windows.Pointing;

public sealed class PointingInstruction
{
    public PointingInstruction(
        double xInScreenPixels,
        double yInScreenPixels,
        string label,
        int screenNumber)
    {
        XInScreenPixels = xInScreenPixels;
        YInScreenPixels = yInScreenPixels;
        Label = label;
        ScreenNumber = screenNumber;
    }

    public double XInScreenPixels { get; }

    public double YInScreenPixels { get; }

    public string Label { get; }

    public int ScreenNumber { get; }
}
