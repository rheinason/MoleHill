// mhBalancePad: finds the pad elevation whose cut and fill balance and writes it into the boundary's Z.
using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Rhino.Model;
using MoleHill.Shared;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Balances one Grade Pad boundary against the ground it grades and moves the curve to that elevation.
///
/// <para>The answer lives in the curve rather than in the modifier, so the drawn boundary still says what
/// the pad will do (auto-balance as a modifier was declined for that reason). When the curve is already a
/// boundary of a Grade Pad card on the active terrain, the card's batter slopes and reach are used and the
/// ground is the mesh that card received, not the finished terrain, which already carries this pad's
/// grading. Otherwise the slopes are options and the ground is the finished terrain.</para>
///
/// <para>The search is <see cref="PadElevationBalancer"/>, the same bracketed search Grasshopper's Balance
/// Grade Pad runs; the volume tolerance is the pad area times one millimetre, so "balanced" means "within
/// a millimetre of the balancing elevation" whatever the pad's size.</para>
/// </summary>
internal static class BalancePadCommandService
{
    private const string CutSlopeKey = "MoleHill.BalancePad.CutSlope";
    private const string FillSlopeKey = "MoleHill.BalancePad.FillSlope";
    private const string TargetKey = "MoleHill.BalancePad.TargetNet";
    private const string ReplaceKey = "MoleHill.BalancePad.ReplaceInput";

