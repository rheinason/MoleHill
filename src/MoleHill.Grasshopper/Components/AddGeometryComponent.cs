using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using MoleHill.Core.Engine;
using MoleHill.Core.Processing;
using MoleHill.Grasshopper.Utilities;
using MoleHill.Grasshopper.Registry;
using MoleHill.Shared;
using MoleHill.Grasshopper.Types;
using Rhino.Geometry;

namespace MoleHill.Grasshopper.Components;

/// <summary>Adds points and breaklines to an existing mesh and rebuilds one constrained TIN in GH.</summary>
public sealed class AddGeometryComponent : GH_Component
{
    private readonly TinEngine _engine = new();
    public AddGeometryComponent() : base("Add Geometry", "AddGeometry", "Add points and breaklines to an existing MoleHill mesh without baking.", "MoleHill", "Terrain") { }
    public override Guid ComponentGuid => new("F9012345-6789-ABCD-EF01-23456789ABCD");
    protected override System.Drawing.Bitmap? Icon => MoleHillInfo.LoadIcon("MoleHill.Grasshopper.Resources.AddGeometry.png");

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddMeshParameter("Mesh", "M", "Existing 2.5D triangle mesh.", GH_ParamAccess.item);
        p.AddPointParameter("Points", "P", "Additional survey points.", GH_ParamAccess.list); p[1].Optional = true;
        p.AddCurveParameter("Breaklines", "B", "Additional hard breakline or contour curves.", GH_ParamAccess.list); p[2].Optional = true;
        p.AddNumberParameter("Tolerance", "T", "XY tolerance; zero uses the document tolerance.", GH_ParamAccess.item, 0.0); p[3].Optional = true;
        p.AddGenericParameter("Terrain", "Tn", "Optional typed Terrain input; its metadata is carried to the appended Terrain output.", GH_ParamAccess.item); p[4].Optional = true;
        p.AddCurveParameter("Boundary", "D", "Optional closed boundary curves defining the rebuilt TIN footprint.", GH_ParamAccess.list); p[5].Optional = true;
    }
    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddMeshParameter("Mesh", "M", "Rebuilt mesh including the added geometry.", GH_ParamAccess.item);
        p.AddIntegerParameter("Added Vertices", "V", "Number of unique added points and breakline stations.", GH_ParamAccess.item);
        p.AddGenericParameter("Terrain", "Tn", "Terrain-aware rebuilt output when a Terrain input was supplied.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        MoleHillTerrainData? sourceTerrain = null;
        IGH_Goo? terrainGoo = null;
        if (da.GetData(4, ref terrainGoo) && terrainGoo is MoleHillTerrainGoo goo && goo.Value != null)
            sourceTerrain = goo.Value;
        Mesh? mesh = null; bool hasMesh = da.GetData(0, ref mesh) && mesh != null;
        if (!hasMesh && sourceTerrain == null) return;
        mesh ??= sourceTerrain!.Mesh.DuplicateMesh();
        var points = new List<Point3d>(); da.GetDataList(1, points);
        var curves = new List<Curve>(); da.GetDataList(2, curves);
        var boundaries = new List<Curve>(); da.GetDataList(5, boundaries);
        double tolerance = 0.0; da.GetData(3, ref tolerance);
        ModelUnitContext units = ModelUnitContext.FromDocument(Rhino.RhinoDoc.ActiveDoc);
        if (!units.IsSupported) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "MoleHill requires model units."); return; }
        if (tolerance <= 0) tolerance = units.AbsoluteTolerance;

        var allPoints = new List<Point3d>(mesh.Vertices.Count + points.Count);
        for (int i = 0; i < mesh.Vertices.Count; i++) allPoints.Add(mesh.Vertices[i]);
        allPoints.AddRange(points);
        var polylines = new List<double[]>();
        bool hasBoundary = false;
        int geometryIndex = 0;
        foreach (Curve curve in boundaries.Concat(curves))
        {
            int currentIndex = geometryIndex++;
            if (curve == null) continue;
            if (!curve.TryGetPolyline(out Polyline polyline))
            {
                Curve? tessellated = curve.ToPolyline(tolerance, Math.PI / 36.0, 0.0, 0.0);
                if (tessellated == null || !tessellated.TryGetPolyline(out polyline)) { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Could not tessellate a breakline; skipped."); continue; }
            }
            if (polyline.Count < 2) continue;
            var flat = new double[polyline.Count * 3];
            for (int i = 0; i < polyline.Count; i++) { flat[i * 3] = polyline[i].X; flat[i * 3 + 1] = polyline[i].Y; flat[i * 3 + 2] = polyline[i].Z; }
            if (currentIndex < boundaries.Count && curve.IsClosed) hasBoundary = true;
            polylines.Add(flat);
        }
        double[] xyz = new double[allPoints.Count * 3];
        for (int i = 0; i < allPoints.Count; i++) { xyz[i * 3] = allPoints[i].X; xyz[i * 3 + 1] = allPoints[i].Y; xyz[i * 3 + 2] = allPoints[i].Z; }
        PointCloudProcessor.MergedData merged = PointCloudProcessor.Merge(xyz, allPoints.Count, BreaklineDiscretizer.Process(polylines), tolerance);
        if (merged.VertexCount < 3) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Added geometry leaves fewer than three unique points."); return; }
        TinResult? result = _engine.Build(merged.XyCoords, merged.ZValues, merged.Segments, QualitySettings.None, out string? error, useConvexHull: !hasBoundary, boundaryPeelSettings: BoundaryTrianglePeelSettings.Disabled);
        if (result == null) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, error ?? "Add Geometry triangulation failed."); return; }
        Mesh output = RhinoGeometryConversions.ToRhinoMesh(result);
        da.SetData(0, output);
        da.SetData(1, Math.Max(0, merged.VertexCount - mesh.Vertices.Count));
        if (sourceTerrain != null)
        {
            var outputBreaklines = sourceTerrain.Breaklines
                .Concat(curves.Where(curve => curve != null).Select(curve => curve.DuplicateCurve()))
                .ToArray();
            var terrain = new MoleHillTerrainData(output, outputBreaklines, sourceTerrain.Regions,
                sourceTerrain.Name, sourceTerrain.Key, sourceTerrain.Revision, sourceTerrain.Diagnostics,
                sourceTerrain.UnitSystem, sourceTerrain.MetersPerModelUnit, sourceTerrain.LocalToWorld,
                sourceTerrain.HasProjectBaseTransform);
            da.SetData(2, new MoleHillTerrainGoo(terrain));
        }
    }
}
