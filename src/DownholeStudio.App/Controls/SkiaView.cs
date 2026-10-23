using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using DownholeStudio.Visualization;
using SkiaSharp;

namespace DownholeStudio.App.Controls;

/// <summary>
/// Hosts a SkiaSharp renderer from DownholeStudio.Visualization. The same drawing code produces
/// the PNG/SVG/PDF exports, so what users see is exactly what they export.
/// </summary>
public abstract class SkiaView : Control
{
    protected SkiaView()
    {
        ClipToBounds = true;
        Focusable = true;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    protected PlotTheme CurrentPlotTheme => ActualThemeVariant == ThemeVariant.Dark ? PlotTheme.Dark : PlotTheme.Light;

    /// <summary>
    /// Called on the UI thread whenever the control is redrawn. Capture everything the drawing needs
    /// (model snapshots, flags, hover text) and return a painter that uses only that captured state:
    /// on desktop platforms the painter runs on the render thread, where control properties and view
    /// models must not be touched.
    /// </summary>
    protected abstract Action<SKCanvas, SKRect, PlotTheme> CreatePainter();

    public override void Render(DrawingContext context)
    {
        var theme = CurrentPlotTheme;
        var paint = CreatePainter();
        context.Custom(new DrawOperation(new Rect(Bounds.Size), (c, r) => paint(c, r, theme)));
    }

    private sealed class DrawOperation(Rect bounds, Action<SKCanvas, SKRect> draw) : ICustomDrawOperation
    {
        public Rect Bounds { get; } = bounds;

        public void Dispose()
        {
        }

        public bool Equals(ICustomDrawOperation? other) => false;

        public bool HitTest(Point p) => Bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature is null)
            {
                return;
            }

            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            canvas.Save();
            try
            {
                draw(canvas, new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height));
            }
            finally
            {
                canvas.Restore();
            }
        }
    }
}
