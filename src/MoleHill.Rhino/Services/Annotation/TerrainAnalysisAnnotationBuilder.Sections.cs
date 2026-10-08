using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Shared;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

// The section family: terrain, cross-station and longitudinal sections, cut/fill reference slices and
// comparison hatches.
internal static partial class TerrainAnalysisAnnotationBuilder
{
    private sealed record SectionTerrainProfile(
        Guid TerrainId,
        string TerrainName,
        int ColorArgb,
        TerrainSectionResult Slice,
        bool IsOwner);

    private readonly record struct SectionEmissionStats(int OutputCount, int CutRegions, int FillRegions);

    /// <summary>
    /// Where one section drawing sits in elevation and how much room it takes, measured once and read by both
    /// the layout that places the cell and the code that draws into it. The three layouts each used to guess
    /// the room below the axis by hand, and every guess drifted from what was actually drawn there.
    /// </summary>
    /// <param name="Minimum">Lowest elevation drawn, existing ground included; the cell origin sits here.</param>
    /// <param name="AxisBottom">The foot of the elevation axis: the minimum, snapped down to a grid step.</param>
    /// <param name="Below">Drawing units below the cell origin: the snap, station figures, title and key.</param>
    /// <param name="Above">Drawing units above the cell origin, to the top of the axis.</param>
    private readonly record struct SectionFrame(
        double Minimum,
        double Maximum,
        double GridSpacing,
        double AxisBottom,
        double AxisTop,
        double Below,
        double Above);

    /// <summary>
    /// Measures a section before it is placed. Existing ground counts towards the range: under a fill it is
    /// the lowest line on the drawing, and leaving it out let it run through the labels and the next row.
    /// </summary>
    private static SectionFrame MeasureSection(
        TerrainSectionAnnotationDefinitionBase analysis,
        IReadOnlyList<SectionTerrainProfile> profiles,
        TerrainSectionResult? existingGround,
        double requestedGridInterval,
        bool showElevationGrid,
        bool showStationLabels,
        double textHeight,
        double verticalScale)
    {
        double minimum = profiles.Min(profile => profile.Slice.MinimumElevation);
        double maximum = profiles.Max(profile => profile.Slice.MaximumElevation);
        if (existingGround != null)
        {
            minimum = Math.Min(minimum, existingGround.MinimumElevation);
            maximum = Math.Max(maximum, existingGround.MaximumElevation);
        }

        double spacing = SectionLayoutHelper.ResolveElevationGridSpacing(minimum, maximum, requestedGridInterval);
        bool snaps = spacing > 0.0 && (showElevationGrid || analysis.ShowElevationLabels);
        double axisBottom = snaps ? Math.Floor(minimum / spacing) * spacing : minimum;
        double axisTop = snaps ? Math.Ceiling(maximum / spacing) * spacing : maximum;

        // Matches EmitCombinedProfileObjects: station figures centred 1.5 text heights under the axis, the
        // title 2 under those, then 1.5 per key row, each text half a height deep.
        double th = Math.Max(textHeight, double.Epsilon);
        int keyRows = DescribeProfileKey(analysis, DescribeProfiles(profiles, existingGround), existingGround != null).Count;
        double band = (showStationLabels ? 2.0 * th : 0.0) +
            (analysis.ShowSectionTitle ? (2.5 * th) + (1.5 * th * keyRows) : 0.0);
        return new SectionFrame(
            minimum,
            maximum,
            spacing,
            axisBottom,
            axisTop,
            ((minimum - axisBottom) * verticalScale) + band,
            (axisTop - minimum) * verticalScale);
    }

    /// <summary>
    /// The ground a section shades cut and fill against, resolved before layout so its depth is measured.
    /// Null when cut/fill is off or there is nothing to compare against (the diagnostic says why).
    /// </summary>
    private static TerrainSectionResult? ResolveExistingGround(
        TerrainBuildSnapshot snapshot,
        SectionCutGeometry cutGeometry,
        TerrainSectionAnnotationDefinitionBase analysis,
        IReadOnlyList<SectionTerrainProfile> profiles,
        double tolerance,
        TerrainBuildResult build,
        RhinoMesh? baseMesh,
        CutFillReferenceMesh referenceMesh) =>
        analysis.IsEnabled && analysis.ShowCutFillRegions
            ? ResolveCutFillReferenceSlice(snapshot, cutGeometry, analysis, profiles, tolerance, build, baseMesh, referenceMesh)
            : null;

    /// <summary>
    /// The slice that places plan marks: this terrain's, or — when the cut misses this terrain but crosses a
    /// comparison terrain — the first one drawn.
    /// </summary>
    private static TerrainSectionResult PrimarySlice(IReadOnlyList<SectionTerrainProfile> profiles) =>
        (profiles.FirstOrDefault(profile => profile.IsOwner) ?? profiles[0]).Slice;

    private static List<(Guid TerrainId, string TerrainName, int ColorArgb, bool IsOwner, bool IsReference)> DescribeProfiles(
        IReadOnlyList<SectionTerrainProfile> profiles,
        TerrainSectionResult? existingGround) =>
        profiles
            .Select(profile => (profile.TerrainId, profile.TerrainName, profile.ColorArgb, profile.IsOwner,
                IsReference: existingGround != null && ReferenceEquals(profile.Slice, existingGround)))
            .ToList();

