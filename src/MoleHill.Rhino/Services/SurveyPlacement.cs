using MoleHill.Core.Interop;
using MoleHill.Shared;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace MoleHill.Rhino.Services;

/// <summary>What the user decided to do about a survey that arrives in real-world coordinates.</summary>
internal enum SurveyPlacementChoice
{
    /// <summary>The document already had a project base; the survey was mapped into it.</summary>
    ProjectBase,

    /// <summary>A project base was created at the survey's own centre and the survey mapped into it.</summary>
    ProjectBaseCreated,

    /// <summary>Left where the file put it.</summary>
    RealWorld,

    /// <summary>
    /// The document has a project base, but the survey is already on a small local grid, so it was left
    /// where the file put it rather than shifted by the base offset.
    /// </summary>
    LocalGrid
}

/// <summary>
/// Decides where an incoming survey lands.
///
/// This is the part of the import with the most ways to be quietly wrong, so all three of its rules are
/// in one place:
///
/// <b>Horizontal placement goes through <see cref="DocumentCommandService.ResolveProjectBase"/>, never
/// straight to the transform.</b> That call is what detects and offers to migrate a legacy <c>FOTM</c> or
/// <c>Georef</c> named CPlane. Skipping it would import a document that holds one silently offset by the
/// entire site translation — which parses, draws, and looks exactly like a correct import until somebody
/// measures against existing linework.
///
/// <b>A project base is applied only to a survey that is actually far from the origin.</b> The base maps
/// real-world coordinates into the local frame; a survey already on a site grid near the origin is in
/// that frame (or some other local one) already, and pushing it through the base would shift it by the
/// whole site offset.
///
/// <b>Far from origin with no base is a decision, not a default.</b> A survey in UTM sits at roughly
/// 500,000 E / 6,000,000 N and arrives as thousands of points feeding triangulation and a Z-aware dedup
/// whose tolerances are all absolute. So the user is asked, at the one moment they can act on it.
///
/// <b>Vertical datum is not here at all.</b> It is an offset on the read
/// (<see cref="SurveyReadOptions.VerticalOffset"/>), because the project base is XY-only by design and a
/// datum belongs to the delivery rather than to the site — two surveys on two datums can land in one
/// document, which a document-level setting could not express.
/// </summary>
internal static class SurveyPlacement
{
    /// <summary>
    /// Distance from the origin, in metres, past which a survey is treated as real-world.
    ///
    /// 100 km clears any site grid and catches every projected coordinate system: UTM northings run to
    /// millions, state-plane eastings to hundreds of thousands.
    /// </summary>
    public const double FarFromOriginMeters = 100_000.0;

    /// <summary>Rounding applied to a generated base point, so the transform is a readable number.</summary>
    private const double BasePointRoundingMeters = 1000.0;

    public static bool IsFarFromOrigin(IReadOnlyList<SurveyPoint> points, ModelUnitContext units)
    {
        double limit = units.FromMeters(FarFromOriginMeters);
        foreach (SurveyPoint point in points)
        {
            if (Math.Abs(point.X) > limit || Math.Abs(point.Y) > limit)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves the transform from file coordinates into document coordinates, prompting where the
    /// answer is the user's rather than the document's.
    /// </summary>
    public static Result Resolve(
        RhinoDoc doc,
        IReadOnlyList<SurveyPoint> points,
        ModelUnitContext units,
        out Transform transform,
        out SurveyPlacementChoice choice)
    {
        transform = Transform.Identity;
        choice = SurveyPlacementChoice.RealWorld;

        Result resolution = DocumentCommandService.ResolveProjectBase(doc, out bool hasProjectBase);
        if (resolution != Result.Success)
            return resolution;

        bool farFromOrigin = points.Count > 0 && IsFarFromOrigin(points, units);

        if (hasProjectBase && !farFromOrigin)
        {
            choice = SurveyPlacementChoice.LocalGrid;
            return Result.Success;
        }

        if (hasProjectBase)
        {
            if (!ProjectBaseCPlaneService.TryGetTransform(true, doc, out transform, out string? error))
            {
                RhinoApp.WriteLine(error ?? "MoleHill: the saved project-base transform is invalid.");
                return Result.Failure;
            }

            choice = SurveyPlacementChoice.ProjectBase;
            return Result.Success;
        }

        // Small coordinates with no project base need no conversation: the survey is already on a local
        // grid, and asking would be a prompt with one sensible answer.
        if (!farFromOrigin)
            return Result.Success;

        return PromptForFarFromOrigin(doc, points, units, ref transform, ref choice);
    }

    private static Result PromptForFarFromOrigin(
        RhinoDoc doc,
        IReadOnlyList<SurveyPoint> points,
        ModelUnitContext units,
        ref Transform transform,
        ref SurveyPlacementChoice choice)
    {
        Point3d centre = Centre(points, units);
        RhinoApp.WriteLine(
            $"MoleHill: this survey is centred near {centre.X:N0}, {centre.Y:N0} {units.Abbreviation}, " +
            "far enough from the origin to affect tolerances. It has no project base.");

        var getOption = new GetOption();
        getOption.SetCommandPrompt("Place the survey");
        getOption.AcceptNothing(true);
        int setBase = getOption.AddOption("SetProjectBase");
        int realWorld = getOption.AddOption("RealWorldCoordinates");
        GetResult result = getOption.Get();

        bool createBase = result == GetResult.Nothing ||
                          (result == GetResult.Option && getOption.OptionIndex() == setBase);

        if (!createBase)
        {
            if (result == GetResult.Option && getOption.OptionIndex() == realWorld)
            {
                RhinoApp.WriteLine("MoleHill: importing in real-world coordinates.");
                return Result.Success;
            }

            return getOption.CommandResult();
        }

        var plane = new Plane(new Point3d(centre.X, centre.Y, 0.0), Vector3d.ZAxis);
        if (!ProjectBaseCPlaneService.SaveProjectBasePlane(doc, plane))
        {
            RhinoApp.WriteLine("MoleHill: could not save a project base at the survey centre.");
            return Result.Failure;
        }

        if (!ProjectBaseCPlaneService.TryGetTransform(true, doc, out transform, out string? error))
        {
            RhinoApp.WriteLine(error ?? "MoleHill: the new project-base transform is invalid.");
            return Result.Failure;
        }

        choice = SurveyPlacementChoice.ProjectBaseCreated;
        RhinoApp.WriteLine(
            $"MoleHill: saved a project base at {centre.X:N0}, {centre.Y:N0} {units.Abbreviation}. " +
            "Existing geometry was not moved; mhClearProjectBase removes it.");
        return Result.Success;
    }

    /// <summary>
    /// The survey's centre, rounded so the saved base is a number a person can read off and retype.
    ///
    /// Rounding is cosmetic — any point near the survey would do — but a base at 512,345.678 makes every
    /// later conversation about coordinates harder than one at 512,000.
    /// </summary>
    internal static Point3d Centre(IReadOnlyList<SurveyPoint> points, ModelUnitContext units)
    {
        if (points.Count == 0)
            return Point3d.Origin;

        double sumX = 0.0;
        double sumY = 0.0;
        foreach (SurveyPoint point in points)
        {
            sumX += point.X;
            sumY += point.Y;
        }

        double rounding = units.FromMeters(BasePointRoundingMeters);
        if (!double.IsFinite(rounding) || rounding <= 0.0)
            rounding = 1.0;

        return new Point3d(
            Math.Round(sumX / points.Count / rounding) * rounding,
            Math.Round(sumY / points.Count / rounding) * rounding,
            0.0);
    }
}
