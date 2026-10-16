using System.Globalization;

namespace DownholeStudio.Domain.Units;

/// <summary>Physical quantity kinds that appear in the user interface and reports.</summary>
public enum Quantity
{
    Length,
    Time,
    Velocity,
    Density,
    Modulus,
    Frequency,
    Angle,
    Dimensionless,
}

/// <summary>Display unit presets. Calculations always use SI internally.</summary>
public enum UnitSystem
{
    SI,
    USCustomary,
}

/// <summary>Concrete display units. Every value is stored in SI and converted only here.</summary>
public enum DisplayUnit
{
    Metre,
    Foot,
    Second,
    Millisecond,
    MetrePerSecond,
    FootPerSecond,
    KilogramPerCubicMetre,
    GramPerCubicCentimetre,
    PoundPerCubicFoot,
    Pascal,
    Megapascal,
    Gigapascal,
    PoundPerSquareInch,
    KipPerSquareFoot,
    Hertz,
    Degree,
    None,
}

/// <summary>Exact conversion factors between SI and other units (factor = SI value of one unit).</summary>
public static class UnitFactors
{
    /// <summary>International foot, exact (1959 agreement).</summary>
    public const double MetresPerFoot = 0.3048;

    /// <summary>International avoirdupois pound, exact.</summary>
    public const double KilogramsPerPound = 0.45359237;

    /// <summary>Standard gravity, exact (CGPM 1901).</summary>
    public const double StandardGravity = 9.80665;

    /// <summary>Pound-force in newtons (exact by definition).</summary>
    public const double NewtonsPerPoundForce = KilogramsPerPound * StandardGravity;

    /// <summary>kg/m³ in one lbm/ft³.</summary>
    public const double KgPerM3PerPcf = KilogramsPerPound / (MetresPerFoot * MetresPerFoot * MetresPerFoot);

    /// <summary>Pa in one psi (lbf/in²).</summary>
    public const double PascalsPerPsi = NewtonsPerPoundForce / (0.0254 * 0.0254);

    /// <summary>Pa in one ksf (1000 lbf/ft²).</summary>
    public const double PascalsPerKsf = 1000.0 * NewtonsPerPoundForce / (MetresPerFoot * MetresPerFoot);

    /// <summary>The SI value of one display unit.</summary>
    public static double SiPerUnit(DisplayUnit unit) => unit switch
    {
        DisplayUnit.Metre => 1.0,
        DisplayUnit.Foot => MetresPerFoot,
        DisplayUnit.Second => 1.0,
        DisplayUnit.Millisecond => 1e-3,
        DisplayUnit.MetrePerSecond => 1.0,
        DisplayUnit.FootPerSecond => MetresPerFoot,
        DisplayUnit.KilogramPerCubicMetre => 1.0,
        DisplayUnit.GramPerCubicCentimetre => 1000.0,
        DisplayUnit.PoundPerCubicFoot => KgPerM3PerPcf,
        DisplayUnit.Pascal => 1.0,
        DisplayUnit.Megapascal => 1e6,
        DisplayUnit.Gigapascal => 1e9,
        DisplayUnit.PoundPerSquareInch => PascalsPerPsi,
        DisplayUnit.KipPerSquareFoot => PascalsPerKsf,
        DisplayUnit.Hertz => 1.0,
        DisplayUnit.Degree => Math.PI / 180.0,
        DisplayUnit.None => 1.0,
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null),
    };

    public static string Symbol(DisplayUnit unit) => unit switch
    {
        DisplayUnit.Metre => "m",
        DisplayUnit.Foot => "ft",
        DisplayUnit.Second => "s",
        DisplayUnit.Millisecond => "ms",
        DisplayUnit.MetrePerSecond => "m/s",
        DisplayUnit.FootPerSecond => "ft/s",
        DisplayUnit.KilogramPerCubicMetre => "kg/m³",
        DisplayUnit.GramPerCubicCentimetre => "g/cm³",
        DisplayUnit.PoundPerCubicFoot => "pcf",
        DisplayUnit.Pascal => "Pa",
        DisplayUnit.Megapascal => "MPa",
        DisplayUnit.Gigapascal => "GPa",
        DisplayUnit.PoundPerSquareInch => "psi",
        DisplayUnit.KipPerSquareFoot => "ksf",
        DisplayUnit.Hertz => "Hz",
        DisplayUnit.Degree => "°",
        DisplayUnit.None => "",
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null),
    };
}

