using DownholeStudio.Domain.Units;
using DownholeStudio.Visualization;
using SkiaSharp;

namespace DownholeStudio.Reporting.Pdf;

public enum PaperSize
{
    Letter,
    A4,
}

public sealed record PageStyle
{
    public PaperSize Paper { get; init; } = PaperSize.Letter;
    public float Margin { get; init; } = 54; // 0.75 in
    public SKColor Accent { get; init; } = SKColor.Parse("#E5531A");
    public SKColor Ink { get; init; } = SKColor.Parse("#1B1F27");
    public SKColor Muted { get; init; } = SKColor.Parse("#5E6675");
    public SKColor Rule { get; init; } = SKColor.Parse("#D9DDE4");
    public SKColor Shade { get; init; } = SKColor.Parse("#F3F4F7");

    public SKSize Size => Paper == PaperSize.A4 ? new SKSize(595.28f, 841.89f) : new SKSize(612, 792);
}

/// <summary>Running header/footer content.</summary>
public sealed record PageChrome(string Left, string Right, string FooterLeft, string FooterCenter);

/// <summary>
/// A flowing page layout on top of a canvas factory. The same code runs twice: a counting pass
/// (to know the total page count for "Page n of N") and the rendering pass to the PDF document.
/// </summary>
public sealed class PdfLayout
{
    private readonly Func<SKCanvas> _newPage;
    private readonly Action _endPage;
    private readonly PageStyle _style;
    private readonly PageChrome _chrome;
    private readonly int _totalPages;
    private SKCanvas? _canvas;
    private float _y;
    private bool _onCover;

    public PdfLayout(Func<SKCanvas> newPage, Action endPage, PageStyle style, PageChrome chrome, int totalPages)
    {
        _newPage = newPage;
        _endPage = endPage;
        _style = style;
        _chrome = chrome;
        _totalPages = totalPages;
    }

    public int PageCount { get; private set; }

    public PageStyle Style => _style;

    public float ContentWidth => _style.Size.Width - 2 * _style.Margin;

    private float Bottom => _style.Size.Height - _style.Margin - 22;

    private float Left => _style.Margin;

    public SKCanvas Canvas => _canvas ?? throw new InvalidOperationException("No page has been started.");

    public float Y => _y;

    /// <summary>Starts a cover page (no header/footer).</summary>
    public SKCanvas BeginCover()
    {
        Finish();
        _canvas = _newPage();
        PageCount++;
        _onCover = true;
        _y = _style.Margin;
        return _canvas;
    }

    public void NewPage()
    {
        Finish();
        _canvas = _newPage();
        PageCount++;
        _onCover = false;
        _y = _style.Margin + 26;
        DrawChrome();
    }

    public void Finish()
    {
        if (_canvas is not null)
        {
            if (!_onCover)
            {
                DrawFooter();
            }

            _endPage();
            _canvas = null;
        }
    }

    private void Ensure(float height)
    {
        if (_canvas is null || _onCover || _y + height > Bottom)
        {
            NewPage();
        }
    }

    public void Space(float h)
    {
        _y += h;
    }

    public void Heading(string text, int level = 1)
    {
        var size = level == 1 ? 17f : level == 2 ? 12.5f : 10.5f;
        Ensure(size * 3.2f);
        if (level == 1 && _y > _style.Margin + 40)
        {
            _y += 10;
        }

        using var font = PlotFonts.Font(size, level == 1 ? PlotFonts.SemiBold : PlotFonts.SemiBold);
        _y += size * 1.25f;
        Draw.Text(Canvas, text, Left, _y, font, level == 1 ? _style.Ink : _style.Ink);
        if (level == 1)
        {
            using var rule = Draw.Stroke(_style.Accent, 1.6f);
            Canvas.DrawLine(Left, _y + 6, Left + 36, _y + 6, rule);
            _y += 16;
        }
        else
        {
            _y += 7;
        }
    }

    /// <summary>Word-wrapped paragraph. Lines starting with "• " are indented bullets.</summary>
    public void Paragraph(string text, float size = 9.5f, bool muted = false, bool bold = false, bool italic = false)
    {
        using var font = PlotFonts.Font(size, bold ? PlotFonts.SemiBold : italic ? PlotFonts.Italic : PlotFonts.Regular);
        var lineH = size * 1.42f;
        foreach (var para in text.Replace("\r\n", "\n").Split('\n'))
        {
            var bullet = para.StartsWith("• ", StringComparison.Ordinal);
            var indent = bullet ? 12f : 0f;
            var lines = Wrap(bullet ? para[2..] : para, font, ContentWidth - indent);
            for (var i = 0; i < lines.Count; i++)
            {
                Ensure(lineH);
                _y += lineH;
                if (bullet && i == 0)
                {
                    Draw.Text(Canvas, "•", Left + 2, _y, font, _style.Accent);
                }

                Draw.Text(Canvas, lines[i], Left + indent, _y, font, muted ? _style.Muted : _style.Ink);
            }

            _y += size * 0.45f;
        }
    }

