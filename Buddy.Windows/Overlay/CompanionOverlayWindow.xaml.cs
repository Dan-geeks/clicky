using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Buddy.Windows.Pointing;
using Forms = System.Windows.Forms;

namespace Buddy.Windows.Overlay;

public partial class CompanionOverlayWindow : Window
{
    private const int ExtendedWindowStyleIndex = -20;
    private const int ToolWindowExtendedStyle = 0x00000080;
    private const int TransparentExtendedStyle = 0x00000020;
    private const int NoActivateExtendedStyle = 0x08000000;
    private const double OverlayScreenMargin = 28;
    private const double StandardCompanionWidth = 320;
    private const double CopyPasteCompanionWidth = 520;
    private const double StandardResponseMaximumHeight = 86;
    private const double CopyPasteResponseMaximumHeight = 260;
    private const double PreferredCursorHorizontalOffset = 28;
    private const double PreferredCursorVerticalOffset = -18;
    private const double FallbackCompanionHeight = 210;
    private const double PointingCursorTipOffsetX = 5;
    private const double PointingCursorTipOffsetY = 2;
    private const double FollowerCursorHorizontalOffset = 28;
    private const double FollowerCursorVerticalOffset = 20;
    private const uint ExcludeFromCaptureDisplayAffinity = 0x00000011;

    // Per-frame lerp factor for the follower cursor. 0.22 at ~60 fps gives a soft trail
    // (cursor catches up in ~5-6 frames) without feeling laggy. macOS uses a SwiftUI
    // spring; this is the WPF-friendly equivalent that doesn't need a physics solver.
    private const double FollowerCursorSmoothingFactor = 0.22;
    // If the cursor is teleported (DPI change, screen swap), we don't want to slowly
    // crawl across the desktop — snap when the gap exceeds this many DIPs.
    private const double FollowerCursorSnapDistanceThreshold = 320;

    // Cursor sprite at rotation 0 has its tip in the upper-left, so its natural pointing
    // vector is roughly (-1,-1). Adding 135° aligns the tip with the motion direction
    // (which atan2 reports as 0° pointing right) so the cursor faces where it is flying.
    private const double PointingCursorRotationAlignmentDegrees = 135.0;
    private const double PointingFlightMinimumDurationSeconds = 0.45;
    private const double PointingFlightMaximumDurationSeconds = 1.05;
    private const double PointingFlightDistancePerSecond = 1100.0;
    private const double PointingFlightArcHeightPerDistance = 0.2;
    private const double PointingFlightMaximumArcHeight = 80.0;
    private const double PointingFlightMaximumScalePulse = 0.28;
    private const double PointingFlightBaseGlowBlurRadius = 14.0;
    private const double PointingFlightMaximumGlowBlurRadius = 36.0;
    private const double PointingFlightBaseGlowOpacity = 0.58;
    private const double PointingFlightMaximumGlowOpacity = 0.92;
    private const double PointingLabelFadeOutDurationMilliseconds = 180.0;
    private const double PointingCursorFadeOutDurationMilliseconds = 220.0;

    private static readonly TimeSpan ThinkingDotsAnimationInterval = TimeSpan.FromMilliseconds(33);
    private const double ThinkingDotsWaveAmplitudePixels = 6.0;
    private const double ThinkingDotsWaveStepRadians = 0.32;
    private static readonly double ThinkingDotsWavePhaseOffsetRadians = 2.0 * Math.PI / 3.0;
    private static readonly System.Windows.Media.Brush DefaultCompanionContentBackground = CreateFrozenSolidColorBrush(0xE6, 0x14, 0x17, 0x1C);
    private static readonly System.Windows.Media.Brush DefaultCompanionContentBorderBrush = CreateFrozenSolidColorBrush(0x66, 0x3F, 0x48, 0x56);
    private static readonly Thickness DefaultCompanionContentBorderThickness = new(1);
    private static readonly Thickness DefaultCompanionContentPadding = new(14);

    private readonly DispatcherTimer thinkingDotsAnimationTimer;
    private readonly Stopwatch animationClock = Stopwatch.StartNew();
    private bool isFrameLoopSubscribed;
    private bool shouldTrackCompanionRoot;
    private bool shouldTrackFollowerCursor;
    private double thinkingDotsAnimationPhase;
    private bool hasFollowerCursorTargetSnapshot;
    private double currentFollowerCursorX;
    private double currentFollowerCursorY;