    public static Result Run(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out ModelUnitContext unitContext))
            return Result.Failure;

        var getCurve = new GetObject();
        getCurve.SetCommandPrompt("Select a closed pad boundary to balance");
        getCurve.GeometryFilter = ObjectType.Curve;
        getCurve.GeometryAttributeFilter = GeometryAttributeFilter.ClosedCurve;
        getCurve.EnablePreSelect(true, true);
        if (getCurve.Get() != GetResult.Object)
            return getCurve.CommandResult();

        ObjRef reference = getCurve.Object(0);
        Curve? source = reference.Curve();
        RhinoObject? sourceObject = reference.Object();
        if (source == null || sourceObject == null)
            return Result.Failure;

        TerrainDefinition? terrain = TerrainController.Instance.GetSelectedTerrain(doc);
        if (terrain == null)
        {
            RhinoApp.WriteLine("Select an active MoleHill terrain before balancing a pad.");
            return Result.Nothing;
        }

        GradePadModifierDefinition? card = FindOwningCard(doc, terrain, sourceObject);
        Mesh? ground = card != null
            ? TerrainController.Instance.DuplicateIncomingModifierMesh(doc, terrain, card.Id)
            : TerrainController.Instance.DuplicateFinalTerrainMesh(doc, terrain.TerrainId);
        if (ground == null)
        {
            RhinoApp.WriteLine($"Terrain '{terrain.Name}' has no current build. Rebuild it before balancing.");
            return Result.Nothing;
        }

        using (ground)
        {
            return Balance(doc, unitContext, reference.ObjectId, source, terrain, card, ground);
        }
    }

    private static Result Balance(
        RhinoDoc doc,
        ModelUnitContext unitContext,
        Guid sourceId,
        Curve source,
        TerrainDefinition terrain,
        GradePadModifierDefinition? card,
        Mesh ground)
    {
        double tolerance = doc.ModelAbsoluteTolerance;
        if (!RhinoSourceResolver.TryGetPolyline(source, tolerance, requireClosed: true, out Polyline polyline))
        {
            RhinoApp.WriteLine("The boundary could not be read as a closed polyline.");
            return Result.Failure;
        }

        int count = polyline.Count;
        if (count > 1 && polyline[0].DistanceTo(polyline[^1]) < tolerance)
            count--;
        if (count < 3)
        {
            RhinoApp.WriteLine("A pad boundary needs at least three corners.");
            return Result.Failure;
        }

        var cutSlope = new SlopeCommandOption(CutSlopeKey, Math.Tan(RhinoMath.ToRadians(33.0)), optionName: "CutSlope");
        var fillSlope = new SlopeCommandOption(FillSlopeKey, Math.Tan(RhinoMath.ToRadians(33.0)), optionName: "FillSlope");
        double targetNet = CommandOptionCache.GetValue(TargetKey, 0.0);
        bool replaceInput = CommandOptionCache.GetValue(ReplaceKey, true);

        while (true)
        {
            var options = new GetOption();
            options.SetCommandPrompt(card != null
                ? $"Balance against the ground '{card.Label}' grades (its slopes are used). Press Enter to balance"
                : "Balance against the finished terrain. Press Enter to balance");
            options.AcceptNothing(true);
            var targetOption = new OptionDouble(targetNet);
            var replaceOption = new OptionToggle(replaceInput, "Copy", "Replace");
            if (card == null)
            {
                cutSlope.AddTo(options);
                fillSlope.AddTo(options);
            }

            options.AddOptionDouble("TargetNet", ref targetOption);
            options.AddOptionToggle("Output", ref replaceOption);

            GetResult result = options.Get();
            if (card == null)
            {
                cutSlope.Commit(options, result);
                fillSlope.Commit(options, result);
            }

            targetNet = targetOption.CurrentValue;
            replaceInput = replaceOption.CurrentValue;
            if (result == GetResult.Option)
                continue;
            if (result != GetResult.Nothing)
                return options.CommandResult();
            break;
        }

        CommandOptionCache.SetValue(TargetKey, targetNet);
        CommandOptionCache.SetValue(ReplaceKey, replaceInput);

        double cutDegrees, fillDegrees, maxDistance;
        if (card != null)
        {
            fillDegrees = card.SlopeAngle;
            cutDegrees = card.CutSlopeAngle > 0.0 ? card.CutSlopeAngle : card.SlopeAngle;
            maxDistance = card.MaxDistance;
        }
        else
        {
            cutDegrees = RhinoMath.ToDegrees(Math.Atan(Math.Abs(cutSlope.Ratio)));
            fillDegrees = RhinoMath.ToDegrees(Math.Atan(Math.Abs(fillSlope.Ratio)));
            maxDistance = 0.0;
        }

        if (cutDegrees <= 0.0 || fillDegrees <= 0.0)
        {
            RhinoApp.WriteLine("Batter slopes must be steeper than flat.");
            return Result.Failure;
        }

        if (!GradePadStage.TryCreatePlanarPadBoundary(
                polyline, count, cutDegrees, maxDistance, unitContext.FromMeters(0.5), fillDegrees,
                out PadGrader.PadBoundary? pad, out string? padError) || pad == null)
        {
            RhinoApp.WriteLine(padError ?? "The boundary does not define a stable planar pad.");
            return Result.Failure;
        }

        if (!RhinoGeometryConversions.TryExtractMesh(ground, out IndexedTriMesh mesh, out string? meshError))
        {
            RhinoApp.WriteLine(meshError ?? "The terrain mesh could not be read.");
            return Result.Failure;
        }

        if (!PadElevationBalancer.TrySuggestRange(mesh, pad, out double minimum, out double maximum))
        {
            RhinoApp.WriteLine("The pad does not lie over the terrain.");
            return Result.Failure;
        }

        using var boundaryCurve = new PolylineCurve(polyline);
        double padArea = Math.Abs(AreaMassProperties.Compute(boundaryCurve)?.Area ?? 0.0);
        double volumeTolerance = Math.Max(padArea * unitContext.FromMeters(0.001), tolerance * tolerance * tolerance);

        RhinoApp.WriteLine("Balancing the pad (each step grades it once)...");
        PadElevationBalanceResult balance;
        try
        {
            balance = PadElevationBalancer.Balance(
                mesh, pad, locks: null, minimum, maximum, targetNet, volumeTolerance, modelTolerance: tolerance);
        }
        catch (ArgumentException exception)
        {
            RhinoApp.WriteLine(exception.Message);
            return Result.Failure;
        }

        VolumeSearchResult search = balance.Search;
        if (search.Best is not { } best)
        {
            RhinoApp.WriteLine(search.Diagnostic ?? "No grade completed, so no balance was measured.");
            return Result.Failure;
        }

        UnitSystem units = doc.ModelUnitSystem;
        double delta = best.Elevation - pad.BoundaryVertices[2];
        string summary =
            $"Balance {search.Status} after {search.Samples.Count} grades: moved {ModelUnits.FormatLength(delta, units, "F3")}, " +
            $"cut {ModelUnits.FormatVolume(best.Cut, units)}, fill {ModelUnits.FormatVolume(best.Fill, units)}, " +
            $"net {ModelUnits.FormatVolume(best.Net, units)}.";
        RhinoApp.WriteLine(summary);
        if (search.Status != "Converged")
            RhinoApp.WriteLine(search.Diagnostic ?? "The search did not converge; the closest grade measured was kept.");

        Curve moved = source.DuplicateCurve();
        moved.Translate(new Vector3d(0.0, 0.0, delta));
        uint undoRecord = doc.BeginUndoRecord("Balance Pad");
        try
        {
            bool committed = replaceInput
                ? doc.Objects.Replace(sourceId, moved)
                : doc.Objects.AddCurve(moved, doc.Objects.FindId(sourceId)?.Attributes.Duplicate()) != Guid.Empty;
            if (!committed)
            {
                RhinoApp.WriteLine("Could not write the balanced boundary.");
                return Result.Failure;
            }

            doc.Views.Redraw();
            return Result.Success;
        }
        finally
        {
            moved.Dispose();
            doc.EndUndoRecord(undoRecord);
        }
    }

    /// <summary>The first enabled Grade Pad card on the terrain whose boundaries name this curve, by id or by layer.</summary>
    private static GradePadModifierDefinition? FindOwningCard(RhinoDoc doc, TerrainDefinition terrain, RhinoObject curve)
    {
        string? layerPath = doc.Layers.FindIndex(curve.Attributes.LayerIndex)?.FullPath;
        // The command balances Grade Pad boundaries and nothing else, so it names that card type itself.
        foreach (GradePadModifierDefinition pad in terrain.Modifiers.OfType<GradePadModifierDefinition>())
        {
            if (!pad.IsEnabled)
                continue;
            if (pad.Boundaries.ObjectIds.Contains(curve.Id))
                return pad;
            if (layerPath != null &&
                pad.Boundaries.LayerPaths.Any(path => string.Equals(path, layerPath, StringComparison.OrdinalIgnoreCase)))
                return pad;
        }

        return null;
    }
}