    /// <summary>Two-column label/value list.</summary>
    public void KeyValues(IEnumerable<(string Key, string? Value)> rows, float keyWidth = 160)
    {
        using var kf = PlotFonts.Font(9f, PlotFonts.Medium);
        using var vf = PlotFonts.Font(9f);
        foreach (var (k, v) in rows)
        {
            var lines = Wrap(string.IsNullOrWhiteSpace(v) ? "—" : v!, vf, ContentWidth - keyWidth);
            var h = 13.5f * lines.Count + 3;
            Ensure(h);
            Draw.Text(Canvas, k, Left, _y + 11, kf, _style.Muted);
            for (var i = 0; i < lines.Count; i++)
            {
                Draw.Text(Canvas, lines[i], Left + keyWidth, _y + 11 + 13.5f * i, vf, _style.Ink);
            }

            using var rule = Draw.Stroke(_style.Rule, 0.5f, antialias: false);
            _y += h;
            Canvas.DrawLine(Left, _y, Left + ContentWidth, _y, rule);
        }

        _y += 6;
    }

    /// <summary>Highlighted note box (warnings, disclaimers, extrapolated values).</summary>
    public void Callout(string title, string text, SKColor color)
    {
        using var tf = PlotFonts.Font(9.5f, PlotFonts.SemiBold);
        using var bf = PlotFonts.Font(9f);
        var lines = Wrap(text, bf, ContentWidth - 24);
        var h = 22 + lines.Count * 12.8f + 8;
        Ensure(h + 6);
        var rect = new SKRect(Left, _y + 2, Left + ContentWidth, _y + 2 + h);
        using (var fill = Draw.Fill(color.WithAlpha(22)))
        using (var bar = Draw.Fill(color))
        {
            Canvas.DrawRoundRect(rect, 3, 3, fill);
            Canvas.DrawRect(new SKRect(rect.Left, rect.Top, rect.Left + 3, rect.Bottom), bar);
        }

        Draw.Text(Canvas, title, rect.Left + 12, rect.Top + 15, tf, color);
        for (var i = 0; i < lines.Count; i++)
        {
            Draw.Text(Canvas, lines[i], rect.Left + 12, rect.Top + 30 + 12.8f * i, bf, _style.Ink);
        }

        _y += h + 10;
    }

    /// <summary>A table with a repeated header on page breaks. Column widths are relative weights.</summary>
    public void Table(ResultTable table, UnitPreferences units, IReadOnlyList<float>? weights = null, IReadOnlyList<int>? columns = null, float fontSize = 7.6f)
    {
        var cols = columns ?? Enumerable.Range(0, table.Columns.Count).ToList();
        var w = weights ?? cols.Select(c => table.Columns[c].Numeric ? 1f : 2f).ToList();
        var total = w.Sum();
        var widths = w.Select(x => x / total * ContentWidth).ToArray();
        // Header text shrinks (to 80 %) so that no single word has to be broken mid-word.
        var headerSize = fontSize;
        using (var probe = PlotFonts.Font(fontSize, PlotFonts.SemiBold))
        {
            for (var i = 0; i < cols.Count; i++)
            {
                foreach (var word in table.Columns[cols[i]].HeaderWithUnit(units).Split(' '))
                {
                    var wWord = probe.MeasureText(word);
                    if (wWord > widths[i] - 6)
                    {
                        headerSize = Math.Min(headerSize, Math.Max(fontSize * 0.8f, fontSize * (widths[i] - 6) / wWord));
                    }
                }
            }
        }

        using var hf = PlotFonts.Font(headerSize, PlotFonts.SemiBold);
        using var cf = PlotFonts.Font(fontSize);
        var headerLines = cols.Select((c, i) => Wrap(table.Columns[c].HeaderWithUnit(units), hf, widths[i] - 6)).ToList();
        var headerH = headerLines.Max(l => l.Count) * (fontSize * 1.3f) + 7;

        void Header()
        {
            using var shade = Draw.Fill(_style.Shade);
            Canvas.DrawRect(new SKRect(Left, _y, Left + ContentWidth, _y + headerH), shade);
            var x = Left;
            for (var i = 0; i < cols.Count; i++)
            {
                var numeric = table.Columns[cols[i]].Numeric;
                for (var l = 0; l < headerLines[i].Count; l++)
                {
                    Draw.Text(Canvas, headerLines[i][l], numeric ? x + widths[i] - 3 : x + 3, _y + fontSize * 1.3f * (l + 1) + 1, hf, _style.Ink,
                        numeric ? SKTextAlign.Right : SKTextAlign.Left);
                }

                x += widths[i];
            }

            _y += headerH;
        }

        Ensure(headerH + fontSize * 2.5f);
        Header();
        using var rule = Draw.Stroke(_style.Rule, 0.4f, antialias: false);
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var cells = cols.Select((c, i) => Wrap(table.Text(r, c, units), cf, widths[i] - 6)).ToList();
            var h = cells.Max(l => l.Count) * (fontSize * 1.3f) + 5;
            if (_y + h > Bottom)
            {
                NewPage();
                Header();
            }

            var x = Left;
            for (var i = 0; i < cols.Count; i++)
            {
                var numeric = table.Columns[cols[i]].Numeric;
                for (var l = 0; l < cells[i].Count; l++)
                {
                    Draw.Text(Canvas, cells[i][l], numeric ? x + widths[i] - 3 : x + 3, _y + fontSize * 1.3f * (l + 1), cf, _style.Ink, numeric ? SKTextAlign.Right : SKTextAlign.Left);
                }

                x += widths[i];
            }

            _y += h;
            Canvas.DrawLine(Left, _y, Left + ContentWidth, _y, rule);
        }

