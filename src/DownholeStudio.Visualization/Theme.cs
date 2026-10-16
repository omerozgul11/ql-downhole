using System.Collections.Concurrent;
using System.Globalization;
using SkiaSharp;

namespace DownholeStudio.Visualization;

/// <summary>Colours used by every chart. Status colours are always paired with a shape or text, never used alone.</summary>
public sealed record PlotTheme
{
    public required SKColor Background { get; init; }
    public required SKColor PlotBackground { get; init; }
    public required SKColor Text { get; init; }
    public required SKColor MutedText { get; init; }
    public required SKColor Grid { get; init; }
    public required SKColor Axis { get; init; }
    public required SKColor Accent { get; init; }
    public required SKColor P { get; init; }
    public required SKColor S { get; init; }
    public required SKColor Good { get; init; }
    public required SKColor Warning { get; init; }
    public required SKColor Problem { get; init; }
    public required SKColor Selection { get; init; }
    public required SKColor Layer { get; init; }
    public required SKColor Wiggle { get; init; }
    public required SKColor WiggleFill { get; init; }
    public required SKColor Raw { get; init; }
    public required SKColor Excluded { get; init; }
    public required SKColor Suggestion { get; init; }
    public required SKColor Reference { get; init; }

    public static PlotTheme Light { get; } = new()
    {
        Background = SKColor.Parse("#FFFFFF"), PlotBackground = SKColor.Parse("#FFFFFF"), Text = SKColor.Parse("#1B1F27"),
        MutedText = SKColor.Parse("#5E6675"), Grid = SKColor.Parse("#E7EAF0"), Axis = SKColor.Parse("#A3AAB8"), Accent = SKColor.Parse("#E5531A"),
        P = SKColor.Parse("#1F6FD1"), S = SKColor.Parse("#C2410C"), Good = SKColor.Parse("#1F8A5B"), Warning = SKColor.Parse("#B7791F"),
        Problem = SKColor.Parse("#C62828"), Selection = SKColor.Parse("#F26522"), Layer = SKColor.Parse("#6B7280"), Wiggle = SKColor.Parse("#232833"),
        WiggleFill = SKColor.Parse("#232833").WithAlpha(150), Raw = SKColor.Parse("#9AA3B2"), Excluded = SKColor.Parse("#C4C9D2"),
        Suggestion = SKColor.Parse("#7C3AED"), Reference = SKColor.Parse("#0F766E"),
    };

    public static PlotTheme Dark { get; } = new()
    {
        Background = SKColor.Parse("#14171D"), PlotBackground = SKColor.Parse("#14171D"), Text = SKColor.Parse("#E6E9EF"),
        MutedText = SKColor.Parse("#9AA3B2"), Grid = SKColor.Parse("#262B35"), Axis = SKColor.Parse("#4B5363"), Accent = SKColor.Parse("#FF7A3D"),
        P = SKColor.Parse("#5AA2FF"), S = SKColor.Parse("#FF8A4C"), Good = SKColor.Parse("#3CC48A"), Warning = SKColor.Parse("#E3B341"),
        Problem = SKColor.Parse("#FF5C5C"), Selection = SKColor.Parse("#FF8A3D"), Layer = SKColor.Parse("#9AA3B2"), Wiggle = SKColor.Parse("#D5DAE3"),
        WiggleFill = SKColor.Parse("#D5DAE3").WithAlpha(140), Raw = SKColor.Parse("#5E6675"), Excluded = SKColor.Parse("#3A404C"),
        Suggestion = SKColor.Parse("#B794F6"), Reference = SKColor.Parse("#2DD4BF"),
    };

    /// <summary>High-contrast palette for reports and exported figures.</summary>
    public static PlotTheme Print { get; } = Light with { Grid = SKColor.Parse("#E3E6EC"), MutedText = SKColor.Parse("#4B5260") };

    public PlotTheme WithTransparentBackground() => this with { Background = SKColors.Transparent, PlotBackground = SKColors.Transparent };
}

/// <summary>The Inter typeface family bundled with the application (SIL Open Font License).</summary>
public static class PlotFonts
{
    private static readonly ConcurrentDictionary<string, SKTypeface> Cache = new();

    public static SKTypeface Regular => Load("Inter-Regular.ttf");

    public static SKTypeface Medium => Load("Inter-Medium.ttf");

    public static SKTypeface SemiBold => Load("Inter-SemiBold.ttf");

    public static SKTypeface Bold => Load("Inter-Bold.ttf");

    public static SKTypeface Italic => Load("Inter-Italic.ttf");

    private static SKTypeface Load(string file) => Cache.GetOrAdd(file, f =>
    {
        var asm = typeof(PlotFonts).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(f, StringComparison.Ordinal));
        if (name is null)
        {
            return SKTypeface.Default;
        }

        using var stream = asm.GetManifestResourceStream(name)!;
        using var data = SKData.Create(stream);
        return SKTypeface.FromData(data) ?? SKTypeface.Default;
    });

    public static SKFont Font(float size, SKTypeface? face = null) => new(face ?? Regular, size) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };
}

/// <summary>Drawing helpers shared by renderers.</summary>
public static class Draw
{
    public static SKPaint Stroke(SKColor color, float width = 1, bool antialias = true, float[]? dash = null)
    {
        var p = new SKPaint { Color = color, StrokeWidth = width, IsStroke = true, IsAntialias = antialias, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        if (dash is not null)
        {
            p.PathEffect = SKPathEffect.CreateDash(dash, 0);
        }

        return p;
    }

    public static SKPaint Fill(SKColor color, bool antialias = true) => new() { Color = color, IsStroke = false, IsAntialias = antialias };

    public static void Text(SKCanvas canvas, string text, float x, float y, SKFont font, SKColor color, SKTextAlign align = SKTextAlign.Left)
    {
        using var paint = Fill(color);
        canvas.DrawText(text, x, y, align, font, paint);
    }

    public static float Width(string text, SKFont font) => font.MeasureText(text);

    public static string Number(double value, double step)
    {
        if (!double.IsFinite(value))
        {
            return "";
        }

        var decimals = step >= 1 ? 0 : Math.Clamp((int)Math.Ceiling(-Math.Log10(step) - 1e-9), 0, 6);
        return value.ToString("F" + decimals, CultureInfo.CurrentCulture);
    }
}
