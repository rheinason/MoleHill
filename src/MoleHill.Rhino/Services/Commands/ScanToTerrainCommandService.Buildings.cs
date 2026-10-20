using System.Drawing;
using MoleHill.Core.Scan;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>Converts validated Core shells before document mutation; writes building context and evidence.</summary>
internal static partial class ScanToTerrainCommandService
{
    private static List<Brep> BuildBuildingSolids(RhinoDoc doc, ScanBuildings.Result? buildings)
    {
        var result = new List<Brep>();
        bool cancelled = false;
        void OnEscape(object? sender, EventArgs args) => cancelled = true;
        RhinoApp.EscapeKeyPressed += OnEscape;
        try
        {
            if (buildings == null)
                return result;
            foreach (ScanBuildings.Building building in buildings.Buildings)
            {
                RhinoApp.SetCommandPrompt($"Preparing building {result.Count + 1} of {buildings.Buildings.Count}. Press Esc to cancel");
                RhinoApp.Wait();
                if (cancelled)
                    throw new OperationCanceledException();
                var patches = new List<Brep>();
                Brep? brep = null;
                try
                {
                    foreach (ScanBuildingFaces.Face face in building.PlanarFaces)
                    {
                        if ((patches.Count & 31) == 0)
                        {
                            RhinoApp.Wait();
                            if (cancelled)
                                throw new OperationCanceledException();
                        }
                        var curves = new List<Curve>();
                        try
                        {
                            foreach (double[] loop in face.Loops)
                            {
                                var points = new List<Point3d>();
                                for (int i = 0; i < loop.Length; i += 3)
                                    points.Add(new Point3d(loop[i], loop[i + 1], loop[i + 2]));
                                points.Add(points[0]);
                                curves.Add(new PolylineCurve(points));
                            }
                            Brep[]? planar = null;
                            // A valid trim at document tolerance can still move short arrangement
                            // edges and leave gaps at its neighbours. Preserve the shared coordinates
                            // by starting precisely; relax only if native planar construction fails.
                            for (int attempt = 0; attempt < 7; attempt++)
                            {
                                planar = Brep.CreatePlanarBreps(curves, doc.ModelAbsoluteTolerance * Math.Pow(.1, 6 - attempt));
                                if (planar is { Length: > 0 } && planar.All(item => item.IsValid))
                                    break;
                                if (planar != null)
                                    foreach (Brep item in planar)
                                        item.Dispose();
                                planar = null;
                            }
                            if (planar == null || planar.Length == 0)
                                throw new InvalidOperationException($"Building {result.Count + 1} ({building.RoofKind}): fitted face {patches.Count + 1} could not be trimmed.");
                            patches.AddRange(planar);
                        }
                        finally
                        {
                            foreach (Curve curve in curves)
                                curve.Dispose();
                        }
                    }
                    for (int attempt = 0; attempt < 7; attempt++)
                    {
                        Brep[]? joined = Brep.JoinBreps(patches, doc.ModelAbsoluteTolerance * Math.Pow(.1, attempt));
                        if (joined is { Length: 1 } && joined[0].IsValid && joined[0].IsSolid)
                        {
                            brep = joined[0];
                            break;
                        }
                        if (joined != null)
                            foreach (Brep item in joined)
                                item.Dispose();
                    }
                    if (brep == null)
                        throw new InvalidOperationException($"Building {result.Count + 1} ({building.RoofKind}): fitted faces did not join into one closed Brep.");
                }
                finally
                {
                    foreach (Brep patch in patches)
                        patch.Dispose();
                }
                if (brep.SolidOrientation == BrepSolidOrientation.Inward)
                    brep.Flip();
                if (!brep.IsValid || !brep.IsSolid)
                {
                    brep.Dispose();
                    throw new InvalidOperationException("A building shell did not form a valid closed Brep.");
                }
                result.Add(brep);
            }
            return result;
        }
        catch
        {
            foreach (Brep brep in result)
                brep.Dispose();
            throw;
        }
        finally
        {
            RhinoApp.EscapeKeyPressed -= OnEscape;
        }
    }

    private static IEnumerable<Guid> AddBuildingOutput(RhinoDoc doc, ScanBuildings.Result? result,
        IReadOnlyList<Brep> solids, Func<string, Color, string, ObjectAttributes> onLayer)
    {
        if (result == null)
            yield break;
        for (int i = 0; i < result.Buildings.Count; i++)
        {
            ScanBuildings.Building building = result.Buildings[i];
            ObjectAttributes attributes = onLayer(TerrainCurveExtractionStyle.Buildings.Layer, TerrainCurveExtractionStyle.Buildings.Colour, $"Building {building.RoofKind}");
            attributes.SetUserString("MoleHill.Scan.RoofKind", building.RoofKind.ToString());
            attributes.SetUserString("MoleHill.Scan.RoofFallback", building.IsFallback ? "Yes" : "No");
            attributes.SetUserString("MoleHill.Scan.GroundHeight", Format(building.GroundHeight));
            attributes.SetUserString("MoleHill.Scan.EaveHeight", Format(building.EaveHeight));
            attributes.SetUserString("MoleHill.Scan.RidgeHeight", Format(building.RidgeHeight));
            attributes.SetUserString("MoleHill.Scan.RoofRms", Format(building.RoofRms));
            attributes.SetUserString("MoleHill.Scan.RoofWithinToleranceShare", Format(building.RoofWithinToleranceShare));
            attributes.SetUserString("MoleHill.Scan.RoofSampleCoverage", Format(building.RoofSampleCoverage));
            attributes.SetUserString("MoleHill.Scan.PlanarShare", Format(building.PlanarShare));
            attributes.SetUserString("MoleHill.Scan.RoofPlanes", building.RoofPlaneCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            attributes.SetUserString("MoleHill.Scan.PlanarFaces", building.PlanarFaces.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            attributes.SetUserString("MoleHill.Scan.FootprintArea", Format(building.FootprintArea));
            attributes.SetUserString("MoleHill.Scan.Diagnostic", building.Diagnostic);
            yield return doc.Objects.AddBrep(solids[i], attributes);
        }
    }
}
