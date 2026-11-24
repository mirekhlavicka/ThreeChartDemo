namespace ThreeChartDemo.Models;

/// <summary>User input from the UI.</summary>
public sealed class ChartSettings
{
    public string Formula { get; set; } = "sin(x + t) + cos(y - t)";

    public double XMin { get; set; } = -6.28;
    public double XMax { get; set; } = 6.28;
    public double YMin { get; set; } = -6.28;
    public double YMax { get; set; } = 6.28;
    public double TMin { get; set; } = 0;
    public double TMax { get; set; } = 6.28;

    public int DivisionsX { get; set; } = 80;
    public int DivisionsY { get; set; } = 80;
    public int DivisionsT { get; set; } = 60;

    /// <summary>Animation speed: how many time steps are shown per second.</summary>
    public int StepsPerSecond { get; set; } = 20;

    // ---- adaptive meshing ----
    /// <summary>true: every time step gets its own locally refined triangle mesh (precomputed).</summary>
    public bool Adaptive { get; set; } = false;
    /// <summary>Refine a triangle when |f(centre) - linear interpolation of its vertices| exceeds this.</summary>
    public double Epsilon { get; set; } = 0.02;
    /// <summary>Maximum number of refinement passes per time step.</summary>
    public int MaxRefinements { get; set; } = 6;
    /// <summary>Safety cap on triangles per time step.</summary>
    public int MaxTriangles { get; set; } = 60000;

    public const int MaxDivisions = 500;
    public const int MaxTimeDivisions = 5000;

    /// <summary>Returns an error message, or null when the settings are valid.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Formula)) return "Enter a function f(x, y, t).";
        if (!double.IsFinite(XMin) || !double.IsFinite(XMax) || XMin >= XMax) return "X interval: min must be less than max.";
        if (!double.IsFinite(YMin) || !double.IsFinite(YMax) || YMin >= YMax) return "Y interval: min must be less than max.";
        if (!double.IsFinite(TMin) || !double.IsFinite(TMax) || TMin >= TMax) return "T interval: min must be less than max.";
        if (DivisionsX < 1 || DivisionsX > MaxDivisions) return $"X divisions must be between 1 and {MaxDivisions}.";
        if (DivisionsY < 1 || DivisionsY > MaxDivisions) return $"Y divisions must be between 1 and {MaxDivisions}.";
        if (DivisionsT < 1 || DivisionsT > MaxTimeDivisions) return $"T divisions must be between 1 and {MaxTimeDivisions}.";
        if (StepsPerSecond < 1 || StepsPerSecond > 120) return "Speed must be between 1 and 120 steps per second.";
        if (Adaptive)
        {
            if (!double.IsFinite(Epsilon) || Epsilon <= 0) return "Epsilon must be greater than 0.";
            if (MaxRefinements < 0 || MaxRefinements > 30) return "Max refinements must be between 0 and 30.";
            if (MaxTriangles < 100 || MaxTriangles > 1_000_000) return "Max triangles must be between 100 and 1 000 000.";
        }
        return null;
    }
}