    // Active pointing-cursor flight (forward to target, or return to real cursor).
    private bool isPointingFlightActive;
    private System.Windows.Point pointingFlightStartPoint;
    private System.Windows.Point pointingFlightControlPoint;
    private System.Windows.Point pointingFlightEndPoint;
    private double pointingFlightDurationSeconds;
    private double pointingFlightStartTimeSeconds;
    private Action? pointingFlightCompletedCallback;

    public CompanionOverlayWindow()
    {
        InitializeComponent();
        thinkingDotsAnimationTimer = new DispatcherTimer
        {
            Interval = ThinkingDotsAnimationInterval
        };
        thinkingDotsAnimationTimer.Tick += HandleThinkingDotsAnimationTimerTick;
        ResizeToVirtualScreen();
    }

    public void Present(CompanionOverlayPresentationState presentationState)
    {
        ResizeToVirtualScreen();
        UpdatePresentationContent(presentationState);

        if (!IsVisible)
        {
            Show();
        }

        ResizeToVirtualScreen();
        UpdateLayout();
        PositionCompanionNearCurrentCursor();
        SetElementOpacityImmediate(PointingCursorRoot, 0);
        SetElementOpacityImmediate(PointingLabelBorder, 1);
        SetElementOpacityImmediate(FollowerCursorRoot, 0);
        AbortPointingFlight();
        ResetPointingCursorVisualState();
        shouldTrackFollowerCursor = false;
        shouldTrackCompanionRoot = true;
        EnsureFrameLoopSubscribed();
        SetElementOpacityImmediate(CompanionRoot, 1);
    }

    public void PresentFollowerCursor()
    {
        ResizeToVirtualScreen();
        StopThinkingDotsAnimation();

        if (!IsVisible)
        {
            Show();
        }

        SetElementOpacityImmediate(CompanionRoot, 0);
        SetElementOpacityImmediate(PointingCursorRoot, 0);
        SetElementOpacityImmediate(PointingLabelBorder, 1);
        AbortPointingFlight();
        ResetPointingCursorVisualState();
        shouldTrackCompanionRoot = false;
        shouldTrackFollowerCursor = true;
        // Reset the smoothing snapshot so the follower appears instantly at the cursor
        // instead of lerping in from wherever the previous session left it.
        hasFollowerCursorTargetSnapshot = false;
        PositionFollowerCursorNearCurrentCursor();
        EnsureFrameLoopSubscribed();
        SetElementOpacityImmediate(FollowerCursorRoot, 1);
    }

    public void Dismiss()
    {
        StopThinkingDotsAnimation();
        SetElementOpacityImmediate(CompanionRoot, 0);
        SetElementOpacityImmediate(FollowerCursorRoot, 0);
        ClearPointingCursor();
        StopFrameLoop();

        if (IsVisible)
        {
            Hide();
        }
    }

    public void PointToInstruction(
        PointingInstruction pointingInstruction,
        string instructionText,
        bool shouldAnimatePointingInstruction)
    {
        System.Windows.Point? targetCursorPosition = CalculatePointingCursorPosition(pointingInstruction);

        if (targetCursorPosition is null)
        {
            ClearPointingCursor();
            return;
        }

        ResizeToVirtualScreen();
        StopThinkingDotsAnimation();
        SetElementOpacityImmediate(FollowerCursorRoot, 0);
        SetElementOpacityImmediate(PointingLabelBorder, 1);
        shouldTrackCompanionRoot = false;
        shouldTrackFollowerCursor = false;
        PointingLabelTextBlock.Text = string.IsNullOrWhiteSpace(instructionText)
            ? (string.IsNullOrWhiteSpace(pointingInstruction.Label) ? "Here" : pointingInstruction.Label)
            : instructionText;
        PointingLabelBorder.ToolTip = string.IsNullOrWhiteSpace(instructionText)
            ? (string.IsNullOrWhiteSpace(pointingInstruction.Label) ? "Here" : pointingInstruction.Label)
            : instructionText;

        if (shouldAnimatePointingInstruction)
        {
            if (PointingCursorRoot.Opacity <= 0)
            {
                System.Windows.Point currentCursorPosition = CalculateCurrentCursorPosition();
                SetPointingCursorPosition(currentCursorPosition);
            }

            BeginPointingCursorBezierFlight(
                CurrentPointingCursorPosition(),
                targetCursorPosition.Value,
                onCompleted: null);
        }
        else
        {
            AbortPointingFlight();
            ResetPointingCursorVisualState();
            SetPointingCursorPosition(targetCursorPosition.Value);
        }

        if (!IsVisible)
        {
            Show();
        }

        SetElementOpacityImmediate(CompanionRoot, 0);
        SetElementOpacityImmediate(PointingCursorRoot, 1);
        EnsureFrameLoopSubscribed();
    }

