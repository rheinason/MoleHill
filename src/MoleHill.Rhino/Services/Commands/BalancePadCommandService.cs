// mhBalancePad: finds the pad elevations whose cut and fill balance and writes them into the boundaries' Z.
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
/// Balances one or more Grade Pad boundaries against the ground they grade and moves the curves to those
/// elevations.
///
/// <para>The answer lives in the curves rather than in the modifier, so the drawn boundaries still say what
/// the pads will do (auto-balance as a modifier was declined for that reason). When the curves are
/// boundaries of a Grade Pad card on the active terrain, the card's batter slopes and reach are used, the
/// ground is the mesh that card received (not the finished terrain, which already carries the grading),
/// and the card's other pads are graded alongside, held where they are, as the card would build them.
/// Otherwise the slopes are options and the ground is the finished terrain.</para>
///
/// <para>With several pads, <c>Mode=Together</c> moves them all by one amount until the set balances, and
/// <c>Mode=Each</c> balances every pad on its own (<see cref="PadSetBalancer"/>). Volume is exact
/// (<see cref="SurfaceDifferenceVolume"/>) and each pad's tolerance is its area times one millimetre, so
/// "balanced" means "within a millimetre of the balancing elevation" whatever the pad's size.</para>
/// </summary>
internal static class BalancePadCommandService
{
    private const string CutSlopeKey = "MoleHill.BalancePad.CutSlope";
    private const string FillSlopeKey = "MoleHill.BalancePad.FillSlope";
    private const string TargetKey = "MoleHill.BalancePad.TargetNet";
    private const string ReplaceKey = "MoleHill.BalancePad.ReplaceInput";
    private const string EachKey = "MoleHill.BalancePad.Each";

    private sealed record SelectedPad(Guid Id, Curve Curve, Polyline Polyline, int Count, double Area);

