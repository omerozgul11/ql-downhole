namespace DownholeStudio.Domain;

/// <summary>Role of a recorded channel.</summary>
public enum ComponentRole
{
    Unassigned,
    Vertical,
    HorizontalX,
    HorizontalY,
    Reference,
    Other,
}

/// <summary>Which processing chain a trace belongs to.</summary>
public enum ComponentGroup
{
    Vertical,
    Horizontal,
    Reference,
}

/// <summary>Kind of source impact that produced a record.</summary>
public enum ShotKind
{
    Unknown,
    /// <summary>Vertical (compressional) impact for P waves.</summary>
    P,
    /// <summary>Horizontal shear impact in the positive direction (e.g. "left" or "east").</summary>
    SPositive,
    /// <summary>Horizontal shear impact in the opposite direction.</summary>
    SNegative,
}

public enum WaveType
{
    P,
    S,
}

public enum PickOrigin
{
    /// <summary>Placed or moved by the user.</summary>
    Manual,
    /// <summary>Produced by an automatic picker and not yet reviewed.</summary>
    Automatic,
    /// <summary>An automatic suggestion the user explicitly accepted.</summary>
    AcceptedSuggestion,
    /// <summary>Derived from cross-correlation with a reference trace.</summary>
    CrossCorrelation,
}

public enum PickAlgorithm
{
    Manual,
    Aic,
    StaLta,
    EnergyRatio,
    CrossCorrelation,
}

public enum ConfidenceLevel
{
    Low,
    Medium,
    High,
}

public enum AcquisitionDirection
{
    TopDown,
    BottomUp,
    Unknown,
}

public enum DepthReference
{
    /// <summary>Measured depths are below the ground surface.</summary>
    GroundSurface,
    /// <summary>Measured depths are below the top of the borehole casing (collar).</summary>
    Collar,
}

public enum SourceOrientation
{
    Vertical,
    HorizontalInline,
    HorizontalCrossline,
}

public enum Severity
{
    Information,
    Warning,
    Error,
}

public enum TriggerCorrectionMode
{
    /// <summary>Arrival times are used as recorded.</summary>
    None,
    /// <summary>t = t_raw − t_reference + t_expected (reference geophone near the source).</summary>
    SubtractReferenceOnset,
    /// <summary>t = t_raw − (t_reference − median(t_reference)): removes shot-to-shot trigger jitter only.</summary>
    AlignToReferenceMedian,
}

/// <summary>How interval/layer velocities are derived.</summary>
public enum VelocityMethod
{
    /// <summary>Straight lines fitted to corrected travel time versus depth within each interpreted layer.</summary>
    LayerRegression,
    /// <summary>Δz / Δt_corrected between successive receiver depths.</summary>
    PseudoInterval,
    /// <summary>(R₂ − R₁) / (t₂ − t₁) using straight slant ray paths.</summary>
    TrueInterval,
    /// <summary>Layer stripping with Snell's-law refracted rays through the interpreted layers.</summary>
    RefractedRay,
}

public enum RegressionMethod
{
    OrdinaryLeastSquares,
    /// <summary>Huber M-estimator solved by iteratively reweighted least squares.</summary>
    RobustHuber,
}

public enum SComponentMode
{
    HorizontalX,
    HorizontalY,
    /// <summary>Horizontal components rotated by a per-station angle chosen by the user.</summary>
    Rotated,
}

public enum DensitySource
{
    /// <summary>Densities entered by the user per layer (measured or from site data).</summary>
    Measured,
    /// <summary>One density entered by the user for all layers.</summary>
    Constant,
    /// <summary>Empirical estimate from Vp; results are labelled ESTIMATED.</summary>
    Estimated,
}

public enum DensityEstimationMethod
{
    /// <summary>Gardner et al. (1974): ρ = 1.74·Vp^0.25 (Vp in km/s, ρ in g/cm³).</summary>
    Gardner1974,
    /// <summary>Brocher (2005) polynomial fit to the Nafe–Drake curve.</summary>
    Brocher2005NafeDrake,
}

public enum Vs30ExtrapolationMethod
{
    /// <summary>Assume the deepest interpreted layer continues to 30 m.</summary>
    ExtendDeepestLayer,
}