    public void ClearPointingCursor()
    {
        ClearPointingCursor(animateReturnToCursor: false, onCompleted: null);
    }

    /// <summary>
    /// Clears the pointing cursor. When <paramref name="animateReturnToCursor"/> is true,
    /// the bubble label fades out, the cursor flies back along a Bezier arc to the real
    /// cursor position, and then the pointing cursor is hidden. <paramref name="onCompleted"/>
    /// fires after the flight (or immediately when animation is skipped). Mirrors the macOS
    /// "fly back and resume following" behavior so the workflow does not feel like the
    /// cursor just teleports away after each step.
    /// </summary>
    public void ClearPointingCursor(bool animateReturnToCursor, Action? onCompleted)
    {
        if (!animateReturnToCursor || PointingCursorRoot.Opacity <= 0 || !IsVisible)
        {
            AbortPointingFlight();
            ResetPointingCursorVisualState();
            SetElementOpacityImmediate(PointingCursorRoot, 0);
            PointingLabelTextBlock.Text = "";
            onCompleted?.Invoke();
            return;
        }

        FadePointingLabelOut();

        System.Windows.Point startPoint = CurrentPointingCursorPosition();
        System.Windows.Point returnTargetPoint = CalculateCurrentCursorPosition();

        BeginPointingCursorBezierFlight(
            startPoint,
            returnTargetPoint,
            onCompleted: () =>
            {
                FadePointingCursorRootOut(() =>
                {
                    PointingLabelTextBlock.Text = "";
                    ResetPointingCursorVisualState();
                    onCompleted?.Invoke();
                });
            });
    }

    public void UpdatePointingInstructionText(string instructionText)
    {
        if (PointingCursorRoot.Opacity <= 0)
        {
            return;
        }

        PointingLabelTextBlock.Text = instructionText;
        PointingLabelBorder.ToolTip = instructionText;
    }

    protected override void OnClosed(EventArgs eventArguments)
    {
        StopFrameLoop();
        thinkingDotsAnimationTimer.Stop();
        thinkingDotsAnimationTimer.Tick -= HandleThinkingDotsAnimationTimerTick;
        base.OnClosed(eventArguments);
    }

    protected override void OnSourceInitialized(EventArgs eventArguments)
    {
        base.OnSourceInitialized(eventArguments);
        ApplyClickThroughOverlayWindowStyles();
    }

    private void UpdatePresentationContent(CompanionOverlayPresentationState presentationState)
    {
        bool isThinking = presentationState.ShouldShowThinkingAnimation;

        CompanionRoot.Width = presentationState.ShouldUseCopyPasteLayout
            ? CopyPasteCompanionWidth
            : StandardCompanionWidth;
        ResponseTextBlock.MaxHeight = presentationState.ShouldUseCopyPasteLayout
            ? CopyPasteResponseMaximumHeight
            : StandardResponseMaximumHeight;

        ApplyCompanionContentChrome(isThinking);

        StatusTextBlock.Text = presentationState.StatusText;
        DetailTextBlock.Text = presentationState.DetailText;
        TranscriptTextBlock.Text = presentationState.TranscriptText;
        ResponseTextBlock.Text = presentationState.ResponseText;
        StatusTextBlock.Visibility = isThinking
            ? Visibility.Collapsed
            : Visibility.Visible;
        ThinkingDotsPanel.Visibility = isThinking
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailTextBlock.Visibility = !isThinking && !string.IsNullOrWhiteSpace(presentationState.DetailText)
            ? Visibility.Visible
            : Visibility.Collapsed;
        AudioLevelTrack.Visibility = !isThinking && presentationState.ShouldShowAudioLevel
            ? Visibility.Visible
            : Visibility.Collapsed;
        TranscriptTextBlock.Visibility = !isThinking && presentationState.ShouldShowTranscript
            ? Visibility.Visible
            : Visibility.Collapsed;
        ResponseTextBlock.Visibility = !isThinking && presentationState.ShouldShowResponse
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (isThinking)
        {
            StartThinkingDotsAnimation();
        }
        else
        {
            StopThinkingDotsAnimation();
        }

        double clampedAudioLevel = Math.Clamp(presentationState.AudioLevel, 0, 1);
        AudioLevelFill.Width = AudioLevelTrack.Width * clampedAudioLevel;
    }