    public static Result Run(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out ModelUnitContext unitContext))
            return Result.Failure;

        var getCurves = new GetObject();
        getCurves.SetCommandPrompt("Select closed pad boundaries to balance");
        getCurves.GeometryFilter = ObjectType.Curve;
        getCurves.GeometryAttributeFilter = GeometryAttributeFilter.ClosedCurve;
        getCurves.EnablePreSelect(true, true);
        if (getCurves.GetMultiple(1, 0) != GetResult.Object)
            return getCurves.CommandResult();

        double tolerance = doc.ModelAbsoluteTolerance;
        var selected = new List<SelectedPad>();
        var objects = new List<RhinoObject>();
        for (int i = 0; i < getCurves.ObjectCount; i++)
        {
            ObjRef reference = getCurves.Object(i);
            Curve? curve = reference.Curve();
            RhinoObject? rhinoObject = reference.Object();
            if (curve == null || rhinoObject == null)
                return Result.Failure;
            if (!TryReadPad(curve, tolerance, out Polyline polyline, out int count, out string? error))
            {
                RhinoApp.WriteLine(error);
                return Result.Failure;
            }

            using var outline = new PolylineCurve(polyline);
            double area = Math.Abs(AreaMassProperties.Compute(outline)?.Area ?? 0.0);
            selected.Add(new SelectedPad(reference.ObjectId, curve, polyline, count, area));
            objects.Add(rhinoObject);
        }

        TerrainDefinition? terrain = TerrainController.Instance.GetSelectedTerrain(doc);
        if (terrain == null)
        {
            RhinoApp.WriteLine("Select an active MoleHill terrain before balancing a pad.");
            return Result.Nothing;
        }

        if (!TryFindOwningCard(doc, terrain, objects, out GradePadModifierDefinition? card))
        {
            RhinoApp.WriteLine(
                "The selected boundaries belong to different Grade Pad cards, or only some belong to one. " +
                "Balance one card's pads at a time.");
            return Result.Nothing;
        }

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
            return Balance(doc, unitContext, terrain, selected, card, ground);
        }
    }

    private static Result Balance(
        RhinoDoc doc,
        ModelUnitContext unitContext,
        TerrainDefinition terrain,
        List<SelectedPad> selected,
        GradePadModifierDefinition? card,
        Mesh ground)
    {
        double tolerance = doc.ModelAbsoluteTolerance;
        var cutSlope = new SlopeCommandOption(CutSlopeKey, Math.Tan(RhinoMath.ToRadians(33.0)), optionName: "CutSlope");
        var fillSlope = new SlopeCommandOption(FillSlopeKey, Math.Tan(RhinoMath.ToRadians(33.0)), optionName: "FillSlope");
        double targetNet = CommandOptionCache.GetValue(TargetKey, 0.0);
        bool replaceInput = CommandOptionCache.GetValue(ReplaceKey, true);
        bool each = CommandOptionCache.GetValue(EachKey, false);
        bool several = selected.Count > 1;

        while (true)
        {
            var options = new GetOption();
            string against = card != null
                ? $"Balance against the ground '{card.Label}' grades (its slopes are used)"
                : "Balance against the finished terrain";
            options.SetCommandPrompt($"{against}. Press Enter to balance");
            options.AcceptNothing(true);
            var targetOption = new OptionDouble(targetNet);
            var replaceOption = new OptionToggle(replaceInput, "Copy", "Replace");
            var modeOption = new OptionToggle(each, "Together", "Each");
            if (card == null)
            {
                cutSlope.AddTo(options);
                fillSlope.AddTo(options);
            }

            if (several)
                options.AddOptionToggle("Mode", ref modeOption);
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
            each = modeOption.CurrentValue;
            if (result == GetResult.Option)
                continue;
            if (result != GetResult.Nothing)
                return options.CommandResult();
            break;
        }

        CommandOptionCache.SetValue(TargetKey, targetNet);
        CommandOptionCache.SetValue(ReplaceKey, replaceInput);
        CommandOptionCache.SetValue(EachKey, each);

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

        double apron = unitContext.FromMeters(0.5);
        var moving = new List<PadGrader.PadBoundary>(selected.Count);
        foreach (SelectedPad pad in selected)
        {
            if (!GradePadStage.TryCreatePlanarPadBoundary(
                    pad.Polyline, pad.Count, cutDegrees, maxDistance, apron, fillDegrees,
                    out PadGrader.PadBoundary? boundary, out string? padError) || boundary == null)
            {
                RhinoApp.WriteLine(padError ?? "A boundary does not define a stable planar pad.");
                return Result.Failure;
            }

            moving.Add(boundary);
        }

        // The card's other pads are built with these ones, so they are graded here too, where they stand.
        var held = new List<PadGrader.PadBoundary>();
        if (card != null)
        {
            var selectedIds = selected.Select(pad => pad.Id).ToHashSet();
            foreach (RhinoObject other in RhinoSourceResolver.ResolveObjects(doc, card.Boundaries))
            {
                if (selectedIds.Contains(other.Id) || other.Geometry is not Curve otherCurve ||
                    !TryReadPad(otherCurve, tolerance, out Polyline otherPolyline, out int otherCount, out _))
                    continue;
                if (GradePadStage.TryCreatePlanarPadBoundary(
                        otherPolyline, otherCount, cutDegrees, maxDistance, apron, fillDegrees,
                        out PadGrader.PadBoundary? otherPad, out _) && otherPad != null)
                    held.Add(otherPad);
            }
        }

        if (!RhinoGeometryConversions.TryExtractMesh(ground, out IndexedTriMesh mesh, out string? meshError))
        {
            RhinoApp.WriteLine(meshError ?? "The terrain mesh could not be read.");
            return Result.Failure;
        }

        // Grade exactly as the card will: its tolerance and detail size, and the breaklines it received as
        // barriers (unless it grades through them). Without them a breakline inside a batter's reach is
        // ignored here but stops the batter in the build, and the built pad misses its balance.
        TerrainTolerancePolicy.Profile profile = TerrainTolerancePolicy.Create(terrain.GlobalTolerance, tolerance, unitContext);
        IReadOnlyList<ConstraintPolyline> breaklines = card != null && !card.GradeThroughBreaklines
            ? TerrainController.Instance.CopyIncomingModifierBreaklines(doc, terrain, card.Id)
            : Array.Empty<ConstraintPolyline>();
        PadGrader.LockCurve[] locks = TerrainBuildService.CombinePadLockCurves(
            Array.Empty<PadGrader.LockCurve>(), breaklines, moving.Concat(held).ToArray(),
            mesh.Vertices, mesh.VertexCount, profile.DetailSize, out _);

        double[] volumeTolerances = selected
            .Select(pad => Math.Max(pad.Area * unitContext.FromMeters(0.001), tolerance * tolerance * tolerance))
            .ToArray();
        PadBalanceMode mode = several && each ? PadBalanceMode.Each : PadBalanceMode.Together;
        string heldNote = held.Count > 0 ? $", with the card's other {held.Count} pad(s) held" : string.Empty;
        RhinoApp.WriteLine(several
            ? $"Balancing {selected.Count} pads {(mode == PadBalanceMode.Each ? "each on its own" : "together")}{heldNote} (each step grades them once; Esc stops)..."
            : $"Balancing the pad{heldNote} (each step grades it once; Esc stops)...");

        PadSetBalanceResult balance;
        bool escape = false;
        void OnEscape(object? sender, EventArgs e) => escape = true;
        RhinoApp.EscapeKeyPressed += OnEscape;
        string prompt = RhinoApp.CommandPrompt;
        try
        {
            balance = PadSetBalancer.Balance(
                mesh, moving, held, locks.Length > 0 ? locks : null, mode, targetNet, volumeTolerances,
                modelTolerance: profile.GradePadTolerance,
                hardConstraints: breaklines,
                terrainDetailSize: profile.DetailSize,
                shouldCancel: () => escape,
                progress: grades =>
                {
                    RhinoApp.CommandPrompt = $"Balancing: {grades} grade(s) so far. Press Esc to stop";
                    RhinoApp.Wait();
                });
        }
        catch (OperationCanceledException)
        {
            RhinoApp.WriteLine("Balance cancelled; no boundary was moved.");
            return Result.Cancel;
        }
        catch (ArgumentException exception)
        {
            RhinoApp.WriteLine(exception.Message);
            return Result.Failure;
        }
        finally
        {
            RhinoApp.EscapeKeyPressed -= OnEscape;
            RhinoApp.CommandPrompt = prompt;
        }

        if (balance.Searches.Count == 0 || balance.Searches.Any(search => search.Best is null))
        {
            RhinoApp.WriteLine(balance.Diagnostic ?? "No grade completed, so no balance was measured.");
            return Result.Failure;
        }

        Report(doc.ModelUnitSystem, selected.Count, balance);

        uint undoRecord = doc.BeginUndoRecord("Balance Pad");
        try
        {
            for (int k = 0; k < selected.Count; k++)
            {
                Curve moved = selected[k].Curve.DuplicateCurve();
                moved.Translate(new Vector3d(0.0, 0.0, balance.Offsets[k]));
                bool committed = replaceInput
                    ? doc.Objects.Replace(selected[k].Id, moved)
                    : doc.Objects.AddCurve(moved, doc.Objects.FindId(selected[k].Id)?.Attributes.Duplicate()) != Guid.Empty;
                moved.Dispose();
                if (!committed)
                {
                    RhinoApp.WriteLine("Could not write the balanced boundary.");
                    return Result.Failure;
                }
            }

            doc.Views.Redraw();
            return Result.Success;
        }
        finally
        {
            doc.EndUndoRecord(undoRecord);
        }
    }

    private static void Report(UnitSystem units, int count, PadSetBalanceResult balance)
    {
        if (count > 1)
        {
            for (int k = 0; k < count; k++)
            {
                RhinoApp.WriteLine(
                    $"  Pad {k + 1}: moved {ModelUnits.FormatLength(balance.Offsets[k], units, "F3")}, " +
                    $"cut {ModelUnits.FormatVolume(balance.PadCut[k], units)}, fill {ModelUnits.FormatVolume(balance.PadFill[k], units)}, " +
                    $"net {ModelUnits.FormatVolume(balance.PadCut[k] - balance.PadFill[k], units)}.");
            }
        }

        string moved = count == 1 ? $"moved {ModelUnits.FormatLength(balance.Offsets[0], units, "F3")}, " : string.Empty;
        RhinoApp.WriteLine(
            $"Balance {balance.Status} after {balance.GradeCount} grades: {moved}" +
            $"cut {ModelUnits.FormatVolume(balance.Cut, units)}, fill {ModelUnits.FormatVolume(balance.Fill, units)}, " +
            $"net {ModelUnits.FormatVolume(balance.Net, units)}.");
        if (balance.Status != "Converged")
            RhinoApp.WriteLine(balance.Diagnostic ?? "The search did not converge; the closest grade measured was kept.");
    }

    private static bool TryReadPad(Curve curve, double tolerance, out Polyline polyline, out int count, out string? error)
    {
        error = null;
        count = 0;
        if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: true, out polyline))
        {
            error = "A boundary could not be read as a closed polyline.";
            return false;
        }

        count = polyline.Count;
        if (count > 1 && polyline[0].DistanceTo(polyline[^1]) < tolerance)
            count--;
        if (count < 3)
        {
            error = "A pad boundary needs at least three corners.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// The one enabled Grade Pad card on the terrain whose boundaries name every curve (by id or by layer),
    /// or null when none names any. False when the curves are split between cards or only some are named.
    /// </summary>
    private static bool TryFindOwningCard(
        RhinoDoc doc, TerrainDefinition terrain, IReadOnlyList<RhinoObject> curves, out GradePadModifierDefinition? card)
    {
        card = null;
        bool anyUnowned = false;
        foreach (RhinoObject curve in curves)
        {
            GradePadModifierDefinition? owner = FindOwningCard(doc, terrain, curve);
            if (owner == null)
            {
                anyUnowned = true;
                continue;
            }

            if (card != null && card.Id != owner.Id)
                return false;
            card = owner;
        }

        return card == null || !anyUnowned;
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
