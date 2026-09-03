using System.Windows;
using System.Windows.Media;

namespace JenkinsTray.Controls;

/// <summary>
/// A ring filled clockwise from twelve o'clock, used on the dashboard cards to show a coverage
/// percentage. WPF has no such control: <c>ProgressRing</c> only spins, and a <c>ProgressBar</c>
/// retemplated into a ring needs the same arc geometry anyway. Drawing it directly keeps the whole
/// thing to one element and no bindings beyond the value.
/// The caption that sits in the middle is not drawn here — it is a TextBlock laid over the gauge,
/// so it picks up the card font and theme like every other piece of text.
/// </summary>
public sealed class CircularGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(CircularGauge),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(CircularGauge),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(CircularGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ProgressBrushProperty = DependencyProperty.Register(
        nameof(ProgressBrush), typeof(Brush), typeof(CircularGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(CircularGauge),
        new FrameworkPropertyMetadata(5.85d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Where the ring stands, between zero and <see cref="Maximum"/>.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>The full ring, drawn under the progress arc.</summary>
    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush? ProgressBrush
    {
        get => (Brush?)GetValue(ProgressBrushProperty);
        set => SetValue(ProgressBrushProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var thickness = StrokeThickness;
        var radius = (Math.Min(RenderSize.Width, RenderSize.Height) - thickness) / 2;
        if (radius <= 0)
            return;

        var centre = new Point(RenderSize.Width / 2, RenderSize.Height / 2);

        if (TrackBrush is not null)
            drawingContext.DrawEllipse(null, new Pen(TrackBrush, thickness), centre, radius, radius);

        if (ProgressBrush is null || Maximum <= 0)
            return;

        var fraction = Math.Clamp(Value / Maximum, 0, 1);
        if (fraction <= 0)
            return;

        var pen = new Pen(ProgressBrush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };

        // A closed arc would leave a seam where its two round caps meet, so a full ring is an ellipse.
        if (fraction >= 1)
        {
            drawingContext.DrawEllipse(null, pen, centre, radius, radius);
            return;
        }

        var angle = fraction * 2 * Math.PI;
        var start = new Point(centre.X, centre.Y - radius);
        var end = new Point(centre.X + radius * Math.Sin(angle), centre.Y - radius * Math.Cos(angle));

        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(
            end,
            new Size(radius, radius),
            rotationAngle: 0,
            isLargeArc: fraction > 0.5,
            sweepDirection: SweepDirection.Clockwise,
            isStroked: true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();

        drawingContext.DrawGeometry(null, pen, geometry);
    }
}