    private void ApplyCompanionContentChrome(bool isThinking)
    {
        if (isThinking)
        {
            CursorDecoration.Visibility = Visibility.Collapsed;
            CompanionContentBorder.Background = System.Windows.Media.Brushes.Transparent;
            CompanionContentBorder.BorderBrush = System.Windows.Media.Brushes.Transparent;
            CompanionContentBorder.BorderThickness = new Thickness(0);
            CompanionContentBorder.Padding = new Thickness(0);
            CompanionContentBorder.Effect = null;
        }
        else
        {
            CursorDecoration.Visibility = Visibility.Visible;
            CompanionContentBorder.Background = DefaultCompanionContentBackground;
            CompanionContentBorder.BorderBrush = DefaultCompanionContentBorderBrush;
            CompanionContentBorder.BorderThickness = DefaultCompanionContentBorderThickness;
            CompanionContentBorder.Padding = DefaultCompanionContentPadding;
            if (CompanionContentBorder.Effect is not DropShadowEffect)
            {
                CompanionContentBorder.Effect = new DropShadowEffect
                {
                    BlurRadius = 24,
                    Color = System.Windows.Media.Colors.Black,
                    Opacity = 0.38,
                    ShadowDepth = 8,
                };
            }
        }
    }

    private static SolidColorBrush CreateFrozenSolidColorBrush(byte alpha, byte red, byte green, byte blue)
    {
        SolidColorBrush brush = new(System.Windows.Media.Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    private void ResizeToVirtualScreen()
    {
        System.Drawing.Rectangle virtualScreenBounds = Forms.SystemInformation.VirtualScreen;
        DpiScale overlayDpiScale = VisualTreeHelper.GetDpi(this);

        Left = virtualScreenBounds.Left / overlayDpiScale.DpiScaleX;
        Top = virtualScreenBounds.Top / overlayDpiScale.DpiScaleY;
        Width = virtualScreenBounds.Width / overlayDpiScale.DpiScaleX;
        Height = virtualScreenBounds.Height / overlayDpiScale.DpiScaleY;
    }

    private void PositionCompanionNearCurrentCursor()
    {
        System.Drawing.Rectangle virtualScreenBounds = Forms.SystemInformation.VirtualScreen;
        Forms.Screen currentCursorScreen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        System.Drawing.Rectangle currentScreenWorkingArea = currentCursorScreen.WorkingArea;
        DpiScale overlayDpiScale = VisualTreeHelper.GetDpi(this);

        double cursorX = (Forms.Cursor.Position.X - virtualScreenBounds.Left) / overlayDpiScale.DpiScaleX;
        double cursorY = (Forms.Cursor.Position.Y - virtualScreenBounds.Top) / overlayDpiScale.DpiScaleY;
        double screenLeft = (currentScreenWorkingArea.Left - virtualScreenBounds.Left) / overlayDpiScale.DpiScaleX;
        double screenTop = (currentScreenWorkingArea.Top - virtualScreenBounds.Top) / overlayDpiScale.DpiScaleY;
        double screenRight = (currentScreenWorkingArea.Right - virtualScreenBounds.Left) / overlayDpiScale.DpiScaleX;
        double screenBottom = (currentScreenWorkingArea.Bottom - virtualScreenBounds.Top) / overlayDpiScale.DpiScaleY;

        double companionWidth = CompanionRoot.ActualWidth > 0
            ? CompanionRoot.ActualWidth
            : CompanionRoot.Width;
        double companionHeight = CompanionRoot.ActualHeight > 0
            ? CompanionRoot.ActualHeight
            : FallbackCompanionHeight;

        double preferredLeft = cursorX + PreferredCursorHorizontalOffset;
        double preferredTop = cursorY + PreferredCursorVerticalOffset;

        if (preferredLeft + companionWidth > screenRight - OverlayScreenMargin)
        {
            preferredLeft = cursorX - companionWidth - PreferredCursorHorizontalOffset;
        }

        double companionLeft = ClampToScreenRange(
            preferredLeft,
            screenLeft + OverlayScreenMargin,
            screenRight - companionWidth - OverlayScreenMargin);
        double companionTop = ClampToScreenRange(
            preferredTop,
            screenTop + OverlayScreenMargin,
            screenBottom - companionHeight - OverlayScreenMargin);

        CompanionRootTranslate.X = companionLeft;
        CompanionRootTranslate.Y = companionTop;
    }

    private void PositionFollowerCursorNearCurrentCursor()
    {
        System.Drawing.Rectangle virtualScreenBounds = Forms.SystemInformation.VirtualScreen;
        Forms.Screen currentCursorScreen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        System.Drawing.Rectangle currentScreenWorkingArea = currentCursorScreen.WorkingArea;
        DpiScale overlayDpiScale = VisualTreeHelper.GetDpi(this);

        double cursorX = (Forms.Cursor.Position.X - virtualScreenBounds.Left) / overlayDpiScale.DpiScaleX;
        double cursorY = (Forms.Cursor.Position.Y - virtualScreenBounds.Top) / overlayDpiScale.DpiScaleY;
        double screenLeft = (currentScreenWorkingArea.Left - virtualScreenBounds.Left) / overlayDpiScale.DpiScaleX;
        double screenTop = (currentScreenWorkingArea.Top - virtualScreenBounds.Top) / overlayDpiScale.DpiScaleY;
        double screenRight = (currentScreenWorkingArea.Right - virtualScreenBounds.Left) / overlayDpiScale.DpiScaleX;
        double screenBottom = (currentScreenWorkingArea.Bottom - virtualScreenBounds.Top) / overlayDpiScale.DpiScaleY;
        double followerWidth = FollowerCursorRoot.ActualWidth > 0
            ? FollowerCursorRoot.ActualWidth
            : FollowerCursorRoot.Width;
        double followerHeight = FollowerCursorRoot.ActualHeight > 0
            ? FollowerCursorRoot.ActualHeight
            : FollowerCursorRoot.Height;
        double followerTargetLeft = ClampToScreenRange(
            cursorX + FollowerCursorHorizontalOffset,
            screenLeft + OverlayScreenMargin,
            screenRight - followerWidth - OverlayScreenMargin);
        double followerTargetTop = ClampToScreenRange(
            cursorY + FollowerCursorVerticalOffset,
            screenTop + OverlayScreenMargin,
            screenBottom - followerHeight - OverlayScreenMargin);

        if (!hasFollowerCursorTargetSnapshot)
        {
            // First frame after PresentFollowerCursor: snap so the cursor doesn't crawl
            // toward the user from the previous session's resting position.
            currentFollowerCursorX = followerTargetLeft;
            currentFollowerCursorY = followerTargetTop;
            hasFollowerCursorTargetSnapshot = true;
        }
        else
        {
            double horizontalGap = followerTargetLeft - currentFollowerCursorX;
            double verticalGap = followerTargetTop - currentFollowerCursorY;
            double gapDistance = Math.Sqrt(horizontalGap * horizontalGap + verticalGap * verticalGap);

            if (gapDistance > FollowerCursorSnapDistanceThreshold)
            {
                // Big jump (DPI change, monitor swap, app teleport): no point lerping —
                // snap so the follower stays close to the user's actual mouse pointer.
                currentFollowerCursorX = followerTargetLeft;
                currentFollowerCursorY = followerTargetTop;
            }
            else
            {
                currentFollowerCursorX += horizontalGap * FollowerCursorSmoothingFactor;
                currentFollowerCursorY += verticalGap * FollowerCursorSmoothingFactor;
            }
        }

        FollowerCursorTranslate.X = currentFollowerCursorX;
        FollowerCursorTranslate.Y = currentFollowerCursorY;
    }

    private System.Windows.Point? CalculatePointingCursorPosition(PointingInstruction pointingInstruction)
    {
        Forms.Screen[] allScreens = Forms.Screen.AllScreens;
        Forms.Screen targetScreen;

        if (pointingInstruction.ScreenNumber <= 0)
        {
            // Auto-resolve: parser left the screen unspecified, so target whichever screen
            // the user's real cursor is on right now (matches macOS's lenient behavior).
            targetScreen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        }
        else
        {
            int screenIndex = pointingInstruction.ScreenNumber - 1;

            if (screenIndex < 0 || screenIndex >= allScreens.Length)
            {
                return null;
            }

            targetScreen = allScreens[screenIndex];
        }

        System.Drawing.Rectangle virtualScreenBounds = Forms.SystemInformation.VirtualScreen;
        System.Drawing.Rectangle targetScreenBounds = targetScreen.Bounds;
        DpiScale overlayDpiScale = VisualTreeHelper.GetDpi(this);
        double maximumXInScreenPixels = Math.Max(0, targetScreenBounds.Width - 1);
        double maximumYInScreenPixels = Math.Max(0, targetScreenBounds.Height - 1);
        double clampedXInScreenPixels = Math.Clamp(
            pointingInstruction.XInScreenPixels,
            0,
            maximumXInScreenPixels);
        double clampedYInScreenPixels = Math.Clamp(
            pointingInstruction.YInScreenPixels,
            0,
            maximumYInScreenPixels);
        double xInVirtualScreenPixels = targetScreenBounds.Left + clampedXInScreenPixels;
        double yInVirtualScreenPixels = targetScreenBounds.Top + clampedYInScreenPixels;

        return new System.Windows.Point(
            ((xInVirtualScreenPixels - virtualScreenBounds.Left) / overlayDpiScale.DpiScaleX)
            - PointingCursorTipOffsetX,
            ((yInVirtualScreenPixels - virtualScreenBounds.Top) / overlayDpiScale.DpiScaleY)
            - PointingCursorTipOffsetY);
    }

    private System.Windows.Point CalculateCurrentCursorPosition()
    {
        System.Drawing.Rectangle virtualScreenBounds = Forms.SystemInformation.VirtualScreen;
        DpiScale overlayDpiScale = VisualTreeHelper.GetDpi(this);

        return new System.Windows.Point(
            ((Forms.Cursor.Position.X - virtualScreenBounds.Left) / overlayDpiScale.DpiScaleX)
            - PointingCursorTipOffsetX,
            ((Forms.Cursor.Position.Y - virtualScreenBounds.Top) / overlayDpiScale.DpiScaleY)
            - PointingCursorTipOffsetY);
    }

    private System.Windows.Point CurrentPointingCursorPosition()
    {
        return new System.Windows.Point(PointingCursorTranslate.X, PointingCursorTranslate.Y);
    }

    private void SetPointingCursorPosition(System.Windows.Point cursorPosition)
    {
        PointingCursorTranslate.X = cursorPosition.X;
        PointingCursorTranslate.Y = cursorPosition.Y;
    }

    private void BeginPointingCursorBezierFlight(
        System.Windows.Point startPoint,
        System.Windows.Point endPoint,
        Action? onCompleted)
    {
        AbortPointingFlight();

        double deltaX = endPoint.X - startPoint.X;
        double deltaY = endPoint.Y - startPoint.Y;
        double distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);

        // Flight duration scales with distance: short hops are quick, long flights more dramatic.
        pointingFlightDurationSeconds = Math.Clamp(
            distance / PointingFlightDistancePerSecond,
            PointingFlightMinimumDurationSeconds,
            PointingFlightMaximumDurationSeconds);

        // Quadratic Bezier control point — midpoint shifted upward (negative Y in screen coords)
        // so the cursor swoops along a parabolic arc instead of a straight line.
        double midpointX = (startPoint.X + endPoint.X) / 2.0;
        double midpointY = (startPoint.Y + endPoint.Y) / 2.0;
        double arcHeight = Math.Min(distance * PointingFlightArcHeightPerDistance, PointingFlightMaximumArcHeight);

        pointingFlightStartPoint = startPoint;
        pointingFlightEndPoint = endPoint;
        pointingFlightControlPoint = new System.Windows.Point(midpointX, midpointY - arcHeight);
        pointingFlightStartTimeSeconds = animationClock.Elapsed.TotalSeconds;
        pointingFlightCompletedCallback = onCompleted;
        isPointingFlightActive = true;

        SetPointingCursorPosition(startPoint);
        UpdatePointingFlightFrame(progressFromZeroToOne: 0);
        EnsureFrameLoopSubscribed();
    }

    private void AdvancePointingFlight()
    {
        if (!isPointingFlightActive)
        {
            return;
        }

        double elapsedSeconds = animationClock.Elapsed.TotalSeconds - pointingFlightStartTimeSeconds;
        double linearProgress = pointingFlightDurationSeconds <= 0
            ? 1.0
            : Math.Clamp(elapsedSeconds / pointingFlightDurationSeconds, 0, 1);

        UpdatePointingFlightFrame(linearProgress);

        if (linearProgress >= 1.0)
        {
            CompletePointingFlight();
        }
    }

    private void UpdatePointingFlightFrame(double progressFromZeroToOne)
    {
        // Smoothstep easing (Hermite): 3t² - 2t³ for ease-in-out without an easing object.
        double easedProgress =
            progressFromZeroToOne * progressFromZeroToOne * (3.0 - 2.0 * progressFromZeroToOne);
        double oneMinusEasedProgress = 1.0 - easedProgress;

        double bezierX = oneMinusEasedProgress * oneMinusEasedProgress * pointingFlightStartPoint.X
            + 2.0 * oneMinusEasedProgress * easedProgress * pointingFlightControlPoint.X
            + easedProgress * easedProgress * pointingFlightEndPoint.X;
        double bezierY = oneMinusEasedProgress * oneMinusEasedProgress * pointingFlightStartPoint.Y
            + 2.0 * oneMinusEasedProgress * easedProgress * pointingFlightControlPoint.Y
            + easedProgress * easedProgress * pointingFlightEndPoint.Y;
        SetPointingCursorPosition(new System.Windows.Point(bezierX, bezierY));

        // Tangent of the quadratic Bezier: B'(t) = 2(1-t)(P1-P0) + 2t(P2-P1).
        double tangentX = 2.0 * oneMinusEasedProgress * (pointingFlightControlPoint.X - pointingFlightStartPoint.X)
            + 2.0 * easedProgress * (pointingFlightEndPoint.X - pointingFlightControlPoint.X);
        double tangentY = 2.0 * oneMinusEasedProgress * (pointingFlightControlPoint.Y - pointingFlightStartPoint.Y)
            + 2.0 * easedProgress * (pointingFlightEndPoint.Y - pointingFlightControlPoint.Y);

        if (Math.Abs(tangentX) > 0.0001 || Math.Abs(tangentY) > 0.0001)
        {
            double motionAngleDegrees = Math.Atan2(tangentY, tangentX) * (180.0 / Math.PI);
            PointingCursorRotate.Angle = motionAngleDegrees + PointingCursorRotationAlignmentDegrees;
        }

        // Sin pulse peaks at the apex of the flight, growing the cursor and intensifying
        // the glow so the motion reads as a deliberate "swoop" rather than a slide.
        double pulse = Math.Sin(progressFromZeroToOne * Math.PI);
        double scale = 1.0 + pulse * PointingFlightMaximumScalePulse;
        PointingCursorScale.ScaleX = scale;
        PointingCursorScale.ScaleY = scale;
        PointingCursorGlowEffect.BlurRadius = PointingFlightBaseGlowBlurRadius
            + pulse * (PointingFlightMaximumGlowBlurRadius - PointingFlightBaseGlowBlurRadius);
        PointingCursorGlowEffect.Opacity = PointingFlightBaseGlowOpacity
            + pulse * (PointingFlightMaximumGlowOpacity - PointingFlightBaseGlowOpacity);
    }

    private void CompletePointingFlight()
    {
        Action? completionCallback = pointingFlightCompletedCallback;
        isPointingFlightActive = false;
        pointingFlightCompletedCallback = null;
        ResetPointingCursorVisualState();
        completionCallback?.Invoke();
    }

    private void AbortPointingFlight()
    {
        isPointingFlightActive = false;
        pointingFlightCompletedCallback = null;
    }

    private void ResetPointingCursorVisualState()
    {
        PointingCursorScale.ScaleX = 1;
        PointingCursorScale.ScaleY = 1;
        PointingCursorRotate.Angle = 0;
        PointingCursorGlowEffect.BlurRadius = PointingFlightBaseGlowBlurRadius;
        PointingCursorGlowEffect.Opacity = PointingFlightBaseGlowOpacity;
    }

    private void FadePointingLabelOut()
    {
        DoubleAnimation fadeAnimation = new()
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(PointingLabelFadeOutDurationMilliseconds)),
            FillBehavior = FillBehavior.HoldEnd
        };