    public static TerrainAnalysisSummary BuildTerrainSectionSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        TerrainSectionAnnotationDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel,
        LayerRoleTable? layerRoles = null,
        RhinoMesh? baseMesh = null)
    {
        var objects = TerrainBuildSnapshotResolver.ResolveObjects(snapshot, analysis.Sources);
        var insertionPlane = ResolveInsertionPlane(analysis, mesh);
        double tolerance = snapshot.ModelAbsoluteTolerance;
        double verticalScale = ResolveVerticalExaggeration(analysis);
        double textHeight = ResolveTextHeight(snapshot, analysis);
        int sourceCount = 0;
        int outputCount = 0;
        AddMissingSectionTerrainDiagnostics(snapshot, analysis, build);
        var referenceMesh = new CutFillReferenceMesh();

        var sections = new List<(List<SectionTerrainProfile> Profiles, IReadOnlyList<Point3d> Cut, TerrainSectionResult? Existing, SectionFrame Frame)>();
        double maxStation = 0.0;
        int availableTerrainCount = 1;

        foreach (var entry in objects)
        {
            ThrowIfCancellationRequested(shouldCancel);
            if (entry.Geometry is not Curve curve)
                continue;

            sourceCount++;
            var cutVertices = ApproximateCurveAsPolyline(curve, tolerance);
            if (cutVertices.Count < 2)
                continue;

            List<SectionTerrainProfile> profiles = SliceTerrainsAlongPolyline(
                snapshot, mesh, analysis, cutVertices, tolerance);
            if (profiles.Count == 0 || profiles[0].Slice.IsEmpty)
                continue;

            TerrainSectionResult? existing = ResolveExistingGround(
                snapshot, SectionCutGeometry.AlongPolyline(cutVertices), analysis, profiles, tolerance, build, baseMesh, referenceMesh);
            SectionFrame frame = MeasureSection(
                analysis, profiles, existing, analysis.ElevationGridInterval, analysis.ShowElevationGrid,
                analysis.ShowStationLabels, textHeight, verticalScale);
            sections.Add((profiles, cutVertices, existing, frame));
            availableTerrainCount = Math.Max(availableTerrainCount, profiles.Count);
            maxStation = Math.Max(maxStation, profiles.Max(profile => profile.Slice.TotalStationLength));
        }

        // One row of cells, side by side; the gap leaves room for the next cell's elevation figures.
        double cellWidth = maxStation + Math.Max(maxStation * 0.15, textHeight * 8.0);

        int cutRegions = 0;
        int fillRegions = 0;
        for (int i = 0; i < sections.Count; i++)
        {
            ThrowIfCancellationRequested(shouldCancel);
            var (profiles, cut, existing, frame) = sections[i];
            Plane cellPlane = OffsetCellPlane(insertionPlane, i, columns: sections.Count, cellWidth, cellHeight: 0.0);
            string mark = SectionMark(i);
            if (analysis.IsEnabled && analysis.ShowPlanLabels)
            {
                outputCount += EmitPlanMarkPair(
                    analysis, build, layerRoles, PrimarySlice(profiles), mark,
                    cut[0], cut[0] - cut[1], cut[^1], cut[^1] - cut[^2],
                    textHeight);
            }

            SectionEmissionStats emitted = EmitCombinedProfileObjects(
                analysis,
                build,
                profiles,
                existing,
                frame,
                cellPlane,
                horizontalScale: 1.0,
                verticalScale: verticalScale,
                comparisonTolerance: tolerance,
                showBaseline: true,
                showElevationGrid: analysis.ShowElevationGrid,
                showStationTicks: analysis.ShowStationTicks,
                stationTickInterval: analysis.StationTickInterval,
                showStationLabels: analysis.ShowStationLabels,
                stationLabelInterval: analysis.StationTickInterval,
                textHeight: textHeight,
                layerRoles: layerRoles,
                sectionLabel: $"{analysis.Label} {mark}",
                hatchPatterns: snapshot.HatchPatterns,
                title: analysis.ShowSectionTitle ? SectionTitle(analysis, mark) : null);
            outputCount += emitted.OutputCount;
            cutRegions += emitted.CutRegions;
            fillRegions += emitted.FillRegions;
        }

        return new TerrainAnalysisSummary
        {
            AnalysisId = analysis.Id,
            SampleSourceCount = sourceCount,
            GeneratedOutputCount = outputCount,
            SectionTerrainCount = availableTerrainCount,
            SectionCutRegionCount = cutRegions,
            SectionFillRegionCount = fillRegions
        };
    }

    public static TerrainAnalysisSummary BuildCrossSectionStationSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        CrossSectionStationAnnotationDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel,
        LayerRoleTable? layerRoles = null,
        RhinoMesh? baseMesh = null)
    {
        var objects = TerrainBuildSnapshotResolver.ResolveObjects(snapshot, analysis.Sources);
        var insertionPlane = ResolveInsertionPlane(analysis, mesh);
        double tolerance = snapshot.ModelAbsoluteTolerance;
        double stationInterval = Math.Max(analysis.StationInterval, tolerance * 100.0);
        double halfWidth = Math.Max(analysis.CrossSectionWidth * 0.5, tolerance * 10.0);
        int gridColumns = Math.Max(analysis.GridColumns, 1);
        double verticalScale = ResolveVerticalExaggeration(analysis);
        double textHeight = ResolveTextHeight(snapshot, analysis);
        int sourceCount = 0;
        int outputCount = 0;
        AddMissingSectionTerrainDiagnostics(snapshot, analysis, build);
        var referenceMesh = new CutFillReferenceMesh();
        int globalIndex = 0;
        int availableTerrainCount = 1;
        int cutRegions = 0;
        int fillRegions = 0;

        foreach (var entry in objects)
        {
            ThrowIfCancellationRequested(shouldCancel);
            if (entry.Geometry is not Curve alignment)
                continue;

            sourceCount++;
            var stations = GetCurveDivisionSamples(alignment, stationInterval);
            if (stations.Count == 0)
                continue;

            var slices = new List<(double Station, List<SectionTerrainProfile> Profiles, Point3d[] Cut, TerrainSectionResult? Existing, SectionFrame Frame)>(stations.Count);
            double maxStation = 0.0;
            double maxBelow = 0.0;
            double maxAbove = 0.0;

            foreach (var station in stations)
            {
                ThrowIfCancellationRequested(shouldCancel);
                Vector3d tangent = alignment.TangentAt(station.Parameter);
                tangent.Z = 0.0;
                if (!tangent.Unitize())
                    continue;

                var perpendicular = new Vector3d(-tangent.Y, tangent.X, 0.0);
                Point3d a = station.Point - (perpendicular * halfWidth);
                Point3d b = station.Point + (perpendicular * halfWidth);
                a.Z = 0.0;
                b.Z = 0.0;

                var cut = new[] { a, b };
                List<SectionTerrainProfile> profiles = SliceTerrainsAlongPolyline(
                    snapshot, mesh, analysis, cut, tolerance);
                if (profiles.Count == 0 || profiles[0].Slice.IsEmpty)
                    continue;

                TerrainSectionResult? existing = ResolveExistingGround(
                    snapshot, SectionCutGeometry.AlongPolyline(cut), analysis, profiles, tolerance, build, baseMesh, referenceMesh);
                SectionFrame frame = MeasureSection(
                    analysis, profiles, existing, analysis.ElevationGridInterval, analysis.ShowElevationGrid,
                    analysis.LabelStations, textHeight, verticalScale);
                slices.Add((
                    alignment.GetLength(new Interval(alignment.Domain.T0, station.Parameter)),
                    profiles,
                    cut,
                    existing,
                    frame));
                availableTerrainCount = Math.Max(availableTerrainCount, profiles.Count);
                maxStation = Math.Max(maxStation, profiles.Max(profile => profile.Slice.TotalStationLength));
                maxBelow = Math.Max(maxBelow, frame.Below);
                maxAbove = Math.Max(maxAbove, frame.Above);
            }

            // Automatic cells fit the tallest drawing: any row's foot (its labels, title and key) must clear
            // the next row's head, so the deepest foot and tallest head are added, plus a gap. Elevation
            // figures to the left of each drawing take a fixed width.
            double labelWidth = analysis.ShowElevationLabels ? textHeight * 6.0 : 0.0;
            double cellWidth = analysis.GridCellWidth > 0.0
                ? analysis.GridCellWidth
                : (analysis.CrossSectionWidth + Math.Max(maxStation, analysis.CrossSectionWidth) * 0.1) + labelWidth;
            double cellHeight = analysis.GridCellHeight > 0.0
                ? analysis.GridCellHeight
                : Math.Max(maxBelow + maxAbove + (2.0 * textHeight), analysis.CrossSectionWidth * 0.3);

            for (int i = 0; i < slices.Count; i++)
            {
                ThrowIfCancellationRequested(shouldCancel);
                var (alignmentStation, profiles, cutLine, existing, frame) = slices[i];
                Plane cellPlane = OffsetCellPlane(insertionPlane, globalIndex, gridColumns, cellWidth, cellHeight);
                globalIndex++;
                string stationText = $"Sta {alignmentStation:F2}";

                if (analysis.IsEnabled && analysis.ShowPlanLabels)
                {
                    build.AuxiliaryObjects.Add(BuildPlanStationLabel(
                        analysis, layerRoles, PrimarySlice(profiles), cutLine[0], cutLine[1], stationText, textHeight));
                    outputCount++;
                }

                if (analysis.ShowCutLinesOnTerrain)
                {
                    foreach (SectionTerrainProfile profile in profiles)
                    {
                        foreach (TerrainSectionSegment segment in profile.Slice.Segments)
                        {
                            var poly = new Polyline(segment.Vertices.Count);
                            for (int v = 0; v < segment.Vertices.Count; v++)
                                poly.Add(segment.Vertices[v].World);
                            build.AuxiliaryObjects.Add(BuildPolylineObject(
                                analysis,
                                poly,
                                layerRoles,
                                $"{analysis.Label} {profile.TerrainName} cut {globalIndex}",
                                LayerRole.SectionsCuts,
                                profile.ColorArgb));
                            outputCount++;
                        }
                    }
                }

                SectionEmissionStats emitted = EmitCombinedProfileObjects(
                    analysis,
                    build,
                    profiles,
                    existing,
                    frame,
                    cellPlane,
                    horizontalScale: 1.0,
                    verticalScale: verticalScale,
                    comparisonTolerance: tolerance,
                    showBaseline: true,
                    showElevationGrid: analysis.ShowElevationGrid,
                    showStationTicks: false,
                    stationTickInterval: 0.0,
                    showStationLabels: analysis.LabelStations,
                    stationLabelInterval: 0.0,
                    textHeight: textHeight,
                    layerRoles: layerRoles,
                    sectionLabel: stationText,
                    hatchPatterns: snapshot.HatchPatterns,
                    title: analysis.ShowSectionTitle ? stationText : null);
                outputCount += emitted.OutputCount;
                cutRegions += emitted.CutRegions;
                fillRegions += emitted.FillRegions;
            }
        }

        return new TerrainAnalysisSummary
        {
            AnalysisId = analysis.Id,
            SampleSourceCount = sourceCount,
            GeneratedOutputCount = outputCount,
            SectionTerrainCount = availableTerrainCount,
            SectionCutRegionCount = cutRegions,
            SectionFillRegionCount = fillRegions
        };
    }

    public static TerrainAnalysisSummary BuildLongitudinalSectionSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        LongitudinalSectionAnnotationDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel,
        LayerRoleTable? layerRoles = null,
        RhinoMesh? baseMesh = null)
    {
        var objects = TerrainBuildSnapshotResolver.ResolveObjects(snapshot, analysis.Sources);
        var insertionPlane = ResolveInsertionPlane(analysis, mesh);
        double tolerance = snapshot.ModelAbsoluteTolerance;
        double sampleInterval = Math.Max(analysis.SampleInterval, tolerance * 10.0);
        double verticalScale = ResolveVerticalExaggeration(analysis);
        double textHeight = ResolveTextHeight(snapshot, analysis);
        int sourceCount = 0;
        int outputCount = 0;
        int availableTerrainCount = 1;
        int cutRegions = 0;
        int fillRegions = 0;
        AddMissingSectionTerrainDiagnostics(snapshot, analysis, build);
        var referenceMesh = new CutFillReferenceMesh();

        var sections = new List<(Curve Curve, List<SectionTerrainProfile> Profiles, TerrainSectionResult? Existing, SectionFrame Frame)>();
        foreach (var entry in objects)
        {
            ThrowIfCancellationRequested(shouldCancel);
            if (entry.Geometry is not Curve curve)
                continue;

            sourceCount++;
            List<SectionTerrainProfile> profiles = SampleTerrainsAlongCurve(
                snapshot, mesh, analysis, curve, sampleInterval, tolerance);
            if (profiles.Count == 0 || profiles[0].Slice.IsEmpty)
                continue;

            TerrainSectionResult? existing = ResolveExistingGround(
                snapshot, SectionCutGeometry.AlongCurve(curve, sampleInterval), analysis, profiles, tolerance, build, baseMesh, referenceMesh);
            SectionFrame frame = MeasureSection(
                analysis, profiles, existing, analysis.ElevationGridInterval, analysis.ShowElevationGrid,
                analysis.ShowStationLabels, textHeight, verticalScale);
            sections.Add((curve, profiles, existing, frame));
            availableTerrainCount = Math.Max(availableTerrainCount, profiles.Count);
        }

        // One section per curve, stacked downwards. Every one used to be drawn at the insertion point, on top
        // of the others. Each row's head clears the previous row's foot by a gap.
        double rowOffset = 0.0;
        for (int i = 0; i < sections.Count; i++)
        {
            ThrowIfCancellationRequested(shouldCancel);
            var (curve, profiles, existing, frame) = sections[i];
            if (i > 0)
                rowOffset += sections[i - 1].Frame.Below + (2.0 * textHeight) + frame.Above;
            Plane cellPlane = insertionPlane;
            cellPlane.Origin = insertionPlane.Origin - (insertionPlane.YAxis * rowOffset);

            string mark = SectionMark(i);
            if (analysis.IsEnabled && analysis.ShowPlanLabels)
            {
                outputCount += EmitPlanMarkPair(
                    analysis, build, layerRoles, PrimarySlice(profiles), mark,
                    curve.PointAtStart, -curve.TangentAtStart, curve.PointAtEnd, curve.TangentAtEnd,
                    textHeight);
            }

            SectionEmissionStats emitted = EmitCombinedProfileObjects(
                analysis,
                build,
                profiles,
                existing,
                frame,
                cellPlane,
                horizontalScale: 1.0,
                verticalScale: verticalScale,
                comparisonTolerance: tolerance,
                showBaseline: analysis.ShowBaseline,
                showElevationGrid: analysis.ShowElevationGrid,
                showStationTicks: analysis.ShowStationLabels,
                stationTickInterval: analysis.StationLabelInterval,
                showStationLabels: analysis.ShowStationLabels,
                stationLabelInterval: analysis.StationLabelInterval,
                textHeight: textHeight,
                layerRoles: layerRoles,
                sectionLabel: $"{analysis.Label} {mark}",
                hatchPatterns: snapshot.HatchPatterns,
                title: analysis.ShowSectionTitle ? SectionTitle(analysis, mark) : null);
            outputCount += emitted.OutputCount;
            cutRegions += emitted.CutRegions;
            fillRegions += emitted.FillRegions;
        }

        return new TerrainAnalysisSummary
        {
            AnalysisId = analysis.Id,
            SampleSourceCount = sourceCount,
            GeneratedOutputCount = outputCount,
            SectionTerrainCount = availableTerrainCount,
            SectionCutRegionCount = cutRegions,
            SectionFillRegionCount = fillRegions
        };
    }

    /// <summary>The drafting mark for the n-th section of a card: A, B, ... Z, AA, AB, ...</summary>
    internal static string SectionMark(int index)
    {
        var letters = new System.Text.StringBuilder();
        for (int n = index; n >= 0; n = (n / 26) - 1)
            letters.Insert(0, (char)('A' + (n % 26)));
        return letters.ToString();
    }

    /// <summary>"Section Cut A-A'": the card's name, so two cards' sections stay distinguishable, then the mark.</summary>
    private static string SectionTitle(TerrainSectionAnnotationDefinitionBase analysis, string mark) =>
        $"{analysis.Label} {mark}-{mark}'";

    /// <summary>
    /// The plan marks for one cut: <paramref name="mark"/> beyond its start and mark' beyond its end, each
    /// pushed one text height outward along the line so it does not sit on the terrain line it labels. They
    /// are placed at the terrain's height there, so they show over the terrain in a shaded view.
    /// </summary>
    private static int EmitPlanMarkPair(
        TerrainSectionAnnotationDefinitionBase analysis,
        TerrainBuildResult build,
        LayerRoleTable? layerRoles,
        TerrainSectionResult ownerSlice,
        string mark,
        Point3d start,
        Vector3d startOutward,
        Point3d end,
        Vector3d endOutward,
        double textHeight)
    {
        double th = Math.Max(textHeight, double.Epsilon);
        TerrainSectionSegment first = ownerSlice.Segments[0];
        TerrainSectionSegment last = ownerSlice.Segments[^1];
        build.AuxiliaryObjects.Add(BuildPlanMark(
            analysis, layerRoles, new Point3d(start.X, start.Y, first.Vertices[0].World.Z), startOutward, mark, th));
        build.AuxiliaryObjects.Add(BuildPlanMark(
            analysis, layerRoles, new Point3d(end.X, end.Y, last.Vertices[^1].World.Z), endOutward, mark + "'", th));
        return 2;
    }

    private static GeneratedRhinoObject BuildPlanMark(
        TerrainSectionAnnotationDefinitionBase analysis,
        LayerRoleTable? layerRoles,
        Point3d at,
        Vector3d outward,
        string text,
        double textHeight)
    {
        outward.Z = 0.0;
        if (!outward.Unitize())
            outward = Vector3d.XAxis;

        var label = new TextEntity
        {
            Plane = new Plane(at + (outward * textHeight), Vector3d.XAxis, Vector3d.YAxis),
            PlainText = text,
            TextHeight = textHeight,
            Justification = TextJustification.MiddleCenter,
            MaskFrame = DimensionStyle.MaskFrame.NoFrame
        };
        return BuildTextObject(analysis, label, layerRoles, $"{analysis.Label} plan {text}", LayerRole.SectionsLabels);
    }

    /// <summary>
    /// A cross-section's station in plan, written along its cut line just past one end. The end is chosen so
    /// the text reads left to right (or upwards) whichever way the alignment runs.
    /// </summary>
    private static GeneratedRhinoObject BuildPlanStationLabel(
        TerrainSectionAnnotationDefinitionBase analysis,
        LayerRoleTable? layerRoles,
        TerrainSectionResult ownerSlice,
        Point3d a,
        Point3d b,
        string text,
        double textHeight)
    {
        double th = Math.Max(textHeight, double.Epsilon);
        Vector3d direction = b - a;
        direction.Z = 0.0;
        if (!direction.Unitize())
            direction = Vector3d.XAxis;

        bool readsBackwards = direction.X < -1e-9 || (Math.Abs(direction.X) <= 1e-9 && direction.Y < 0.0);
        Point3d end = readsBackwards ? a : b;
        if (readsBackwards)
            direction = -direction;

        // The text sits beyond the end the reading direction leads to, at the terrain's height there.
        TerrainSectionVertex nearest = readsBackwards
            ? ownerSlice.Segments[0].Vertices[0]
            : ownerSlice.Segments[^1].Vertices[^1];
        var origin = new Point3d(end.X, end.Y, nearest.World.Z) + (direction * (0.5 * th));
        var label = new TextEntity
        {
            Plane = new Plane(origin, direction, Vector3d.CrossProduct(Vector3d.ZAxis, direction)),
            PlainText = text,
            TextHeight = th,
            Justification = TextJustification.MiddleLeft,
            MaskFrame = DimensionStyle.MaskFrame.NoFrame
        };
        return BuildTextObject(analysis, label, layerRoles, $"{analysis.Label} plan {text}", LayerRole.SectionsLabels);
    }

    /// <summary>
    /// What each line on the section is, in the order it is drawn on: this terrain, the ground it was
    /// compared to, then any other terrains. Null-coloured entries take their layer's colour, as their
    /// profiles do. A single profile needs no key.
    /// </summary>
    internal static IReadOnlyList<(string Text, LayerRole Role, int? ColorArgb)> DescribeProfileKey(
        TerrainSectionAnnotationDefinitionBase analysis,
        IReadOnlyList<(Guid TerrainId, string TerrainName, int ColorArgb, bool IsOwner, bool IsReference)> profiles,
        bool hasExistingGround)
    {
        var entries = new List<(string, LayerRole, int?)>();
        if (profiles.Count == 0)
            return entries;

        // Only this terrain is "proposed". A cut that misses it but crosses a comparison terrain still
        // draws that terrain, under its own name.
        foreach (var owner in profiles.Where(profile => profile.IsOwner))
            entries.Add(($"{owner.TerrainName} (proposed)", LayerRole.Sections, null));
        if (hasExistingGround)
        {
            // Name the existing ground after the terrain it came from; geometry or this terrain's own
            // initial triangulation have no name of their own.
            var reference = profiles.FirstOrDefault(profile => profile.IsReference);
            string text = !analysis.CutFillReference.HasReferences && reference.TerrainName != null
                ? $"{reference.TerrainName} (existing)"
                : "Existing ground";
            entries.Add((text, LayerRole.SectionsExisting, null));
        }

        foreach (var profile in profiles)
        {
            if (profile.IsOwner || profile.IsReference)
                continue;
            entries.Add((profile.TerrainName, LayerRole.Sections, profile.ColorArgb));
        }

        return entries.Count > 1 ? entries : Array.Empty<(string, LayerRole, int?)>();
    }

    /// <summary>
    /// The key beneath a section's title: one row per line on the drawing, a short sample of the line in its
    /// own role and colour, then its name. Stacked rather than run along one line, so no row depends on how
    /// wide the previous one's text turned out.
    /// </summary>
    private static int EmitProfileKey(
        TerrainSectionAnnotationDefinitionBase analysis,
        TerrainBuildResult build,
        LayerRoleTable? layerRoles,
        IReadOnlyList<SectionTerrainProfile> profiles,
        TerrainSectionResult? existingGround,
        Plane cellPlane,
        double horizontalScale,
        double verticalScale,
        double baseElevation,
        double firstRowElevation,
        double rowStep,
        double textHeight,
        string sectionLabel)
    {
        var entries = DescribeProfileKey(analysis, DescribeProfiles(profiles, existingGround), existingGround != null);
        double perStationUnit = 1.0 / Math.Max(horizontalScale, double.Epsilon);
        int emitted = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            var (text, role, colorArgb) = entries[i];
            double elevation = firstRowElevation - (i * rowStep);
            var sample = new Polyline
            {
                SectionLayoutHelper.ProjectToInsertionPlane(cellPlane, 0.0, elevation, horizontalScale, verticalScale, baseElevation),
                SectionLayoutHelper.ProjectToInsertionPlane(cellPlane, 2.0 * textHeight * perStationUnit, elevation, horizontalScale, verticalScale, baseElevation)
            };
            build.AuxiliaryObjects.Add(BuildPolylineObject(analysis, sample, layerRoles, $"{sectionLabel} key {text}", role, colorArgb));

            var label = SectionLayoutHelper.BuildLabel(
                cellPlane,
                2.5 * textHeight * perStationUnit,
                elevation,
                horizontalScale,
                verticalScale,
                baseElevation,
                text,
                textHeight,
                TextJustification.MiddleLeft);
            build.AuxiliaryObjects.Add(BuildTextObject(analysis, label, layerRoles, $"{sectionLabel} key {text} label", LayerRole.SectionsLabels));
            emitted += 2;
        }

        return emitted;
    }

    private static SectionEmissionStats EmitCombinedProfileObjects(
        TerrainSectionAnnotationDefinitionBase analysis,
        TerrainBuildResult build,
        IReadOnlyList<SectionTerrainProfile> profiles,
        TerrainSectionResult? existingGround,
        SectionFrame frame,
        Plane cellPlane,
        double horizontalScale,
        double verticalScale,
        double comparisonTolerance,
        bool showBaseline,
        bool showElevationGrid,
        bool showStationTicks,
        double stationTickInterval,
        bool showStationLabels,
        double stationLabelInterval,
        double textHeight,
        LayerRoleTable? layerRoles,
        string sectionLabel,
        HatchPatternSnapshot hatchPatterns,
        string? title)
    {
        if (!analysis.IsEnabled)
            return default;

        int emitted = 0;
        int cutRegions = 0;
        int fillRegions = 0;
        bool showElevationLabels = analysis.ShowElevationLabels;
        double totalStation = profiles.Max(profile => profile.Slice.TotalStationLength);
        double minimumElevation = frame.Minimum;
        double maximumElevation = frame.Maximum;
        double baseElevation = frame.Minimum;
        double effectiveElevationGridInterval = frame.GridSpacing;

        // Cut and fill are this terrain's against the existing ground, so a cut that misses this terrain
        // (and only crosses a comparison terrain) shades nothing.
        TerrainSectionResult? referenceSliceForProfile = existingGround;
        TerrainSectionResult? ownerSlice = profiles.FirstOrDefault(profile => profile.IsOwner)?.Slice;
        if (existingGround is { } referenceSlice && ownerSlice != null)
        {
            IReadOnlyList<SectionComparisonRegion> regions = SectionProfileComparison.Compare(
                ownerSlice,
                referenceSlice,
                Math.Max(comparisonTolerance, totalStation * 1e-10));
            foreach (SectionComparisonRegion region in regions)
            {
                bool isCut = region.IsCut;
                LayerRole regionRole = isCut ? LayerRole.SectionsCutFillCut : LayerRole.SectionsCutFillFill;
                LayerAppearance regionAppearance = Roles(layerRoles).Appearance(regionRole);
                string regionLayerPath = Roles(layerRoles).Path(regionRole);

                // Pattern, scale and rotation come from the role, so every section in a document
                // fills the same way and the office controls it from one place. The analysis's own
                // fields are only a fallback for a document whose template predates them.
                string? patternName = regionAppearance.HatchPatternName
                    ?? (isCut ? analysis.CutHatchPatternName : analysis.FillHatchPatternName);
                string defaultPatternName = isCut
                    ? HatchPatternService.DefaultCutPatternName
                    : HatchPatternService.DefaultFillPatternName;

                // A hatch, not a transparent mesh: a shaded mesh is a rendering artefact that does not
                // print and ignores the document hatch scale.
                IReadOnlyList<Hatch> regionHatches = BuildComparisonRegionHatch(
                    region,
                    cellPlane,
                    horizontalScale,
                    verticalScale,
                    baseElevation,
                    hatchPatterns.ResolveIndex(patternName, defaultPatternName),
                    hatchPatterns.ResolveScale(
                        patternName,
                        defaultPatternName,
                        regionAppearance.HatchScale,
                        textHeight),
                    regionAppearance.HatchRotationDegrees,
                    comparisonTolerance);
                if (regionHatches.Count == 0)
                    continue;
                foreach (Hatch regionHatch in regionHatches)
                {
                    build.AuxiliaryObjects.Add(new GeneratedRhinoObject
                    {
                        Role = regionRole,
                        Geometry = regionHatch,
                        Name = $"{sectionLabel} {(isCut ? "cut" : "fill")}",
                        AnalysisId = analysis.Id,
                        AppearanceSource = GeneratedAppearanceSource.Layer,
                        LayerPath = regionLayerPath,
                        DisplayOrder = SectionDisplayOrder.Fill
                    });
                    emitted++;
                }

                // Region counts stay per comparison region: one region may need several hatches.
                if (isCut)
                    cutRegions++;
                else
                    fillRegions++;
            }
        }

        // Existing ground, drawn from whatever the cut/fill comparison measured against. It is context:
        // a light line the proposed profile is read against. Without it the original ground is only ever
        // implied by the far edge of a hatch, so it vanishes wherever nothing was cut or filled.
        if (referenceSliceForProfile != null)
        {
            foreach (Polyline existing in SectionLayoutHelper.LayoutFlatAll(
                         referenceSliceForProfile, cellPlane, horizontalScale, verticalScale, baseElevation))
            {
                if (existing.Count < 2)
                    continue;

                build.AuxiliaryObjects.Add(BuildPolylineObject(
                    analysis,
                    existing,
                    layerRoles,
                    $"{sectionLabel} existing ground",
                    LayerRole.SectionsExisting,
                    colorArgbOverride: null));
                emitted++;
            }
        }

        for (int profileIndex = 0; profileIndex < profiles.Count; profileIndex++)
        {
            SectionTerrainProfile profile = profiles[profileIndex];

            // The sectioned terrain itself — the finished modifier stack — is the subject of the drawing,
            // so it is the heaviest line on it and takes its appearance from the layer: black, by
            // convention, rather than the terrain's preview tint, which is a screen colour and prints as
            // whatever pastel it happens to be. Additional comparison terrains keep their own colours,
            // which is the only thing telling them apart.
            bool isOwnerProfile = profile.IsOwner;

            // A comparison terrain that is also the cut/fill reference has already been drawn above as
            // existing ground, from this same slice; drawing it again stacks a second, coloured line on it.
            if (!isOwnerProfile && ReferenceEquals(profile.Slice, referenceSliceForProfile))
                continue;

            foreach (Polyline poly in SectionLayoutHelper.LayoutFlatAll(
                         profile.Slice, cellPlane, horizontalScale, verticalScale, baseElevation))
            {
                if (poly.Count < 2)
                    continue;

                build.AuxiliaryObjects.Add(BuildPolylineObject(
                    analysis,
                    poly,
                    layerRoles,
                    $"{sectionLabel} {profile.TerrainName}",
                    LayerRole.Sections,
                    isOwnerProfile ? null : profile.ColorArgb));
                emitted++;
            }
        }

        // Everything below is laid out in drawing units, then converted: an offset written in elevation
        // units would be stretched by the vertical exaggeration, which put station labels fifteen text
        // heights under a 10x section.
        double th = Math.Max(textHeight, double.Epsilon);
        double perDrawingUnit = 1.0 / Math.Max(verticalScale, double.Epsilon);
        double perStationUnit = 1.0 / Math.Max(horizontalScale, double.Epsilon);

        // The elevation axis spans whole grid steps, so the grid and the elevation figures end on round
        // values; the foot of the drawing drops to the lowest of them (measured in MeasureSection).
        double axisBottom = frame.AxisBottom;
        double axisTop = frame.AxisTop;

        if (showBaseline && totalStation > 0.0)
        {
            var baseline = SectionLayoutHelper.BuildBaselineAxis(cellPlane, totalStation, horizontalScale, verticalScale, axisBottom, baseElevation);
            build.AuxiliaryObjects.Add(BuildLineObject(analysis, baseline, layerRoles, $"{sectionLabel} baseline", LayerRole.SectionsGrid));
            emitted++;
        }

        if (showElevationGrid && effectiveElevationGridInterval > 0.0 && totalStation > 0.0)
        {
            var grid = SectionLayoutHelper.BuildElevationGridLines(cellPlane, totalStation, minimumElevation, maximumElevation, baseElevation, effectiveElevationGridInterval, horizontalScale, verticalScale);
            foreach (var line in grid)
            {
                build.AuxiliaryObjects.Add(BuildLineObject(analysis, line, layerRoles, $"{sectionLabel} grid", LayerRole.SectionsGrid));
                emitted++;
            }
        }

        if (showElevationLabels && effectiveElevationGridInterval > 0.0 && totalStation > 0.0)
        {
            var axis = SectionLayoutHelper.BuildElevationAxis(cellPlane, axisBottom, axisTop, horizontalScale, verticalScale, baseElevation);
            build.AuxiliaryObjects.Add(BuildLineObject(analysis, axis, layerRoles, $"{sectionLabel} elevation axis", LayerRole.SectionsGrid));
            emitted++;

            // Figures on every grid step pile into each other when a step is shorter than the text is tall
            // (1 m steps, 1 m text, no exaggeration). Label every n-th step instead, n chosen so figures
            // stand at least 1.8 text heights apart; the grid itself keeps every step.
            int labelEvery = SectionLayoutHelper.ElevationLabelStride(effectiveElevationGridInterval * verticalScale, th);
            string format = SectionLayoutHelper.ElevationLabelFormat(effectiveElevationGridInterval * labelEvery);
            foreach (double elevation in SectionLayoutHelper.ElevationSteps(minimumElevation, maximumElevation, effectiveElevationGridInterval))
            {
                if (Math.Round(elevation / effectiveElevationGridInterval) % labelEvery != 0)
                    continue;

                Point3d tickEnd = SectionLayoutHelper.ProjectToInsertionPlane(cellPlane, 0.0, elevation, horizontalScale, verticalScale, baseElevation);
                Point3d tickStart = SectionLayoutHelper.ProjectToInsertionPlane(cellPlane, -0.4 * th * perStationUnit, elevation, horizontalScale, verticalScale, baseElevation);
                build.AuxiliaryObjects.Add(BuildLineObject(analysis, new Line(tickStart, tickEnd), layerRoles, $"{sectionLabel} elevation tick", LayerRole.SectionsTicks));

                string text = elevation.ToString(format);
                var label = SectionLayoutHelper.BuildLabel(
                    cellPlane,
                    -0.7 * th * perStationUnit,
                    elevation,
                    horizontalScale,
                    verticalScale,
                    baseElevation,
                    text,
                    th,
                    TextJustification.MiddleRight);
                build.AuxiliaryObjects.Add(BuildTextObject(analysis, label, layerRoles, $"{sectionLabel} elevation {text}", LayerRole.SectionsLabels));
                emitted += 2;
            }
        }

        if (showStationTicks && stationTickInterval > 0.0 && totalStation > 0.0)
        {
            var stations = BuildStationList(totalStation, stationTickInterval);
            var ticks = SectionLayoutHelper.BuildStationTicks(cellPlane, stations, th * perDrawingUnit, horizontalScale, verticalScale, baseElevation, axisBottom);
            foreach (var line in ticks)
            {
                build.AuxiliaryObjects.Add(BuildLineObject(analysis, line, layerRoles, $"{sectionLabel} tick", LayerRole.SectionsTicks));
                emitted++;
            }
        }

        double belowAxis = 0.0;
        if (showStationLabels)
        {
            double labelInterval = stationLabelInterval > 0.0 ? stationLabelInterval : totalStation * 0.25;
            var stations = BuildStationList(totalStation, labelInterval);
            belowAxis = 2.0 * th;
            foreach (double station in stations)
            {
                var label = SectionLayoutHelper.BuildLabel(
                    cellPlane,
                    station,
                    axisBottom - (1.5 * th * perDrawingUnit),
                    horizontalScale,
                    verticalScale,
                    baseElevation,
                    station.ToString("F1"),
                    th);
                build.AuxiliaryObjects.Add(BuildTextObject(analysis, label, layerRoles, $"{sectionLabel} {station:F1}", LayerRole.SectionsLabels));
                emitted++;
            }
        }

        if (!string.IsNullOrWhiteSpace(title) && totalStation > 0.0)
        {
            var titleText = SectionLayoutHelper.BuildLabel(
                cellPlane,
                totalStation * 0.5,
                axisBottom - ((belowAxis + (2.0 * th)) * perDrawingUnit),
                horizontalScale,
                verticalScale,
                baseElevation,
                title,
                th);
            build.AuxiliaryObjects.Add(BuildTextObject(analysis, titleText, layerRoles, $"{sectionLabel} title", LayerRole.SectionsLabels));
            emitted++;

            // The key reads with the title, so it follows the title's switch.
            emitted += EmitProfileKey(
                analysis, build, layerRoles, profiles, referenceSliceForProfile, cellPlane,
                horizontalScale, verticalScale, baseElevation,
                firstRowElevation: axisBottom - ((belowAxis + (3.5 * th)) * perDrawingUnit),
                rowStep: 1.5 * th * perDrawingUnit,
                textHeight: th,
                sectionLabel);
        }

        return new SectionEmissionStats(emitted, cutRegions, fillRegions);
    }

    /// <summary>
    /// How the terrain was cut for one section cell, so an arbitrary reference mesh can be cut the same
    /// way. A polyline cut and a sampled-along-curve cut produce different station parametrizations, and
    /// comparing profiles built two different ways would misreport every depth.
    /// </summary>
    private readonly record struct SectionCutGeometry(
        IReadOnlyList<Point3d>? CutVertices,
        Curve? SampledCurve,
        double SampleInterval)
    {
        public static SectionCutGeometry AlongPolyline(IReadOnlyList<Point3d> cutVertices) =>
            new(cutVertices, null, 0.0);

        public static SectionCutGeometry AlongCurve(Curve curve, double sampleInterval) =>
            new(null, curve, sampleInterval);

        /// <summary>Cuts a mesh exactly as the terrain was cut. Null when the mesh misses the cut.</summary>
        public TerrainSectionResult? Slice(RhinoMesh mesh, double tolerance)
        {
            TerrainSectionResult slice = SampledCurve != null
                ? TerrainSectionSlicer.SampleAlongCurve(mesh, SampledCurve, SampleInterval, tolerance)
                : TerrainSectionSlicer.SliceAlongPolyline(mesh, CutVertices!, tolerance);
            return slice.IsEmpty ? null : slice;
        }
    }

    /// <summary>
    /// The existing-ground profile to shade cut and fill against: explicitly referenced Rhino geometry
    /// first, then another MoleHill terrain. Returns null — with a diagnostic saying why — when cut/fill
    /// is switched on but nothing usable is configured, which used to fail silently and read as "the hatch
    /// does not work".
    /// </summary>
    private static TerrainSectionResult? ResolveCutFillReferenceSlice(
        TerrainBuildSnapshot snapshot,
        SectionCutGeometry cutGeometry,
        TerrainSectionAnnotationDefinitionBase analysis,
        IReadOnlyList<SectionTerrainProfile> profiles,
        double tolerance,
        TerrainBuildResult build,
        RhinoMesh? baseMesh,
        CutFillReferenceMesh referenceMesh)
    {
        if (analysis.CutFillReference.HasReferences)
        {
            RhinoMesh? combined = referenceMesh.Get(snapshot, analysis);
            if (combined == null)
            {
                build.Diagnostics.Add(
                    $"{analysis.Label}: cut/fill reference resolved no mesh geometry; no cut or fill was shaded.");
                return null;
            }

            TerrainSectionResult? slice = cutGeometry.Slice(combined, tolerance);
            if (slice == null)
            {
                build.Diagnostics.Add(
                    $"{analysis.Label}: the cut/fill reference does not reach this section line; no cut or fill was shaded.");
            }

            return slice;
        }

        if (analysis.CutFillReferenceTerrainId.HasValue)
        {
            SectionTerrainProfile? referenceProfile = profiles.FirstOrDefault(
                profile => profile.TerrainId == analysis.CutFillReferenceTerrainId.Value);
            if (referenceProfile != null)
                return referenceProfile.Slice;

            build.Diagnostics.Add(
                $"{analysis.Label}: the reference terrain has no profile on this section line; no cut or fill was shaded.");
            return null;
        }

        // No explicit reference: compare against this terrain's own initial triangulation — the ground as
        // it was before any modifier moved it. That is what "how much cut and fill did my grading do"
        // means, and it is the overwhelmingly common question; requiring a second terrain to ask it made
        // the feature unreachable for the case it exists to serve. An explicit reference still wins, for
        // comparing against surveyed ground that is not this terrain's own starting point.
        if (baseMesh == null)
        {
            build.Diagnostics.Add(
                $"{analysis.Label}: cut/fill shading is on but this terrain has no base triangulation to " +
                "compare against, and no reference is set.");
            return null;
        }

        TerrainSectionResult? baseSlice = cutGeometry.Slice(baseMesh, tolerance);
        if (baseSlice == null)
        {
            build.Diagnostics.Add(
                $"{analysis.Label}: the terrain's initial triangulation does not reach this section line; " +
                "no cut or fill was shaded.");
        }

        return baseSlice;
    }

    /// <summary>
    /// An annotation's explicit cut/fill reference as one mesh, resolved on first use and then shared by
    /// every section cell. Resolving meshes a Brep reference and welds a multi-object one, and neither
    /// result depends on the cell, so doing it per cell repeated the whole cost for every section drawn.
    /// </summary>
    private sealed class CutFillReferenceMesh
    {
        private bool _resolved;
        private RhinoMesh? _mesh;

        /// <summary>The combined reference, or null when it resolves to no mesh geometry.</summary>
        public RhinoMesh? Get(TerrainBuildSnapshot snapshot, TerrainSectionAnnotationDefinitionBase analysis)
        {
            if (_resolved)
                return _mesh;

            _resolved = true;
            var meshes = TerrainBuildSnapshotResolver.ResolveMeshes(snapshot, analysis.CutFillReference);
            _mesh = meshes.Count switch
            {
                0 => null,
                1 => meshes[0],
                _ => CombineMeshes(meshes),
            };
            return _mesh;
        }

        private static RhinoMesh CombineMeshes(IReadOnlyList<RhinoMesh> meshes)
        {
            var combined = new RhinoMesh();
            foreach (RhinoMesh mesh in meshes)
                combined.Append(mesh);
            RhinoGeometryConversions.NormalizeMeshInPlace(combined);
            return combined;
        }
    }

    private static List<SectionTerrainProfile> SliceTerrainsAlongPolyline(
        TerrainBuildSnapshot snapshot,
        RhinoMesh ownerMesh,
        TerrainSectionAnnotationDefinitionBase analysis,
        IReadOnlyList<Point3d> cutVertices,
        double tolerance)
    {
        var profiles = new List<SectionTerrainProfile>();
        TerrainSectionResult ownerSlice = TerrainSectionSlicer.SliceAlongPolyline(ownerMesh, cutVertices, tolerance);
        if (!ownerSlice.IsEmpty)
        {
            profiles.Add(new SectionTerrainProfile(
                snapshot.Terrain.TerrainId,
                snapshot.Terrain.Name,
                analysis.ColorArgb ?? snapshot.Terrain.TerrainColorArgb,
                ownerSlice,
                IsOwner: true));
        }

        foreach (Guid terrainId in analysis.ComparisonTerrainIds)
        {
            if (!snapshot.SectionTerrains.TryGetValue(terrainId, out TerrainSectionReferenceSnapshot? terrain))
                continue;
            TerrainSectionResult slice = TerrainSectionSlicer.SliceAlongPolyline(terrain.Mesh, cutVertices, tolerance);
            if (slice.IsEmpty)
                continue;
            profiles.Add(new SectionTerrainProfile(
                terrain.TerrainId,
                terrain.Name,
                analysis.ResolveProfileColorArgb(terrain.TerrainId, terrain.ColorArgb),
                slice,
                IsOwner: false));
        }

        return profiles;
    }

    private static List<SectionTerrainProfile> SampleTerrainsAlongCurve(
        TerrainBuildSnapshot snapshot,
        RhinoMesh ownerMesh,
        TerrainSectionAnnotationDefinitionBase analysis,
        Curve curve,
        double sampleInterval,
        double tolerance)
    {
        var profiles = new List<SectionTerrainProfile>();
        TerrainSectionResult ownerSlice = TerrainSectionSlicer.SampleAlongCurve(
            ownerMesh, curve, sampleInterval, tolerance);
        if (!ownerSlice.IsEmpty)
        {
            profiles.Add(new SectionTerrainProfile(
                snapshot.Terrain.TerrainId,
                snapshot.Terrain.Name,
                analysis.ColorArgb ?? snapshot.Terrain.TerrainColorArgb,
                ownerSlice,
                IsOwner: true));
        }

        foreach (Guid terrainId in analysis.ComparisonTerrainIds)
        {
            if (!snapshot.SectionTerrains.TryGetValue(terrainId, out TerrainSectionReferenceSnapshot? terrain))
                continue;
            TerrainSectionResult slice = TerrainSectionSlicer.SampleAlongCurve(
                terrain.Mesh, curve, sampleInterval, tolerance);
            if (slice.IsEmpty)
                continue;
            profiles.Add(new SectionTerrainProfile(
                terrain.TerrainId,
                terrain.Name,
                analysis.ResolveProfileColorArgb(terrain.TerrainId, terrain.ColorArgb),
                slice,
                IsOwner: false));
        }

        return profiles;
    }

    private static void AddMissingSectionTerrainDiagnostics(
        TerrainBuildSnapshot snapshot,
        TerrainSectionAnnotationDefinitionBase analysis,
        TerrainBuildResult build)
    {
        foreach (Guid terrainId in analysis.ComparisonTerrainIds.Distinct())
        {
            if (snapshot.SectionTerrains.ContainsKey(terrainId))
                continue;
            build.Diagnostics.Add(
                $"{analysis.Label}: comparison terrain {terrainId} has no completed final mesh; its profile was skipped.");
        }
    }

    /// <summary>
    /// Builds the closed boundary of a cut/fill region and returns it as a hatch. The region is a ribbon
    /// between the proposed and reference profiles, so its outline is the proposed elevations forward then
    /// the reference elevations back. Regions are already split at profile crossings by
    /// <see cref="SectionProfileComparison"/>, so the loop does not self-intersect.
    /// </summary>
    private static IReadOnlyList<Hatch> BuildComparisonRegionHatch(
        SectionComparisonRegion region,
        Plane cellPlane,
        double horizontalScale,
        double verticalScale,
        double baseElevation,
        int hatchPatternIndex,
        double hatchScale,
        double hatchRotationDegrees,
        double tolerance)
    {
        if (region.Vertices.Count < 2)
            return Array.Empty<Hatch>();

        var loop = new List<Point3d>(region.Vertices.Count * 2 + 1);
        for (int i = 0; i < region.Vertices.Count; i++)
        {
            SectionComparisonVertex vertex = region.Vertices[i];
            AppendDistinct(loop, SectionLayoutHelper.ProjectToInsertionPlane(
                cellPlane, vertex.Station, vertex.ProposedElevation, horizontalScale, verticalScale, baseElevation), tolerance);
        }

        for (int i = region.Vertices.Count - 1; i >= 0; i--)
        {
            SectionComparisonVertex vertex = region.Vertices[i];
            AppendDistinct(loop, SectionLayoutHelper.ProjectToInsertionPlane(
                cellPlane, vertex.Station, vertex.ReferenceElevation, horizontalScale, verticalScale, baseElevation), tolerance);
        }

        // A hatch boundary needs three distinct corners; anything less encloses no area.
        if (loop.Count < 3)
            return Array.Empty<Hatch>();

        loop.Add(loop[0]);
        var boundary = new PolylineCurve(loop);
        Hatch[]? hatches = Hatch.Create(
            boundary,
            Math.Max(hatchPatternIndex, 0),
            RhinoMath.ToRadians(hatchRotationDegrees),
            hatchScale > 0.0 ? hatchScale : 1.0,
            Math.Max(tolerance, RhinoMath.ZeroTolerance));

        // Hatch.Create can split one boundary into several hatches; keeping only the first would silently
        // drop part of the filled region.
        return hatches is { Length: > 0 }
            ? hatches.Where(hatch => hatch != null).ToList()
            : (IReadOnlyList<Hatch>)Array.Empty<Hatch>();
    }

    private static void AppendDistinct(List<Point3d> points, Point3d candidate, double tolerance)
    {
        double thresholdSquared = Math.Max(tolerance, RhinoMath.ZeroTolerance);
        thresholdSquared *= thresholdSquared;
        if (points.Count > 0 && points[^1].DistanceToSquared(candidate) <= thresholdSquared)
            return;

        points.Add(candidate);
    }
}
