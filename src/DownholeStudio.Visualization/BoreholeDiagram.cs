using SkiaSharp;

namespace DownholeStudio.Visualization;

public sealed record DiagramReceiver(double Depth, bool HasP, bool HasS, bool Excluded, bool Selected, object? Key);

/// <summary>Everything needed to draw the survey geometry (SI, depths below ground).</summary>
public sealed class BoreholeDiagramModel
{
    public string BoreholeName { get; set; } = "BH-1";
    public double TotalDepth { get; set; } = 30;
    public double CollarHeight { get; set; }
    public double POffset { get; set; } = 2;
    public double PSourceDepth { get; set; }
    public double SOffset { get; set; } = 2;
    public double SSourceDepth { get; set; }
    public List<DiagramReceiver> Receivers { get; } = [];
    public string LengthUnit { get; set; } = "m";
    public double LengthScale { get; set; } = 1;
    public string? Note { get; set; }

    /// <summary>An independent copy for rendering on another thread.</summary>
    public BoreholeDiagramModel Snapshot()
    {
        var copy = new BoreholeDiagramModel
        {
            BoreholeName = BoreholeName, TotalDepth = TotalDepth, CollarHeight = CollarHeight, POffset = POffset, PSourceDepth = PSourceDepth,
            SOffset = SOffset, SSourceDepth = SSourceDepth, LengthUnit = LengthUnit, LengthScale = LengthScale, Note = Note,
        };
        copy.Receivers.AddRange(Receivers);
        return copy;
    }
}

public static class BoreholeDiagramRenderer
{
    /// <summary>Draws a cross-section: ground surface, borehole, P source (left), S source (right), receivers and the ray to the selected receiver.</summary>
    public static PlotLayout Render(SKCanvas canvas, SKRect bounds, BoreholeDiagramModel m, PlotTheme theme, float scale = 1f)
    {
        using var bg = Draw.Fill(theme.Background);
        canvas.DrawRect(bounds, bg);
        var maxDepth = Math.Max(1, Math.Max(m.TotalDepth, m.Receivers.Count > 0 ? m.Receivers.Max(r => r.Depth) : 1)) * 1.05;
        var maxOffset = Math.Max(1, Math.Max(m.POffset, m.SOffset)) * 1.6;
        var model = new XyPlotModel { ShowLegend = false, XAxisOnTop = true };
        model.X.Min = -maxOffset;
        model.X.Max = maxOffset;
        model.X.Title = $"Horizontal distance ({m.LengthUnit})";
        model.X.ShowGrid = false;
        model.X.Format = v => Draw.Number(Math.Abs(v) * m.LengthScale, Axis.NiceStep(maxOffset / 3 * m.LengthScale));
        model.Y.Min = -Math.Max(0.6, maxDepth * 0.06);
        model.Y.Max = maxDepth;
        model.Y.Inverted = true;
        model.Y.Title = $"Depth ({m.LengthUnit})";
        model.Y.Format = v => v < 0 ? "" : Draw.Number(v * m.LengthScale, Axis.NiceStep(maxDepth / 8 * m.LengthScale));
        var layout = XyPlotRenderer.Render(canvas, bounds, model, theme, scale);
        var area = layout.PlotArea;
        canvas.Save();
        canvas.ClipRect(area);

        // Ground.
        var gy = layout.ToPixelY(0);
        using (var soil = Draw.Fill(theme.Grid.WithAlpha(110)))
        {
            canvas.DrawRect(new SKRect(area.Left, gy, area.Right, area.Bottom), soil);
        }

        using (var ground = Draw.Stroke(theme.Text, 1.6f * scale))
        {
            canvas.DrawLine(area.Left, gy, area.Right, gy, ground);
        }

        // Borehole.
        var bx = layout.ToPixelX(0);
        var bw = Math.Max(6 * scale, area.Width * 0.018f);
        var bottom = layout.ToPixelY(m.TotalDepth);
        using (var hole = Draw.Fill(theme.Background))
        using (var edge = Draw.Stroke(theme.Axis, 1.2f * scale))
        {
            var top = layout.ToPixelY(-m.CollarHeight);
            canvas.DrawRect(new SKRect(bx - bw / 2, top, bx + bw / 2, bottom), hole);
            canvas.DrawRect(new SKRect(bx - bw / 2, top, bx + bw / 2, bottom), edge);
        }

        using var font = PlotFonts.Font(10.5f * scale, PlotFonts.Medium);
        Draw.Text(canvas, m.BoreholeName, bx, layout.ToPixelY(-m.CollarHeight) - 6 * scale, font, theme.Text, SKTextAlign.Center);

        // Sources.
        var pSrc = layout.ToPixel(-m.POffset, m.PSourceDepth);
        var sSrc = layout.ToPixel(m.SOffset, m.SSourceDepth);
        XyPlotRenderer.Marker(canvas, new SKPoint(pSrc.X, pSrc.Y - 5 * scale), 6 * scale, MarkerShape.Square, theme.P, false, theme.Background, scale);
        Draw.Text(canvas, "P source", pSrc.X, pSrc.Y - 16 * scale, font, theme.P, SKTextAlign.Center);
        using (var beam = Draw.Fill(theme.S))
        {
            canvas.DrawRect(new SKRect(sSrc.X - 14 * scale, sSrc.Y - 8 * scale, sSrc.X + 14 * scale, sSrc.Y - 2 * scale), beam);
        }

        Draw.Text(canvas, "S source", sSrc.X, sSrc.Y - 16 * scale, font, theme.S, SKTextAlign.Center);

        // Receivers and rays to the selected one.
        foreach (var r in m.Receivers.Where(r => r.Selected))
        {
            var rp = layout.ToPixel(0, r.Depth);
            using var pRay = Draw.Stroke(theme.P.WithAlpha(170), 1.2f * scale, dash: [5 * scale, 4 * scale]);
            using var sRay = Draw.Stroke(theme.S.WithAlpha(170), 1.2f * scale, dash: [5 * scale, 4 * scale]);
            canvas.DrawLine(pSrc.X, pSrc.Y, rp.X, rp.Y, pRay);
            canvas.DrawLine(sSrc.X, sSrc.Y, rp.X, rp.Y, sRay);
        }

        foreach (var r in m.Receivers)
        {
            var c = layout.ToPixel(0, r.Depth);
            var color = r.Excluded ? theme.Excluded : r.HasP && r.HasS ? theme.Text : theme.Warning;
            XyPlotRenderer.Marker(canvas, c, (r.Selected ? 5.5f : 3.5f) * scale, r.HasP && r.HasS ? MarkerShape.Circle : MarkerShape.Triangle, r.Selected ? theme.Selection : color, r.Excluded, theme.Background, scale);
        }

        canvas.Restore();
        if (m.Note is not null)
        {
            using var nf = PlotFonts.Font(10.5f * scale);
            Draw.Text(canvas, m.Note, area.Left + 6 * scale, area.Bottom - 6 * scale, nf, theme.MutedText);
        }

        return layout;
    }
}