        PointingLabelBorder.BeginAnimation(UIElement.OpacityProperty, fadeAnimation);
    }

    private void FadePointingCursorRootOut(Action onCompleted)
    {
        DoubleAnimation fadeAnimation = new()
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(PointingCursorFadeOutDurationMilliseconds)),
            FillBehavior = FillBehavior.HoldEnd
        };
        fadeAnimation.Completed += (_, _) =>
        {
            PointingCursorRoot.BeginAnimation(UIElement.OpacityProperty, null);
            PointingCursorRoot.Opacity = 0;
            PointingLabelBorder.BeginAnimation(UIElement.OpacityProperty, null);
            PointingLabelBorder.Opacity = 1;
            onCompleted();
        };

        PointingCursorRoot.BeginAnimation(UIElement.OpacityProperty, fadeAnimation);
    }

    private static void SetElementOpacityImmediate(UIElement element, double opacity)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = opacity;
    }

    private static double ClampToScreenRange(double value, double minimumValue, double maximumValue)
    {
        if (maximumValue < minimumValue)
        {
            return minimumValue;
        }

        return Math.Clamp(value, minimumValue, maximumValue);
    }

    private void HandleFrameLoopTick(object? sender, EventArgs eventArguments)
    {
        ResizeToVirtualScreen();

        if (shouldTrackCompanionRoot)
        {
            PositionCompanionNearCurrentCursor();
        }

        if (shouldTrackFollowerCursor)
        {
            PositionFollowerCursorNearCurrentCursor();
        }

        if (isPointingFlightActive)
        {
            AdvancePointingFlight();
        }

        if (!shouldTrackCompanionRoot
            && !shouldTrackFollowerCursor
            && !isPointingFlightActive)
        {
            StopFrameLoop();
        }
    }

    private void HandleThinkingDotsAnimationTimerTick(object? sender, EventArgs eventArguments)
    {
        thinkingDotsAnimationPhase += ThinkingDotsWaveStepRadians;
        if (thinkingDotsAnimationPhase > 2 * Math.PI)
        {
            thinkingDotsAnimationPhase -= 2 * Math.PI;
        }
        UpdateThinkingDotsAnimationFrame();
    }

    /// <summary>
    /// Subscribes to <see cref="CompositionTarget.Rendering"/> for a frame-synced (~60 fps on
    /// most displays, higher on 120 Hz panels) update loop. Beats a 33 ms <see cref="DispatcherTimer"/>
    /// because we tick at the compositor's rate and avoid layout thrash from setting Canvas.Left/Top.
    /// </summary>
    private void EnsureFrameLoopSubscribed()
    {
        if (isFrameLoopSubscribed)
        {
            return;
        }

        CompositionTarget.Rendering += HandleFrameLoopTick;
        isFrameLoopSubscribed = true;
    }

    private void StopFrameLoop()
    {
        shouldTrackCompanionRoot = false;
        shouldTrackFollowerCursor = false;

        if (!isFrameLoopSubscribed)
        {
            return;
        }

        CompositionTarget.Rendering -= HandleFrameLoopTick;
        isFrameLoopSubscribed = false;
    }

    private void StartThinkingDotsAnimation()
    {
        if (!thinkingDotsAnimationTimer.IsEnabled)
        {
            thinkingDotsAnimationPhase = 0;
            UpdateThinkingDotsAnimationFrame();
            thinkingDotsAnimationTimer.Start();
        }
    }

    private void StopThinkingDotsAnimation()
    {
        if (thinkingDotsAnimationTimer.IsEnabled)
        {
            thinkingDotsAnimationTimer.Stop();
        }

        thinkingDotsAnimationPhase = 0;
        ThinkingDotOneTransform.Y = 0;
        ThinkingDotTwoTransform.Y = 0;
        ThinkingDotThreeTransform.Y = 0;
        ThinkingDotOne.Opacity = 1.0;
        ThinkingDotTwo.Opacity = 1.0;
        ThinkingDotThree.Opacity = 1.0;
    }

    private void UpdateThinkingDotsAnimationFrame()
    {
        ThinkingDotOneTransform.Y =
            -Math.Sin(thinkingDotsAnimationPhase) * ThinkingDotsWaveAmplitudePixels;
        ThinkingDotTwoTransform.Y =
            -Math.Sin(thinkingDotsAnimationPhase - ThinkingDotsWavePhaseOffsetRadians) * ThinkingDotsWaveAmplitudePixels;
        ThinkingDotThreeTransform.Y =
            -Math.Sin(thinkingDotsAnimationPhase - 2 * ThinkingDotsWavePhaseOffsetRadians) * ThinkingDotsWaveAmplitudePixels;
        ThinkingDotOne.Opacity = 1.0;
        ThinkingDotTwo.Opacity = 1.0;
        ThinkingDotThree.Opacity = 1.0;
    }

    private void ApplyClickThroughOverlayWindowStyles()
    {
        IntPtr windowHandle = new WindowInteropHelper(this).Handle;
        IntPtr existingExtendedWindowStyle = GetExtendedWindowStyle(windowHandle);
        IntPtr updatedExtendedWindowStyle = new(
            existingExtendedWindowStyle.ToInt64()
            | ToolWindowExtendedStyle
            | TransparentExtendedStyle
            | NoActivateExtendedStyle);

        SetExtendedWindowStyle(windowHandle, updatedExtendedWindowStyle);

        // Keep Buddy's own chrome out of desktop screenshots so the AI sees the user's app.
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
