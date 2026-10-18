using System.Drawing;
using MoleHill.Core.Scan;
using MoleHill.Core.Processing;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>Writes paved/soft area boundaries and measured kerbs or colour-only road breaklines.</summary>
internal static partial class ScanToTerrainCommandService
{
    private static IEnumerable<Guid> AddSurfaceOutput(RhinoDoc doc, ScanTerrainPipeline.Result pipeline,
        Func<string, Color, string, ObjectAttributes> onLayer)
    {
        if (pipeline.Areas != null)
        {
            foreach (ScanSurfaceAreas.Boundary boundary in pipeline.Areas.Boundaries)
            {
                bool paved = boundary.Kind == ScanSurfaceKind.Paved;
                string kind = paved ? "Paved areas" : "Soft areas";
                using Curve curve = RouteCurveBuilder.FromFitted(boundary.Curve);
                ObjectAttributes attributes = onLayer(kind, paved ? Color.SlateGray : Color.DarkSeaGreen, kind + " — draped boundary");
                attributes.SetUserString("MoleHill.Scan.GeometryRole", "Classification boundary and elevation breakline");
                yield return doc.Objects.AddCurve(curve, attributes);
            }
        }
        foreach (ScanRoadEdges.EdgeLine edge in pipeline.Roads)
        {
            string layer = edge.IsKerb ? "Kerbs" : "Road edges";
            ObjectAttributes Attributes(string name)
            {
                ObjectAttributes attributes = onLayer(layer, Color.FromArgb(160, 95, 35), name);
                attributes.SetUserString("MoleHill.Scan.MeasuredStep", Format(edge.MeanStep));
                attributes.SetUserString("MoleHill.Scan.StepEvidenceShare", Format(edge.MeasuredShare));
                return attributes;
            }
            using Curve bottom = RouteCurveBuilder.FromFitted(edge.BottomCurve);
            yield return doc.Objects.AddCurve(bottom, Attributes(edge.IsKerb ? "Kerb bottom" : "Road edge"));
            if (edge.TopCurve != null)
            {
                using Curve top = RouteCurveBuilder.FromFitted(edge.TopCurve);
                yield return doc.Objects.AddCurve(top, Attributes("Kerb top"));
            }
        }
    }
}
