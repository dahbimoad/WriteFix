using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WriteFix.Models;
using WriteFix.Services.Correction;
using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;

namespace WriteFix.Views;

/// <summary>
/// The suggestion card. A normal active topmost window, which is simpler and more
/// reliable than a focus-free overlay (ARCHITECTURE.md §6); the coordinator returns
/// focus to the original app when the user accepts.
/// </summary>
public partial class ResultWindow : Window
{
    private (int X, int Y) _anchor;
    private bool _finished;

    public ResultWindow()
    {
        InitializeComponent();
        Opacity = 0;

        Loaded += OnLoaded;
        Deactivated += OnDeactivated;
    }

    public event Action? AcceptRequested;
    public event Action? RegenerateRequested;
    public event Action? CopyRequested;
    public event Action? Cancelled;

    /// <summary>Screen point, in physical pixels, that the card should appear beside.</summary>
    public void SetAnchor(int x, int y) => _anchor = (x, y);

    // ---- States ------------------------------------------------------------

    public void ShowWorking(CaptureMode mode)
    {
        ModeChipText.Text = mode == CaptureMode.Selection ? "selection" : "whole message";
        HintText.Text = "Esc cancel";

        WorkingPanel.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Collapsed;

        AcceptButton.IsEnabled = false;
        RegenerateButton.IsEnabled = false;
        CopyButton.IsEnabled = false;

        StartPulse();
    }

    public void ShowResult(string original, string corrected, bool canReplace, string notice)
    {
        StopPulse();

        WorkingPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;

        RenderDiff(original, corrected);

        NoticeText.Text = notice;
        NoticeText.Visibility = string.IsNullOrEmpty(notice) ? Visibility.Collapsed : Visibility.Visible;

        AcceptButton.IsEnabled = canReplace;
        RegenerateButton.IsEnabled = true;
        CopyButton.IsEnabled = true;

        HintText.Text = canReplace ? "↵ accept · Esc cancel" : "Esc cancel";

        // Keeps Enter working after the pointer has been over another button.
        if (canReplace) AcceptButton.Focus();
    }

    public void ShowError(string message)
    {
        StopPulse();

        WorkingPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorText.Text = message;

        AcceptButton.IsEnabled = false;
        RegenerateButton.IsEnabled = true;
        CopyButton.IsEnabled = false;
        HintText.Text = "Esc close";
    }

    /// <summary>Feedback for Copy without closing the card straight away.</summary>
    public void FlashCopied()
    {
        NoticeText.Text = "Copied to clipboard.";
        NoticeText.Visibility = Visibility.Visible;
    }

    private void RenderDiff(string original, string corrected)
    {
        ResultText.Inlines.Clear();

        foreach (var segment in DiffHighlighter.Build(original, corrected))
        {
            var run = new Run(segment.Text);

            if (segment.IsChanged)
            {
                run.Background = (Brush)FindResource("HighlightBg");
                run.Foreground = (Brush)FindResource("HighlightInk");
                run.FontWeight = FontWeights.SemiBold;
            }

            ResultText.Inlines.Add(run);
        }
    }

    // ---- Placement ---------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionCard();

        // Fading in after placement avoids the card visibly jumping into position.
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110)));
        Activate();
    }

    private void PositionCard()
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null) return;

        // Anchor and screen bounds are physical pixels; WPF positions in DIPs.
        var toDip = source.CompositionTarget.TransformFromDevice;

        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(_anchor.X, _anchor.Y));
        var work = screen.WorkingArea;

        var anchorDip = toDip.Transform(new Point(_anchor.X, _anchor.Y));
        var workTopLeft = toDip.Transform(new Point(work.Left, work.Top));
        var workBottomRight = toDip.Transform(new Point(work.Right, work.Bottom));

        // Sit just below and slightly left of the caret, so the card does not cover
        // the line being written.
        var left = anchorDip.X - 24;
        var top = anchorDip.Y + 14;

        if (left + ActualWidth > workBottomRight.X) left = workBottomRight.X - ActualWidth;
        if (left < workTopLeft.X) left = workTopLeft.X;

        // Flip above the caret when there is no room below.
        if (top + ActualHeight > workBottomRight.Y)
        {
            var above = anchorDip.Y - ActualHeight - 22;
            top = above >= workTopLeft.Y ? above : workBottomRight.Y - ActualHeight;
        }

        if (top < workTopLeft.Y) top = workTopLeft.Y;

        Left = left;
        Top = top;
    }

    // ---- Working indicator -------------------------------------------------

    private void StartPulse()
    {
        var animation = new DoubleAnimation(1.0, 0.25, TimeSpan.FromMilliseconds(620))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };

        Pulse.BeginAnimation(OpacityProperty, animation);
    }

    private void StopPulse() => Pulse.BeginAnimation(OpacityProperty, null);

    // ---- Commands ----------------------------------------------------------

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (_finished) return;
        _finished = true;
        AcceptRequested?.Invoke();
    }

    private void OnRegenerate(object sender, RoutedEventArgs e) => RegenerateRequested?.Invoke();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (_finished) return;
        _finished = true;
        CopyRequested?.Invoke();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Finish();

    /// <summary>Clicking away dismisses the card, leaving the original untouched (FR-17).</summary>
    private void OnDeactivated(object? sender, EventArgs e) => Finish();

    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        Cancelled?.Invoke();
    }

    /// <summary>Closes without raising Cancelled — used once an action has been taken.</summary>
    public void CloseQuietly()
    {
        _finished = true;
        Deactivated -= OnDeactivated;
        StopPulse();
        Close();
    }
}
