using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownholeStudio.Core;
using DownholeStudio.Domain;
using DownholeStudio.Domain.Units;
using DownholeStudio.Visualization;

namespace DownholeStudio.App.ViewModels.Pages;

public static class Icons
{
    public static Geometry? Get(string key) =>
        Application.Current?.TryGetResource("Icon." + key, Application.Current.ActualThemeVariant, out var value) == true ? value as Geometry : null;
}

/// <summary>Base for the workspace pages. Pages adapt project state to controls; they never calculate science themselves.</summary>
public abstract partial class PageViewModel(WorkspaceViewModel workspace) : ObservableObject
{
    public WorkspaceViewModel Workspace { get; } = workspace;

    public abstract string NavTitle { get; }
    public abstract string Title { get; }
    /// <summary>Plain-language explanation shown in Guided mode.</summary>
    public abstract string Description { get; }
    public abstract string IconKey { get; }
    public virtual string HelpTopic => NavTitle.ToLowerInvariant().Replace(' ', '-');
    public Geometry? IconData => Icons.Get(IconKey);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusGlyph), nameof(StatusTip))]
    private StepStatus _status;

    public string StatusGlyph => Status switch { StepStatus.Complete => "Good", StepStatus.NeedsAttention => "Warning", _ => "" };

    public string StatusTip => Status switch
    {
        StepStatus.Complete => "Done",
        StepStatus.NeedsAttention => "Needs attention",
        StepStatus.InProgress => "In progress",
        _ => "Not started",
    };

    public bool IsGuided => !Workspace.IsExpert;
    public bool IsExpert => Workspace.IsExpert;

    public virtual object? Inspector => null;

    /// <summary>Whether this page uses the right-hand inspector.</summary>
    public virtual bool HasInspector => true;

    /// <summary>Page suggested after this one (the "Next" button).</summary>
    public virtual PageViewModel? Next => null;

    public string? NextTitle => Next is { } n ? $"Next: {n.NavTitle}" : null;

    protected ProjectState State => Workspace.State;
    protected UnitPreferences Units => Workspace.Units;
    protected PlotTheme PlotTheme => Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? PlotTheme.Dark : PlotTheme.Light;

    public virtual void OnStateChanged()
    {
    }

    public virtual void OnResultChanged()
    {
    }

    public virtual void OnSelectionChanged()
    {
    }

    public virtual void OnActivated()
    {
        OnStateChanged();
        OnResultChanged();
    }

    public virtual void OnModeChanged()
    {
        OnPropertyChanged(nameof(IsGuided));
        OnPropertyChanged(nameof(IsExpert));
    }

    protected bool IsActive => ReferenceEquals(Workspace.CurrentPage, this);

    protected bool Execute(ProjectEdit edit) => Workspace.Execute(edit);

    [RelayCommand]
    private void GoNext()
    {
        if (Next is { } n)
        {
            Workspace.Navigate(n);
        }
    }

    [RelayCommand]
    private void OpenHelp() => Workspace.Main.ShowHelpCommand.Execute(HelpTopic);

    // Display-unit helpers.
    protected double? ToDisplay(double? si, Quantity q) => si is { } v ? Units.ToDisplay(v, q) : null;

    protected double FromDisplay(double display, Quantity q) => Units.FromDisplay(display, q);

    public string LengthUnit => Units.Symbol(Quantity.Length);
    public string VelocityUnit => Units.Symbol(Quantity.Velocity);
    public string DensityUnit => Units.Symbol(Quantity.Density);
    public string ModulusUnit => Units.Symbol(Quantity.Modulus);

    protected void NotifyUnits()
    {
        OnPropertyChanged(nameof(LengthUnit));
        OnPropertyChanged(nameof(VelocityUnit));
        OnPropertyChanged(nameof(DensityUnit));
        OnPropertyChanged(nameof(ModulusUnit));
    }
}

/// <summary>A labelled option for combo boxes.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}
