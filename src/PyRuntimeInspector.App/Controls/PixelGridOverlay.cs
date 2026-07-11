using System.Windows;
using System.Windows.Media;

namespace PyRuntimeInspector.App.Controls;

public sealed class PixelGridOverlay : FrameworkElement
{
    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(PixelGridOverlay), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Zoom
    {
        get => (double)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (Zoom < 8 || ActualWidth / Zoom > 2048 || ActualHeight / Zoom > 2048)
            return;
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(75, 255, 255, 255)), 1);
        pen.Freeze();
        for (var x = Zoom; x < ActualWidth; x += Zoom)
            drawingContext.DrawLine(pen, new Point(x, 0), new Point(x, ActualHeight));
        for (var y = Zoom; y < ActualHeight; y += Zoom)
            drawingContext.DrawLine(pen, new Point(0, y), new Point(ActualWidth, y));
    }
}
