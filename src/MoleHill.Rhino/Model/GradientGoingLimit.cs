namespace MoleHill.Rhino.Model;

/// <summary>
/// The longest ramp going allowed at one gradient: one row of a table like Approved Document M Table 1.
/// The slope is stored in degrees like every slope in the model; the going in model units.
/// </summary>
public sealed class GradientGoingLimit
{
    public double SlopeDegrees { get; set; }

    public double MaxGoing { get; set; }
}