/// <summary>
/// The unit chosen for each quantity. Converts SI values to display values and back; never
/// used to store data.
/// </summary>
public sealed record UnitPreferences
{
    public UnitSystem System { get; init; } = UnitSystem.SI;
    public DisplayUnit Length { get; init; } = DisplayUnit.Metre;
    public DisplayUnit Time { get; init; } = DisplayUnit.Millisecond;
    public DisplayUnit Velocity { get; init; } = DisplayUnit.MetrePerSecond;
    public DisplayUnit Density { get; init; } = DisplayUnit.KilogramPerCubicMetre;
    public DisplayUnit Modulus { get; init; } = DisplayUnit.Megapascal;

    public static UnitPreferences Si { get; } = new();

    public static UnitPreferences UsCustomary { get; } = new()
    {
        System = UnitSystem.USCustomary,
        Length = DisplayUnit.Foot,
        Time = DisplayUnit.Millisecond,
        Velocity = DisplayUnit.FootPerSecond,
        Density = DisplayUnit.PoundPerCubicFoot,
        Modulus = DisplayUnit.KipPerSquareFoot,
    };

    public static UnitPreferences For(UnitSystem system) => system == UnitSystem.SI ? Si : UsCustomary;

    public DisplayUnit UnitFor(Quantity quantity) => quantity switch
    {
        Quantity.Length => Length,
        Quantity.Time => Time,
        Quantity.Velocity => Velocity,
        Quantity.Density => Density,
        Quantity.Modulus => Modulus,
        Quantity.Frequency => DisplayUnit.Hertz,
        Quantity.Angle => DisplayUnit.Degree,
        _ => DisplayUnit.None,
    };

    public double ToDisplay(double siValue, Quantity quantity) => siValue / UnitFactors.SiPerUnit(UnitFor(quantity));

    public double FromDisplay(double displayValue, Quantity quantity) => displayValue * UnitFactors.SiPerUnit(UnitFor(quantity));

    public string Symbol(Quantity quantity) => UnitFactors.Symbol(UnitFor(quantity));

    /// <summary>Formats an SI value for display with a sensible number of decimals.</summary>
    public string Format(double? siValue, Quantity quantity, bool withUnit = true, int? decimals = null)
    {
        if (siValue is null || double.IsNaN(siValue.Value))
        {
            return "—";
        }

        var unit = UnitFor(quantity);
        var value = siValue.Value / UnitFactors.SiPerUnit(unit);
        var digits = decimals ?? DefaultDecimals(unit, value);
        var text = value.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture);
        var symbol = UnitFactors.Symbol(unit);
        return withUnit && symbol.Length > 0 ? $"{text} {symbol}" : text;
    }

    private static int DefaultDecimals(DisplayUnit unit, double value) => unit switch
    {
        DisplayUnit.Metre or DisplayUnit.Foot => 2,
        DisplayUnit.Millisecond => 2,
        DisplayUnit.Second => 5,
        DisplayUnit.MetrePerSecond or DisplayUnit.FootPerSecond => 0,
        DisplayUnit.KilogramPerCubicMetre or DisplayUnit.PoundPerCubicFoot => 0,
        DisplayUnit.GramPerCubicCentimetre => 3,
        DisplayUnit.Megapascal => Math.Abs(value) < 10 ? 2 : 1,
        DisplayUnit.Gigapascal => 3,
        DisplayUnit.KipPerSquareFoot or DisplayUnit.PoundPerSquareInch => 0,
        DisplayUnit.Hertz => 1,
        DisplayUnit.Degree => 1,
        _ => 3,
    };
}