public enum ExportFormat
{
    Png,
    Svg,
    Pdf,
}

/// <summary>Writes any renderer to PNG, SVG or PDF at a chosen size.</summary>
public static class ChartExporter
{
    /// <param name="draw">Draws the figure into the given rectangle (logical units) with a text/stroke scale.</param>
    /// <param name="width">Logical width (points for PDF, CSS pixels for SVG, pixels/scale for PNG).</param>
    /// <param name="pixelScale">PNG only: device pixels per logical unit (e.g. 3 for ~300 dpi at 100 px/inch).</param>
    public static byte[] Export(Action<SKCanvas, SKRect> draw, ExportFormat format, float width, float height, float pixelScale = 2f)
    {
        switch (format)
        {
            case ExportFormat.Png:
            {
                var info = new SKImageInfo((int)Math.Ceiling(width * pixelScale), (int)Math.Ceiling(height * pixelScale), SKColorType.Rgba8888, SKAlphaType.Premul);
                using var surface = SKSurface.Create(info);
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);
                canvas.Scale(pixelScale);
                draw(canvas, new SKRect(0, 0, width, height));
                using var image = surface.Snapshot();
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                return data.ToArray();
            }

            case ExportFormat.Svg:
            {
                using var stream = new MemoryStream();
                using (var wstream = new SKManagedWStream(stream))
                using (var canvas = SKSvgCanvas.Create(new SKRect(0, 0, width, height), wstream))
                {
                    draw(canvas, new SKRect(0, 0, width, height));
                }

                return stream.ToArray();
            }

            default:
            {
                using var stream = new MemoryStream();
                using (var wstream = new SKManagedWStream(stream))
                using (var document = SKDocument.CreatePdf(wstream, new SKDocumentPdfMetadata { Creator = "Downhole Studio", Producer = "Downhole Studio" }))
                {
                    var canvas = document.BeginPage(width, height);
                    draw(canvas, new SKRect(0, 0, width, height));
                    document.EndPage();
                    document.Close();
                }

                return stream.ToArray();
            }
        }
    }
}
