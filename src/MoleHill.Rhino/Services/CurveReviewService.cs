// Coordinates the document-owned mhInspectCurve picker, modeless form and review evaluation.
using Eto.Forms;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace MoleHill.Rhino.Services;

internal static class CurveReviewService
{
    private static readonly Dictionary<uint, CurveReviewForm> OpenForms = new();
    public static Result Start(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out _))
            return Result.Failure;

        var getObject = new GetObject();
        getObject.SetCommandPrompt("Select curve to inspect");
        getObject.GeometryFilter = ObjectType.Curve;
        getObject.EnablePreSelect(true, true);
        if (getObject.Get() != GetResult.Object)
            return getObject.CommandResult();

        Guid objectId = getObject.Object(0).ObjectId;
        if (OpenForms.TryGetValue(doc.RuntimeSerialNumber, out CurveReviewForm? existing))
        {
            existing.SetObject(objectId);
            existing.BringToFront();
            return Result.Success;
        }

        var form = new CurveReviewForm(doc, objectId);
        OpenForms[doc.RuntimeSerialNumber] = form;
        form.Closed += (_, _) => OpenForms.Remove(doc.RuntimeSerialNumber);
        Application.Instance.AsyncInvoke(() =>
        {
            try
            {
                form.Show();
                form.PositionOverActiveView();
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"MoleHill could not open Inspect Curve: {ex.Message}");
                OpenForms.Remove(doc.RuntimeSerialNumber);
                form.Close();
            }
        });
        return Result.Success;
    }

    public static CurveReviewAnalysis? Evaluate(
        RhinoDoc doc,
        Curve curve,
        CurveReviewRuleSettings rules,
        out string? error) =>
        CurveReviewAnalyzer.Build(doc, curve, PeekTerrainMesh(doc), rules, out error);

    public static Mesh? PeekTerrainMesh(RhinoDoc doc)
    {
        var terrain = TerrainController.Instance.GetSelectedTerrain(doc);
        return terrain == null ? null : TerrainController.Instance.PeekFinalTerrainMesh(doc, terrain.TerrainId);
    }

    public static string DescribeStatus(CurveReviewAnalysis analysis) => analysis.WarningCount == 0
        ? "OK: no active warnings"
        : $"{analysis.WarningCount} warning{(analysis.WarningCount == 1 ? string.Empty : "s")}";
}