        _y += 10;
    }

    /// <summary>A figure drawn by <paramref name="draw"/> into a rectangle of the given height, with a caption.</summary>
    public void Figure(Action<SKCanvas, SKRect> draw, float height, string? caption = null, float? width = null)
    {
        var w = width ?? ContentWidth;
        Ensure(height + (caption is null ? 8 : 26));
        var rect = new SKRect(Left + (ContentWidth - w) / 2, _y + 4, Left + (ContentWidth + w) / 2, _y + 4 + height);
        Canvas.Save();
        Canvas.ClipRect(rect);
        draw(Canvas, rect);
        Canvas.Restore();
        _y = rect.Bottom + 4;
        if (caption is not null)
        {
            using var f = PlotFonts.Font(8.5f, PlotFonts.Italic);
            foreach (var line in Wrap(caption, f, ContentWidth))
            {
                _y += 11.5f;
                Draw.Text(Canvas, line, Left + ContentWidth / 2, _y, f, _style.Muted, SKTextAlign.Center);
            }

            _y += 8;
        }
    }

    /// <summary>Several figures side by side.</summary>
    public void FigureRow(IReadOnlyList<Action<SKCanvas, SKRect>> figures, float height, string? caption = null)
    {
        Figure((c, r) =>
        {
            var w = r.Width / figures.Count;
            for (var i = 0; i < figures.Count; i++)
            {
                var cell = new SKRect(r.Left + i * w, r.Top, r.Left + (i + 1) * w - (i < figures.Count - 1 ? 6 : 0), r.Bottom);
                c.Save();
                c.ClipRect(cell);
                figures[i](c, cell);
                c.Restore();
            }
        }, height, caption);
    }

    private void DrawChrome()
    {
        using var f = PlotFonts.Font(8f, PlotFonts.Medium);
        Draw.Text(Canvas, _chrome.Left, Left, _style.Margin, f, _style.Muted);
        Draw.Text(Canvas, _chrome.Right, Left + ContentWidth, _style.Margin, f, _style.Muted, SKTextAlign.Right);
        using var rule = Draw.Stroke(_style.Rule, 0.6f);
        Canvas.DrawLine(Left, _style.Margin + 6, Left + ContentWidth, _style.Margin + 6, rule);
    }

    private void DrawFooter()
    {
        using var f = PlotFonts.Font(7.5f);
        var y = _style.Size.Height - _style.Margin + 14;
        using var rule = Draw.Stroke(_style.Rule, 0.6f);
        Canvas.DrawLine(Left, y - 12, Left + ContentWidth, y - 12, rule);
        Draw.Text(Canvas, _chrome.FooterLeft, Left, y, f, _style.Muted);
        Draw.Text(Canvas, _chrome.FooterCenter, Left + ContentWidth / 2, y, f, _style.Muted, SKTextAlign.Center);
        Draw.Text(Canvas, _totalPages > 0 ? $"Page {PageCount} of {_totalPages}" : $"Page {PageCount}", Left + ContentWidth, y, f, _style.Muted, SKTextAlign.Right);
    }

    public static List<string> Wrap(string text, SKFont font, float width)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            lines.Add("");
            return lines;
        }

        var current = "";
        foreach (var word in text.Split(' '))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (font.MeasureText(candidate) <= width || current.Length == 0)
            {
                current = candidate;
                // Hard-break words that are wider than the column.
                while (font.MeasureText(current) > width && current.Length > 1)
                {
                    var cut = current.Length - 1;
                    while (cut > 1 && font.MeasureText(current[..cut]) > width)
                    {
                        cut--;
                    }

                    lines.Add(current[..cut]);
                    current = current[cut..];
                }
            }
            else
            {
                lines.Add(current);
                current = word;
            }
        }

        lines.Add(current);
        return lines;
    }
}
